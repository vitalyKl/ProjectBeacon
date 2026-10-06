namespace ProjectBeacon.Application.Tests;

using Application.CodeIndex;

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

    private static string MakeWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-callers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
