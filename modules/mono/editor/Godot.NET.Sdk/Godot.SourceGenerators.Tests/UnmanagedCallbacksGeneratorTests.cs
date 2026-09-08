using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot.SourceGenerators.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Godot.SourceGenerators.Tests;

public class UnmanagedCallbacksGeneratorTests
{
    private const string FixturePreamble = """
        using System;
        using Godot.SourceGenerators.Internal;

        namespace Godot.Bridge { }

        namespace Godot.NativeInterop
        {
            public enum godot_bool : byte { False, True }

            public ref struct godot_ref
            {
                public nint Value;
            }

            public static unsafe class CustomUnsafe
            {
                public static godot_ref* AsPointer(ref godot_ref value) => null;
                public static godot_ref* ReadOnlyRefAsPointer(in godot_ref value) => null;
            }
        }

        namespace Fixtures
        {
            public struct Color
            {
                public float R;
                public float G;
                public float B;
                public float A;
            }

            public struct Wasm
            {
                public int Value;
            }

            public struct Large
            {
                public long A;
                public long B;
                public long C;
            }
        """;

    [Fact]
    public void UnmanagedStructReturnsUseHiddenOutputBuffers()
    {
        var generated = RunGenerator("""
            [GenerateUnmanagedCallbacks(typeof(Callbacks))]
            public static unsafe partial class NativeFuncs
            {
                private partial struct Callbacks { }

                internal static partial Color GetColor();
                internal static partial Wasm GetWasm(int value);
                internal static partial Large GetLarge(long value);
            }
            """);

        AssertCallbackType(generated, "GetColor", "delegate* unmanaged<global::Fixtures.Color*, void>");
        AssertCallbackType(generated, "GetWasm", "delegate* unmanaged<int, global::Fixtures.Wasm*, void>");
        AssertCallbackType(generated, "GetLarge", "delegate* unmanaged<long, global::Fixtures.Large*, void>");

        AssertStructReturnForwarding(generated, "GetColor", expectedOriginalArgumentCount: 0);
        AssertStructReturnForwarding(generated, "GetWasm", expectedOriginalArgumentCount: 1);
        AssertStructReturnForwarding(generated, "GetLarge", expectedOriginalArgumentCount: 1);
    }

    [Fact]
    public void StructReturnPreservesRefOutCopyAndWriteback()
    {
        var generated = RunGenerator("""
            [GenerateUnmanagedCallbacks(typeof(Callbacks))]
            public static unsafe partial class NativeFuncs
            {
                private partial struct Callbacks { }

                internal static partial Color Transform(in Color input, ref Color value, out int count);
            }
            """);

        AssertCallbackType(generated, "Transform",
            "delegate* unmanaged<global::Fixtures.Color*, global::Fixtures.Color*, int*, global::Fixtures.Color*, void>");

        var method = GetGeneratedMethod(generated, "Transform");
        string body = method.Body!.NormalizeWhitespace().ToFullString();
        Assert.Contains("global::Fixtures.Color input_copy = input;", body);
        Assert.Contains("global::Fixtures.Color value_copy = value;", body);
        Assert.Contains("int count_copy;", body);
        Assert.DoesNotContain("input = input_copy;", body);
        Assert.Contains("value = value_copy;", body);
        Assert.Contains("count = count_copy;", body);
        AssertStructReturnForwarding(method, expectedOriginalArgumentCount: 3);
    }

    [Fact]
    public void NonStructReturnsRemainByValue()
    {
        var generated = RunGenerator("""
            public enum Result : int { Ok }

            [GenerateUnmanagedCallbacks(typeof(Callbacks))]
            public static unsafe partial class NativeFuncs
            {
                private partial struct Callbacks { }

                internal static partial void ReturnVoid();
                internal static partial int ReturnInt();
                internal static partial Result ReturnEnum();
                internal static partial Godot.NativeInterop.godot_bool ReturnGodotBool();
                internal static partial IntPtr ReturnIntPtr();
                internal static partial UIntPtr ReturnUIntPtr();
                internal static partial int* ReturnPointer();
                internal static partial delegate* unmanaged<int> ReturnFunctionPointer();
            }
            """);

        AssertCallbackType(generated, "ReturnVoid", "delegate* unmanaged<void>");
        AssertCallbackType(generated, "ReturnInt", "delegate* unmanaged<int>");
        AssertCallbackType(generated, "ReturnEnum", "delegate* unmanaged<global::Fixtures.Result>");
        AssertCallbackType(generated, "ReturnGodotBool",
            "delegate* unmanaged<global::Godot.NativeInterop.godot_bool>");
        AssertCallbackType(generated, "ReturnIntPtr", "delegate* unmanaged<nint>");
        AssertCallbackType(generated, "ReturnUIntPtr", "delegate* unmanaged<nuint>");
        AssertCallbackType(generated, "ReturnPointer", "delegate* unmanaged<int*>");
        AssertCallbackType(generated, "ReturnFunctionPointer", "delegate* unmanaged<delegate* unmanaged<int> >");

        foreach (string methodName in new[]
                 {
                     "ReturnInt", "ReturnEnum", "ReturnGodotBool", "ReturnIntPtr", "ReturnUIntPtr",
                     "ReturnPointer", "ReturnFunctionPointer",
                 })
        {
            var method = GetGeneratedMethod(generated, methodName);
            Assert.Single(method.Body!.Statements);
            Assert.IsType<ReturnStatementSyntax>(method.Body.Statements[0]);
        }
    }

