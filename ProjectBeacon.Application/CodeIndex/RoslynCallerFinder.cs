namespace ProjectBeacon.Application.CodeIndex;

using Application.Common;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

public sealed class RoslynCallerFinder : ICallerFinder
{
    private static bool _msbuildRegistered;
    private static readonly object _lock = new();

    internal static Func<Result>? MsBuildRegistrationOverride;
    internal static Func<ISymbol, Solution, IEnumerable<ReferencedSymbol>>? ReferenceLookupOverride;

    private const int MaxSnippetLength = 200;

    public string Id => "roslyn";

    public Result<CallerResult> Find(CallerScope scope)
    {
        try
        {
            return FindInternal(scope);
        }
        catch (Exception ex)
        {
            return Result.Failure<CallerResult>("roslyn backend failed: " + ex.Message);
        }
    }

    private Result<CallerResult> FindInternal(CallerScope scope)
    {
        var msbuild = EnsureMsBuild();
        if (!msbuild.Success)
            return Result.Failure<CallerResult>(msbuild.Error ?? "msbuild is not available");

        var targetPath = Path.GetFullPath(
            Path.Combine(scope.Root, scope.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(targetPath))
            return Result.Failure<CallerResult>("file not found");

        var candidates = CandidateProjects(scope.Root, targetPath);
        if (candidates.Count == 0)
            return Result.Failure<CallerResult>("no .sln or .csproj found");

        string? lastOpenError = null;

        foreach (var projectFile in candidates)
        {
            using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>());
            Solution solution;
            try
            {
                if (projectFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    solution = workspace.OpenSolutionAsync(projectFile).GetAwaiter().GetResult();
                }
                else
                {
                    var project = workspace.OpenProjectAsync(projectFile).GetAwaiter().GetResult();
                    if (project is null)
                    {
                        lastOpenError = Path.GetFileName(projectFile) + ": failed to open project";
                        continue;
                    }
                    solution = project.Solution;
                }
            }
            catch (Exception ex)
            {
                lastOpenError = Path.GetFileName(projectFile) + ": " + ex.Message;
                continue;
            }

            var document = FindDocument(solution, targetPath);
            if (document is null)
                continue;

            var solutionBuilds = !workspace.Diagnostics.Any(d => d.Kind == WorkspaceDiagnosticKind.Failure);

            var root = document.GetSyntaxRootAsync().GetAwaiter().GetResult();
            if (root is null)
                return Result.Failure<CallerResult>("failed to get syntax root");

            var model = document.GetSemanticModelAsync().GetAwaiter().GetResult();
            if (model is null)
                return Result.Failure<CallerResult>("failed to get semantic model");
            var text = document.GetTextAsync().GetAwaiter().GetResult();

            if (scope.Line < 1 || scope.Line > text.Lines.Count)
                return Result.Failure<CallerResult>("line out of range");

            var lineSpan = text.Lines[scope.Line - 1].Span;
            var node = root.FindNode(lineSpan);

            ISymbol? symbol = null;
            foreach (var ancestor in node.AncestorsAndSelf())
            {
                symbol = model.GetDeclaredSymbol(ancestor);
                if (symbol is not null)
                    break;
            }

            if (symbol is null)
                symbol = model.GetSymbolInfo(node).Symbol;

            if (symbol is null)
                return Result.Failure<CallerResult>("no symbol found at line " + scope.Line);

            if (!string.Equals(symbol.Name, scope.SymbolName, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<CallerResult>(
                    "symbol at line " + scope.Line + " is '" + symbol.Name + "', not '" + scope.SymbolName + "'");

            IEnumerable<ReferencedSymbol> referencedSymbols;
            try
            {
                referencedSymbols = ReferenceLookupOverride is not null
                    ? ReferenceLookupOverride(symbol, solution)
                    : SymbolFinder.FindReferencesAsync(symbol, solution).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return Result.Failure<CallerResult>("reference lookup failed: " + ex.Message);
            }

            var callers = new List<CallerSite>();

            foreach (var refSym in referencedSymbols)
            {
                foreach (var refLoc in refSym.Locations)
                {
                    if (refLoc.IsImplicit)
                        continue;

                    var location = refLoc.Location;
                    var sourceFile = location.SourceTree?.FilePath;
                    if (sourceFile is null)
                        continue;

                    var refLine = location.GetLineSpan().StartLinePosition.Line + 1;

                    if (string.Equals(Path.GetFullPath(sourceFile), targetPath, StringComparison.OrdinalIgnoreCase)
                        && refLine == scope.Line)
                        continue;

                    var refRoot = refLoc.Document.GetSyntaxRootAsync().GetAwaiter().GetResult();
                    var snippet = ExtractSnippet(refRoot, location.SourceSpan);
                    var enclosing = FindEnclosingSymbol(refRoot, location.SourceSpan);
                    var relativePath = ToRelative(scope.Root, sourceFile);

                    callers.Add(new CallerSite(relativePath, refLine, snippet, enclosing));
                }
            }

            return Result.Ok(new CallerResult(scope.SymbolName, callers, Id, solutionBuilds));
        }

        return Result.Failure<CallerResult>(
            lastOpenError is not null
                ? "failed to open project: " + lastOpenError
                : "document not found in any candidate project or solution");
    }

    private static Result EnsureMsBuild()
    {
        if (MsBuildRegistrationOverride is not null)
            return MsBuildRegistrationOverride();
        lock (_lock)
        {
            if (_msbuildRegistered)
                return Result.Ok();
            try
            {
                MSBuildLocator.RegisterDefaults();
                _msbuildRegistered = true;
                return Result.Ok();
            }
            catch (Exception ex)
            {
                return Result.Failure("msbuild is not available: " + ex.Message);
            }
        }
    }

    internal static IReadOnlyList<string> CandidateProjects(string root, string targetPath)
    {
        var fullRoot = Path.GetFullPath(root);
        var dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        var result = new List<string>();
        while (dir is not null
            && (dir == fullRoot
                || dir.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            result.AddRange(SafeFiles(dir, "*.csproj"));
            result.AddRange(SafeFiles(dir, "*.sln"));
            dir = Path.GetDirectoryName(dir);
        }
        return result;
    }

    private static Document? FindDocument(Solution solution, string targetPath)
    {
        return solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(
                Path.GetFullPath(d.Name), targetPath, StringComparison.OrdinalIgnoreCase))
            ?? solution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => string.Equals(
                    Path.GetFileName(d.Name), Path.GetFileName(targetPath), StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> SafeFiles(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string ExtractSnippet(SyntaxNode? root, TextSpan span)
    {
        if (root is null)
            return "";
        var text = root.GetText().ToString(span);
        text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (text.Contains("  "))
            text = text.Replace("  ", " ");
        if (text.Length > MaxSnippetLength)
            text = text[..MaxSnippetLength] + "…";
        return text;
    }

    private static string? FindEnclosingSymbol(SyntaxNode? root, TextSpan span)
    {
        if (root is null)
            return null;

        var node = root.FindNode(span);
        string? methodName = null;
        string? typeName = null;

        foreach (var ancestor in node.AncestorsAndSelf())
        {
            if (methodName is null)
            {
                if (ancestor is MethodDeclarationSyntax method)
                    methodName = method.Identifier.Text;
                else if (ancestor is PropertyDeclarationSyntax prop)
                    methodName = prop.Identifier.Text;
                else if (ancestor is LocalFunctionStatementSyntax local)
                    methodName = local.Identifier.Text;
                else if (ancestor is ConstructorDeclarationSyntax ctor)
                    methodName = ctor.Identifier.Text + " (constructor)";
            }
            if (typeName is null && ancestor is TypeDeclarationSyntax type)
                typeName = type.Identifier.Text;
        }

        if (methodName is null && typeName is null)
            return null;
        if (methodName is not null && typeName is not null)
            return typeName + "." + methodName;
        return methodName ?? typeName;
    }

    private static string ToRelative(string root, string fullPath)
        => Path.GetRelativePath(root, fullPath).Replace('\\', '/');
}
