namespace ProjectBeacon.Application.Tests;

using Application.CodeIndex;
using Application.Common;

public sealed class CallerTests
{
    [Fact]
    public void GetCallers_CSharp_NoProject_DegradesToHeuristic()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "Library.cs"),
                "namespace Demo;\n" +
                "\n" +
                "public static class Library\n" +
                "{\n" +
                "    public static int Add(int a, int b) => a + b;\n" +
                "}\n");
            File.WriteAllText(Path.Combine(root, "Caller1.cs"),
                "namespace Demo;\n" +
                "\n" +
                "public class Caller1\n" +
                "{\n" +
                "    public int Use(int x)\n" +
                "    {\n" +
                "        var r = Library.Add(x, 1);\n" +
                "        return r;\n" +
                "    }\n" +
                "}\n");

            var index = new CodeIndex(root);
            var result = index.GetCallers("Library.cs", "Add", 5);

            Assert.True(result.Success, result.Error);
            var v = result.Value!;
            Assert.Equal("heuristic", v.Backend);
            Assert.False(v.SolutionBuilds);
            Assert.Single(v.Callers);
            Assert.Equal("Caller1.cs", v.Callers[0].Path);
            Assert.Equal(7, v.Callers[0].Line);
            Assert.Contains("Add", v.Callers[0].Snippet);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetCallers_TypeScript_Heuristic_FindsCallers()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "app.ts"),
                "function greet(name: string): string {\n" +
                "    return \"hello \" + name;\n" +
                "}\n");
            File.WriteAllText(Path.Combine(root, "main.ts"),
                "import { greet } from \"./app\";\n" +
                "\n" +
                "export function run() {\n" +
                "    const msg = greet(\"world\");\n" +
                "    console.log(msg);\n" +
                "}\n");

            var index = new CodeIndex(root);
            var result = index.GetCallers("app.ts", "greet", 1);

            Assert.True(result.Success, result.Error);
            var v = result.Value!;
            Assert.Equal("heuristic", v.Backend);
            Assert.Null(v.SolutionBuilds);
            var caller = v.Callers.FirstOrDefault(c => c.Path == "main.ts" && c.Line == 4);
            Assert.NotNull(caller);
            Assert.Contains("greet", caller!.Snippet);
            Assert.Equal("run", caller.Symbol);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetCallers_Python_Heuristic_FindsCallers()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "utils.py"),
                "def greet(name):\n" +
                "    return \"hello \" + name\n");
            File.WriteAllText(Path.Combine(root, "main.py"),
                "from utils import greet\n" +
                "\n" +
                "def run():\n" +
                "    msg = greet(\"world\")\n" +
                "    print(msg)\n");

            var index = new CodeIndex(root);
            var result = index.GetCallers("utils.py", "greet", 1);

            Assert.True(result.Success, result.Error);
            var v = result.Value!;
            Assert.Equal("heuristic", v.Backend);
            Assert.Null(v.SolutionBuilds);
            var caller = v.Callers.FirstOrDefault(c => c.Path == "main.py" && c.Line == 4);
            Assert.NotNull(caller);
            Assert.Contains("greet", caller!.Snippet);
            Assert.Equal("run", caller.Symbol);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetCallers_NoCallers_ReturnsEmptyList()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "standalone.ts"),
                "function unused() {\n" +
                "    return 42;\n" +
                "}\n");

            var index = new CodeIndex(root);
            var result = index.GetCallers("standalone.ts", "unused", 1);

            Assert.True(result.Success, result.Error);
            var v = result.Value!;
            Assert.Empty(v.Callers);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetCallers_MissingFile_Fails()
    {
        var root = MakeWorkspace();
        try
        {
            var index = new CodeIndex(root);
            var result = index.GetCallers("nonexistent.ts", "foo", 1);

            Assert.False(result.Success);
            Assert.Contains("file not found", result.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetCallers_EmptySymbol_Fails()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "file.ts"), "function foo() {}\n");

            var index = new CodeIndex(root);
            var result = index.GetCallers("file.ts", "", 1);

            Assert.False(result.Success);
            Assert.Contains("missing symbol name", result.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetCallers_LineBelowOne_Fails()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "file.ts"), "function foo() {}\n");

            var index = new CodeIndex(root);
            var result = index.GetCallers("file.ts", "foo", 0);

            Assert.False(result.Success);
            Assert.Contains("line must be >= 1", result.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetCallers_CSharp_WithProject_RoslynBackend()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "demo.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                "  <PropertyGroup>\n" +
                "    <TargetFramework>net9.0</TargetFramework>\n" +
                "    <ImplicitUsings>enable</ImplicitUsings>\n" +
                "    <Nullable>enable</Nullable>\n" +
                "  </PropertyGroup>\n" +
                "</Project>\n");
            File.WriteAllText(Path.Combine(root, "Calculator.cs"),
                "namespace Demo;\n" +
                "\n" +
                "public static class Calculator\n" +
                "{\n" +
                "    public static int Add(int a, int b) => a + b;\n" +
                "}\n");
            File.WriteAllText(Path.Combine(root, "Caller1.cs"),
                "namespace Demo;\n" +
                "\n" +
                "public class Caller1\n" +
                "{\n" +
                "    public int Use(int x)\n" +
                "    {\n" +
                "        var result = Calculator.Add(x, 1);\n" +
                "        return result;\n" +
                "    }\n" +
                "}\n");

            var index = new CodeIndex(root);
            var result = index.GetCallers("Calculator.cs", "Add", 5);

            Assert.True(result.Success, result.Error);
            var v = result.Value!;
            Assert.Equal("roslyn", v.Backend);
            Assert.True(v.SolutionBuilds);
            var caller = v.Callers.FirstOrDefault(c => c.Path == "Caller1.cs" && c.Line == 7);
            Assert.NotNull(caller);
            Assert.Contains("Add", caller!.Snippet);
            Assert.Equal("Caller1.Use", caller.Symbol);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetCallers_Roslyn_MsBuildRegistrationFails_ReturnsFailureWithoutThrowing()
    {
        var root = MakeWorkspace();
        try
        {
            RoslynCallerFinder.MsBuildRegistrationOverride = () => Result.Failure("simulated msbuild registration failure");
            var finder = new RoslynCallerFinder();

            var result = finder.Find(new CallerScope(root, "Calculator.cs", "Add", 5, []));

            Assert.False(result.Success);
            Assert.Contains("msbuild", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            RoslynCallerFinder.MsBuildRegistrationOverride = null;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetCallers_Roslyn_BrokenSolutionFile_ReturnsFailure()
    {
        var root = MakeWorkspace();
        try
        {
            WriteCSharpSources(root);
            File.WriteAllText(Path.Combine(root, "bad.sln"), "this is definitely not a solution file\n");

            var finder = new RoslynCallerFinder();
            var result = finder.Find(new CallerScope(root, "Calculator.cs", "Add", 5, []));

            Assert.False(result.Success);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetCallers_Roslyn_ReferenceLookupFails_ReturnsFailure()
    {
        var root = MakeWorkspace();
        try
        {
            WriteCSharpProject(root);
            RoslynCallerFinder.ReferenceLookupOverride = (_, _) => throw new InvalidOperationException("simulated lookup failure");
            var finder = new RoslynCallerFinder();

            var result = finder.Find(new CallerScope(root, "Calculator.cs", "Add", 5, []));

            Assert.False(result.Success);
            Assert.Contains("reference lookup failed", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            RoslynCallerFinder.ReferenceLookupOverride = null;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetCallers_RoslynFailure_FallsBackToHeuristic()
    {
        var root = MakeWorkspace();
        try
        {
            WriteCSharpProject(root);
            RoslynCallerFinder.MsBuildRegistrationOverride = () => Result.Failure("simulated msbuild registration failure");

            var index = new CodeIndex(root);
            var result = index.GetCallers("Calculator.cs", "Add", 5);

            Assert.True(result.Success, result.Error);
            var v = result.Value!;
            Assert.Equal("heuristic", v.Backend);
            Assert.False(v.SolutionBuilds);
            var caller = v.Callers.FirstOrDefault(c => c.Path == "Caller1.cs" && c.Line == 7);
            Assert.NotNull(caller);
            Assert.Contains("Add", caller!.Snippet);
        }
        finally
        {
            RoslynCallerFinder.MsBuildRegistrationOverride = null;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetCallers_Roslyn_BrokenSolution_SolutionBuildsFalse()
    {
        var root = MakeWorkspace();
        try
        {
            WriteCSharpSources(root);
            Directory.CreateDirectory(Path.Combine(root, "proj"));
            File.WriteAllText(Path.Combine(root, "proj", "demo.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                "  <PropertyGroup>\n" +
                "    <TargetFramework>net9.0</TargetFramework>\n" +
                "    <ImplicitUsings>enable</ImplicitUsings>\n" +
                "    <Nullable>enable</Nullable>\n" +
                "  </PropertyGroup>\n" +
                "  <ItemGroup>\n" +
                "    <Compile Include=\"..\\Calculator.cs\" />\n" +
                "    <Compile Include=\"..\\Caller1.cs\" />\n" +
                "  </ItemGroup>\n" +
                "</Project>\n");
            File.WriteAllText(Path.Combine(root, "app.sln"),
                "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
                "# Visual Studio Version 17\n" +
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Demo\", \"proj\\demo.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\n" +
                "EndProject\n" +
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Missing\", \"Missing.csproj\", \"{22222222-2222-2222-2222-222222222222}\"\n" +
                "EndProject\n");

            var index = new CodeIndex(root);
            var result = index.GetCallers("Calculator.cs", "Add", 5);

            Assert.True(result.Success, result.Error);
            var v = result.Value!;
            Assert.Equal("roslyn", v.Backend);
            Assert.False(v.SolutionBuilds);
            var caller = v.Callers.FirstOrDefault(c => c.Path == "Caller1.cs" && c.Line == 7);
            Assert.NotNull(caller);
            Assert.Contains("Add", caller!.Snippet);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CandidateProjects_NearestFirst_CsprojBeforeSln_DeterministicOrder()
    {
        var root = MakeWorkspace();
        try
        {
            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "Lib.cs"), "");
            File.WriteAllText(Path.Combine(src, "a.csproj"), "");
            File.WriteAllText(Path.Combine(src, "b.csproj"), "");
            File.WriteAllText(Path.Combine(src, "a.sln"), "");
            File.WriteAllText(Path.Combine(root, "a.sln"), "");
            File.WriteAllText(Path.Combine(root, "z.sln"), "");

            var candidates = RoslynCallerFinder.CandidateProjects(root, Path.Combine(src, "Lib.cs"));

            Assert.Equal(
                [
                    Path.Combine(src, "a.csproj"),
                    Path.Combine(src, "b.csproj"),
                    Path.Combine(src, "a.sln"),
                    Path.Combine(root, "a.sln"),
                    Path.Combine(root, "z.sln"),
                ],
                candidates);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetCallers_Roslyn_NearestProjectBeatsRootSolution()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "App.sln"),
                "Microsoft Visual Studio Solution File, Format Version 12.00\n");
            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "lib.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                "  <PropertyGroup>\n" +
                "    <TargetFramework>net9.0</TargetFramework>\n" +
                "    <ImplicitUsings>enable</ImplicitUsings>\n" +
                "    <Nullable>enable</Nullable>\n" +
                "  </PropertyGroup>\n" +
                "</Project>\n");
            File.WriteAllText(Path.Combine(src, "Library.cs"),
                "namespace Demo;\n" +
                "\n" +
                "public static class Library\n" +
                "{\n" +
                "    public static int Add(int a, int b) => a + b;\n" +
                "}\n");
            File.WriteAllText(Path.Combine(src, "Caller1.cs"),
                "namespace Demo;\n" +
                "\n" +
                "public class Caller1\n" +
                "{\n" +
                "    public int Use(int x)\n" +
                "    {\n" +
                "        var result = Library.Add(x, 1);\n" +
                "        return result;\n" +
                "    }\n" +
                "}\n");

            var index = new CodeIndex(root);
            var result = index.GetCallers("src/Library.cs", "Add", 5);

            Assert.True(result.Success, result.Error);
            var v = result.Value!;
            Assert.Equal("roslyn", v.Backend);
            Assert.True(v.SolutionBuilds);
            var caller = v.Callers.FirstOrDefault(c => c.Path == "src/Caller1.cs" && c.Line == 7);
            Assert.NotNull(caller);
            Assert.Contains("Add", caller!.Snippet);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void WriteCSharpSources(string root)
    {
        File.WriteAllText(Path.Combine(root, "Calculator.cs"),
            "namespace Demo;\n" +
            "\n" +
            "public static class Calculator\n" +
            "{\n" +
            "    public static int Add(int a, int b) => a + b;\n" +
            "}\n");
        File.WriteAllText(Path.Combine(root, "Caller1.cs"),
            "namespace Demo;\n" +
            "\n" +
            "public class Caller1\n" +
            "{\n" +
            "    public int Use(int x)\n" +
            "    {\n" +
            "        var result = Calculator.Add(x, 1);\n" +
            "        return result;\n" +
            "    }\n" +
            "}\n");
    }

    private static void WriteCSharpProject(string root)
    {
        File.WriteAllText(Path.Combine(root, "demo.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>net9.0</TargetFramework>\n" +
            "    <ImplicitUsings>enable</ImplicitUsings>\n" +
            "    <Nullable>enable</Nullable>\n" +
            "  </PropertyGroup>\n" +
            "</Project>\n");
        WriteCSharpSources(root);
    }

    private static string MakeWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-callers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
