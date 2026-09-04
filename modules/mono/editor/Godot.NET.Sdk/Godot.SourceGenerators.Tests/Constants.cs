using System.IO;
using System.Reflection;
using Microsoft.CodeAnalysis.Testing;

namespace Godot.SourceGenerators.Tests;

public static class Constants
{
    public static Assembly GodotSharpAssembly => typeof(GodotObject).Assembly;

    // The analyzer testing package doesn't expose ReferenceAssemblies.Net.Net110 yet,
    // so use the reference pack version bundled with the pinned .NET 11 preview SDK.
    public static ReferenceAssemblies Net110 => new ReferenceAssemblies(
        "net11.0",
        new PackageIdentity("Microsoft.NETCore.App.Ref", "11.0.0-preview.7.26381.103"),
        Path.Combine("ref", "net11.0")
    );

    public static string ExecutingAssemblyPath { get; }
    public static string SourceFolderPath { get; }
    public static string GeneratedSourceFolderPath { get; }

    static Constants()
    {
        ExecutingAssemblyPath = Path.GetFullPath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location!)!);

        var testDataPath = Path.Combine(ExecutingAssemblyPath, "TestData");

        SourceFolderPath = Path.Combine(testDataPath, "Sources");
        GeneratedSourceFolderPath = Path.Combine(testDataPath, "GeneratedSources");
    }
}
