namespace ProjectBeacon.Application.Tests;

using Application.CodeIndex;

public sealed class SignatureTests
{
    [Fact]
    public void GetSignatures_CSharp_ExtractsClassAndMethod()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "Foo.cs"),
                "public class Foo\n{\n    public int Add(int a, int b) => a + b;\n}\n");

            var index = new CodeIndex(root);
            var result = index.GetSignatures(["Foo.cs"]);

            Assert.True(result.Success);
            var file = result.Value!.Files.Single();
            Assert.Equal("roslyn", file.Backend);
            Assert.Null(file.Error);

            var names = file.Symbols.Select(s => s.Name).ToList();
            Assert.Contains("Foo", names);
            Assert.Contains("Add", names);

            var cls = file.Symbols.Single(s => s.Name == "Foo");
            Assert.Equal("class", cls.Kind);
            Assert.Equal(1, cls.Line);

            var method = file.Symbols.Single(s => s.Name == "Add");
            Assert.Equal("method", method.Kind);
            Assert.Equal(3, method.Line);
            Assert.Contains("int", method.Signature);

            Assert.Equal("roslyn", result.Value.Backend);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetSignatures_TypeScript_ExtractsFunctions()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "app.ts"),
                "function greet(name: string): string { return `hi ${name}`; }\n" +
                "class Service { run(): void {} }\n");

            var index = new CodeIndex(root);
            var result = index.GetSignatures(["app.ts"]);

            Assert.True(result.Success);
            var file = result.Value!.Files.Single();
            Assert.Equal("tree-sitter", file.Backend);
            Assert.Null(file.Error);

            var names = file.Symbols.Select(s => s.Name).ToList();
            Assert.Contains("greet", names);
            Assert.Contains("Service", names);

            var fn = file.Symbols.Single(s => s.Name == "greet");
            Assert.Equal("function", fn.Kind);
            Assert.Equal(1, fn.Line);

            Assert.Equal("tree-sitter", result.Value.Backend);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetSignatures_Python_ExtractsFunctions()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "main.py"),
                "def hello(name):\n    return 'hi ' + name\n\n" +
                "class Greeter:\n    pass\n");

            var index = new CodeIndex(root);
            var result = index.GetSignatures(["main.py"]);

            Assert.True(result.Success);
            var file = result.Value!.Files.Single();
            Assert.Equal("tree-sitter", file.Backend);
            Assert.Null(file.Error);

            var names = file.Symbols.Select(s => s.Name).ToList();
            Assert.Contains("hello", names);
            Assert.Contains("Greeter", names);

            var fn = file.Symbols.Single(s => s.Name == "hello");
            Assert.Equal("function", fn.Kind);
            Assert.Equal(1, fn.Line);

            var cls = file.Symbols.Single(s => s.Name == "Greeter");
            Assert.Equal("class", cls.Kind);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetSignatures_MixedBatch_ReturnsMixedBackend()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "A.cs"), "public class A {}\n");
            File.WriteAllText(Path.Combine(root, "b.ts"), "function f(): void {}\n");

            var index = new CodeIndex(root);
            var result = index.GetSignatures(["A.cs", "b.ts"]);

            Assert.True(result.Success);
            Assert.Equal("mixed", result.Value!.Backend);
            Assert.Equal(2, result.Value.Files.Count);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetSignatures_EmptyPaths_ReturnsEmptyResult()
    {
        var root = MakeWorkspace();
        try
        {
            var index = new CodeIndex(root);
            var result = index.GetSignatures(Array.Empty<string>());

            Assert.True(result.Success);
            Assert.Empty(result.Value!.Files);
            Assert.Equal("", result.Value.Backend);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetSignatures_UnsupportedExtension_ReturnsUnsupported()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "data.xyz"), "something\n");

            var index = new CodeIndex(root);
            var result = index.GetSignatures(["data.xyz"]);

            Assert.True(result.Success);
            var file = result.Value!.Files.Single();
            Assert.Equal("unsupported", file.Backend);
            Assert.Empty(file.Symbols);
            Assert.Equal("", result.Value.Backend);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetSignatures_MissingFile_ReturnsErrorEntry()
    {
        var root = MakeWorkspace();
        try
        {
            var index = new CodeIndex(root);
            var result = index.GetSignatures(["missing.cs"]);

            Assert.True(result.Success);
            var file = result.Value!.Files.Single();
            Assert.Equal("missing.cs", file.Path);
            Assert.Equal("roslyn", file.Backend);
            Assert.Empty(file.Symbols);
            Assert.Contains("not found", file.Error!);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetSignatures_SlashParentEscape_ReturnsErrorEntry_DoesNotReadOutside()
    {
        var root = MakeWorkspace();
        var outside = Path.GetFullPath(Path.Combine(root, "..", "outside.cs"));
        try
        {
            File.WriteAllText(outside, "public class OutsideDecoy { public int Leak() => 42; }\n");

            var index = new CodeIndex(root);
            var result = index.GetSignatures(["../outside.cs"]);

            Assert.True(result.Success);
            var file = result.Value!.Files.Single();
            Assert.Equal("../outside.cs", file.Path);
            Assert.Equal("roslyn", file.Backend);
            Assert.Empty(file.Symbols);
            Assert.Contains("escapes", file.Error!);
            Assert.DoesNotContain("OutsideDecoy", file.Symbols.Select(s => s.Name));
        }
        finally
        {
            Directory.Delete(root, true);
            DeleteFileIfPresent(outside);
        }
    }

    [Fact]
    public void GetSignatures_BackslashParentEscape_ReturnsErrorEntry_DoesNotReadOutside()
    {
        if (Path.DirectorySeparatorChar != '\\')
            return;

        var root = MakeWorkspace();
        var outside = Path.GetFullPath(Path.Combine(root, "..", "outside.cs"));
        try
        {
            File.WriteAllText(outside, "public class OutsideDecoy { public int Leak() => 42; }\n");

            var index = new CodeIndex(root);
            var result = index.GetSignatures(["..\\outside.cs"]);

            Assert.True(result.Success);
            var file = result.Value!.Files.Single();
            Assert.Equal("..\\outside.cs", file.Path);
            Assert.Equal("roslyn", file.Backend);
            Assert.Empty(file.Symbols);
            Assert.Contains("escapes", file.Error!);
            Assert.DoesNotContain("OutsideDecoy", file.Symbols.Select(s => s.Name));
        }
        finally
        {
            Directory.Delete(root, true);
            DeleteFileIfPresent(outside);
        }
    }

    [Fact]
    public void GetSignatures_NestedTraversal_ReturnsErrorEntry_DoesNotReadOutside()
    {
        var root = MakeWorkspace();
        var outside = Path.GetFullPath(Path.Combine(root, "..", "outside.cs"));
        try
        {
            File.WriteAllText(outside, "public class OutsideDecoy { public int Leak() => 42; }\n");
            Directory.CreateDirectory(Path.Combine(root, "a"));

            var paths = new List<string> { "a/../../outside.cs" };
            if (Path.DirectorySeparatorChar == '\\')
                paths.Add("a\\..\\..\\outside.cs");

            var index = new CodeIndex(root);
            var result = index.GetSignatures(paths);

            Assert.True(result.Success);
            var files = result.Value!.Files;
            Assert.Equal(paths.Count, files.Count);
            foreach (var (requested, entry) in paths.Zip(files))
            {
                Assert.Equal(requested, entry.Path);
                Assert.Equal("roslyn", entry.Backend);
                Assert.Empty(entry.Symbols);
                Assert.Contains("escapes", entry.Error!);
                Assert.DoesNotContain("OutsideDecoy", entry.Symbols.Select(s => s.Name));
            }
        }
        finally
        {
            Directory.Delete(root, true);
            DeleteFileIfPresent(outside);
        }
    }

    [Fact]
    public void GetSignatures_TooLargeFile_ReturnsErrorEntry()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "big.cs"), new byte[1_000_001]);

            var index = new CodeIndex(root);
            var result = index.GetSignatures(["big.cs"]);

            Assert.True(result.Success);
            var file = result.Value!.Files.Single();
            Assert.Equal("big.cs", file.Path);
            Assert.Equal("roslyn", file.Backend);
            Assert.Empty(file.Symbols);
            Assert.Contains("too large", file.Error!);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetSignatures_EmptyFile_ReturnsNoSymbolsWithoutError()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "empty.cs"), "");

            var index = new CodeIndex(root);
            var result = index.GetSignatures(["empty.cs"]);

            Assert.True(result.Success);
            var file = result.Value!.Files.Single();
            Assert.Equal("empty.cs", file.Path);
            Assert.Equal("roslyn", file.Backend);
            Assert.Empty(file.Symbols);
            Assert.Null(file.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetSignatures_MixedBatch_GoodPlusEscapedPlusMissing()
    {
        var root = MakeWorkspace();
        var outside = Path.GetFullPath(Path.Combine(root, "..", "outside.cs"));
        try
        {
            File.WriteAllText(Path.Combine(root, "good.cs"), "public class Good { public int Add(int a, int b) => a + b; }\n");
            File.WriteAllText(outside, "public class OutsideDecoy { public int Leak() => 42; }\n");

            var index = new CodeIndex(root);
            var result = index.GetSignatures(["good.cs", "../outside.cs", "missing.cs"]);

            Assert.True(result.Success);
            var files = result.Value!.Files;
            Assert.Equal(3, files.Count);

            var good = files.Single(f => f.Path == "good.cs");
            Assert.Null(good.Error);
            Assert.NotEmpty(good.Symbols);
            Assert.Contains("Good", good.Symbols.Select(s => s.Name));

            var escaped = files.Single(f => f.Path == "../outside.cs");
            Assert.Equal("roslyn", escaped.Backend);
            Assert.Empty(escaped.Symbols);
            Assert.Contains("escapes", escaped.Error!);
            Assert.DoesNotContain("OutsideDecoy", escaped.Symbols.Select(s => s.Name));

            var missing = files.Single(f => f.Path == "missing.cs");
            Assert.Equal("roslyn", missing.Backend);
            Assert.Empty(missing.Symbols);
            Assert.Contains("not found", missing.Error!);
        }
        finally
        {
            Directory.Delete(root, true);
            DeleteFileIfPresent(outside);
        }
    }

    [Fact]
    public void GetSignatures_RespectsMaxFiles()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "A.cs"), "public class A {}\n");
            File.WriteAllText(Path.Combine(root, "B.cs"), "public class B {}\n");
            File.WriteAllText(Path.Combine(root, "C.cs"), "public class C {}\n");

            var index = new CodeIndex(root);
            var result = index.GetSignatures(["A.cs", "B.cs", "C.cs"], maxFiles: 2);

            Assert.True(result.Success);
            Assert.Equal(2, result.Value!.Files.Count);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetSignatures_PerFileErrorIsolation()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "good.cs"), "public class Good {}\n");
            File.WriteAllText(Path.Combine(root, "bad.ts"), "this is not valid typescript {{{{");

            var index = new CodeIndex(root);
            var result = index.GetSignatures(["good.cs", "bad.ts"]);

            Assert.True(result.Success);
            var files = result.Value!.Files;
            Assert.Equal(2, files.Count);

            var good = files.Single(f => f.Path == "good.cs");
            Assert.Null(good.Error);
            Assert.NotEmpty(good.Symbols);

            var bad = files.Single(f => f.Path == "bad.ts");
            Assert.Equal("tree-sitter", bad.Backend);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NewLanguage_RegistrableWithoutModifyingCodeIndexOrBackends()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "script.rs"),
                "fn main() { println!(\"hi\"); }\n");

            var customRules = new SignatureRules(
                [new NodeRule("function_item", "function")],
                NameField: "name");

            var customLanguage = new LanguageDefinition(
                "rust", "Rust", [".rs"],
                SignatureBackend.TreeSitter,
                Grammar: "rust",
                SignatureRules: customRules);

            var defaultRegistry = LanguageRegistry.CreateDefault();
            var registry = new LanguageRegistry(
                defaultRegistry.Languages.Append(customLanguage));

            var index = new CodeIndex(root, registry);
            var result = index.GetSignatures(["script.rs"]);

            Assert.True(result.Success);
            var file = result.Value!.Files.Single();
            Assert.Equal("tree-sitter", file.Backend);
            Assert.Contains("main", file.Symbols.Select(s => s.Name));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RoslynBackend_ExtractsInterfaceAndStruct()
    {
        var root = MakeWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "Types.cs"),
                "public interface IService { void Do(); }\n" +
                "public struct Point { public int X, Y; }\n");

            var index = new CodeIndex(root);
            var result = index.GetSignatures(["Types.cs"]);

            Assert.True(result.Success);
            var file = result.Value!.Files.Single();

            var iface = file.Symbols.Single(s => s.Name == "IService");
            Assert.Equal("interface", iface.Kind);

            var struct_ = file.Symbols.Single(s => s.Name == "Point");
            Assert.Equal("struct", struct_.Kind);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Registry_Resolve_MatchesByExtension()
    {
        var registry = LanguageRegistry.CreateDefault();

        Assert.Equal("csharp", registry.Resolve("src/Foo.cs")!.Id);
        Assert.Equal("typescript", registry.Resolve("app.tsx")!.Id);
        Assert.Equal("typescript", registry.Resolve("bar.ts")!.Id);
        Assert.Equal("python", registry.Resolve("script.py")!.Id);
        Assert.Null(registry.Resolve("data.xyz"));
        Assert.Null(registry.Resolve("noext"));
    }

    [Fact]
    public void Registry_DuplicateExtension_Throws()
    {
        var lang1 = new LanguageDefinition("a", "A", [".foo"], SignatureBackend.Roslyn, null, null);
        var lang2 = new LanguageDefinition("b", "B", [".foo"], SignatureBackend.Roslyn, null, null);

        Assert.Throws<ArgumentException>(() => new LanguageRegistry([lang1, lang2]));
    }

    private static string MakeWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-sig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteFileIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the shared temp decoy; a locked file must not fail the test.
        }
    }
}