    [Fact]
    public void GodotRefStructReturnCompilesAndRetParameterDoesNotCollide()
    {
        var generated = RunGenerator("""
            [GenerateUnmanagedCallbacks(typeof(Callbacks))]
            public static unsafe partial class NativeFuncs
            {
                private partial struct Callbacks { }

                internal static partial Godot.NativeInterop.godot_ref GetRef();
                internal static partial Color GetColorWithRetParameter(int ret);
            }
            """);

        AssertCallbackType(generated, "GetRef",
            "delegate* unmanaged<global::Godot.NativeInterop.godot_ref*, void>");
        AssertCallbackType(generated, "GetColorWithRetParameter",
            "delegate* unmanaged<int, global::Fixtures.Color*, void>");

        AssertStructReturnForwarding(generated, "GetRef", expectedOriginalArgumentCount: 0);

        var collisionMethod = GetGeneratedMethod(generated, "GetColorWithRetParameter");
        var local = Assert.Single(collisionMethod.Body!.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            variable => variable.Parent?.Parent is LocalDeclarationStatementSyntax);
        Assert.NotEqual("ret", local.Identifier.ValueText);
        AssertStructReturnForwarding(collisionMethod, expectedOriginalArgumentCount: 1);
    }

    private static IReadOnlyList<SyntaxTree> RunGenerator(string fixtureBody)
    {
        string source = FixturePreamble + fixtureBody + "\n}";
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp12);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "UnmanagedCallbacksGeneratorFixture",
            new[] { syntaxTree },
            GetFrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new ISourceGenerator[] { new UnmanagedCallbacksGenerator() },
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        AssertNoErrors(diagnostics);
        AssertNoErrors(outputCompilation.GetDiagnostics());

        var runResult = driver.GetRunResult();
        AssertNoErrors(runResult.Diagnostics);
        var generatorResult = Assert.Single(runResult.Results);
        Assert.Null(generatorResult.Exception);

        return generatorResult.GeneratedSources.Select(sourceResult => sourceResult.SyntaxTree).ToArray();
    }

    private static IEnumerable<MetadataReference> GetFrameworkReferences()
    {
        string[] trustedPlatformAssemblies =
            ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator);

        return trustedPlatformAssemblies.Select(path => MetadataReference.CreateFromFile(path));
    }

    private static void AssertNoErrors(IEnumerable<Diagnostic> diagnostics)
    {
        var errors = diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0,
            string.Join(System.Environment.NewLine, errors.Select(error => error.ToString())));
    }

    private static void AssertCallbackType(
        IReadOnlyList<SyntaxTree> generated,
        string callbackName,
        string expectedType)
    {
        var variable = Assert.Single(generated
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()),
            candidate => candidate.Identifier.ValueText == callbackName);
        var field = Assert.IsType<FieldDeclarationSyntax>(variable.Parent!.Parent);
        Assert.Equal(expectedType, field.Declaration.Type.NormalizeWhitespace().ToFullString());
    }

    private static MethodDeclarationSyntax GetGeneratedMethod(
        IReadOnlyList<SyntaxTree> generated,
        string methodName)
    {
        return Assert.Single(generated
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()),
            method => method.Identifier.ValueText == methodName && method.Body is not null);
    }

    private static void AssertStructReturnForwarding(
        IReadOnlyList<SyntaxTree> generated,
        string methodName,
        int expectedOriginalArgumentCount)
    {
        AssertStructReturnForwarding(GetGeneratedMethod(generated, methodName), expectedOriginalArgumentCount);
    }

    private static void AssertStructReturnForwarding(
        MethodDeclarationSyntax method,
        int expectedOriginalArgumentCount)
    {
        var local = Assert.Single(method.Body!.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            variable => variable.Parent?.Parent is LocalDeclarationStatementSyntax &&
                variable.Initializer?.Value.IsKind(SyntaxKind.DefaultLiteralExpression) == true);
        string localName = local.Identifier.ValueText;
        Assert.Equal("default", Assert.IsType<LiteralExpressionSyntax>(local.Initializer!.Value).Token.ValueText);

        var invocation = Assert.Single(method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            candidate => candidate.Expression.ToString().Contains("_unmanagedCallbacks", StringComparison.Ordinal));
        Assert.Equal(expectedOriginalArgumentCount + 1, invocation.ArgumentList.Arguments.Count);
        var returnPointer = Assert.IsType<PrefixUnaryExpressionSyntax>(invocation.ArgumentList.Arguments[^1].Expression);
        Assert.Equal(SyntaxKind.AddressOfExpression, returnPointer.Kind());
        Assert.Equal(localName, Assert.IsType<IdentifierNameSyntax>(returnPointer.Operand).Identifier.ValueText);

        var returnStatement = Assert.Single(method.Body.Statements.OfType<ReturnStatementSyntax>());
        Assert.Equal(localName, Assert.IsType<IdentifierNameSyntax>(returnStatement.Expression).Identifier.ValueText);
    }
}
