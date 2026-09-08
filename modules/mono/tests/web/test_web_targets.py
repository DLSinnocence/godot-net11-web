import json
import os
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parents[4]
BROWSER_TARGETS = REPOSITORY_ROOT / "modules/mono/editor/Godot.NET.Sdk/Godot.NET.Sdk/Sdk/Browser.targets"
PROJECT_EDITOR_PROJECT = (
    REPOSITORY_ROOT / "modules/mono/editor/GodotTools/GodotTools.ProjectEditor/GodotTools.ProjectEditor.csproj"
)
TRANSLATE_TO_EXNREF_FLAG = "-s BINARYEN_EXTRA_PASSES=translate-to-exnref"


class WebTargetsTests(unittest.TestCase):
    def create_dependency_probe(self, directory, copy_msbuild_runtime=False):
        child_directory = directory / "ProjectEditor"
        child_directory.mkdir()
        child = ET.parse(PROJECT_EDITOR_PROJECT).getroot()
        for group in child.findall("./ItemGroup"):
            for reference in group.findall("ProjectReference"):
                group.remove(reference)
        if copy_msbuild_runtime:
            child.find("./ItemGroup/PackageReference[@Include='Microsoft.Build']").attrib.pop("ExcludeAssets")
        ET.ElementTree(child).write(child_directory / "ProjectEditor.csproj", encoding="utf-8")
        (child_directory / "Parser.cs").write_text(
            "public static class Parser { public static string Parse(string value) => "
            "NuGet.Frameworks.NuGetFramework.Parse(value).ToString(); }",
            encoding="utf-8",
        )
        parent = directory / "Parent"
        parent.mkdir()
        (parent / "Parent.csproj").write_text(
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../ProjectEditor/ProjectEditor.csproj" />
  </ItemGroup>
</Project>
""",
            encoding="utf-8",
        )
        (parent / "Program.cs").write_text('System.Console.WriteLine(Parser.Parse("net8.0"));', encoding="utf-8")
        return parent / "Parent.csproj"

    def build_dependency_probe(self, directory, project):
        environment = os.environ.copy()
        environment["NUGET_HTTP_CACHE_PATH"] = str(directory / "http-cache")
        environment["NUGET_SCRATCH"] = str(directory / "scratch")
        command = [
            "dotnet",
            "build",
            str(project),
            "--disable-build-servers",
            "-nologo",
            "-verbosity:minimal",
            "-maxcpucount:1",
            "-p:NuGetAudit=false",
            f"-p:RestorePackagesPath={directory / 'packages'}",
        ]
        cache = Path(environment.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
        if cache.is_dir():
            # The existing package cache is read-only; missing packages go into the fixture.
            command.append(f"-p:RestoreFallbackFolders={cache}")
        return subprocess.run(
            command, capture_output=True, encoding="utf-8", errors="replace", env=environment, timeout=180
        )

    def test_nuget_frameworks_runtime_reaches_parent(self):
        with tempfile.TemporaryDirectory(prefix="godot-locator-parent-") as temporary_directory:
            directory = Path(temporary_directory)
            parent = self.create_dependency_probe(directory)
            result = self.build_dependency_probe(directory, parent)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

            output = parent.parent / "bin/Debug/net8.0"
            self.assertTrue((output / "NuGet.Frameworks.dll").is_file())
            dependencies = json.loads((output / "Parent.deps.json").read_text(encoding="utf-8"))
            runtime_files = [
                name
                for target in dependencies["targets"].values()
                for library in target.values()
                for name in library.get("runtime", {})
            ]
            self.assertIn("NuGet.Frameworks.dll", runtime_files)
            self.assertFalse((output / "Microsoft.Build.dll").exists())
            self.assertFalse((output / "Microsoft.Build.Framework.dll").exists())
            result = subprocess.run(
                ["dotnet", str(output / "Parent.dll")],
                capture_output=True,
                encoding="utf-8",
                errors="replace",
                timeout=30,
            )
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertEqual(result.stdout.strip(), "net8.0")

    def test_locator_still_rejects_copied_msbuild_runtime(self):
        with tempfile.TemporaryDirectory(prefix="godot-locator-guard-") as temporary_directory:
            directory = Path(temporary_directory)
            parent = self.create_dependency_probe(directory, copy_msbuild_runtime=True)
            result = self.build_dependency_probe(directory, parent)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("MSBL001", result.stdout + result.stderr)
            self.assertIn("'Microsoft.Build'", result.stdout + result.stderr)

    def test_exception_translation_is_a_platform_neutral_link_flag(self):
        root = ET.parse(BROWSER_TARGETS).getroot()

        flags = root.findall(f"./ItemGroup/GodotEmccExtraLDFlag[@Include='{TRANSLATE_TO_EXNREF_FLAG}']")

        self.assertEqual(len(flags), 1)

    def test_exception_translation_is_present_in_evaluated_emcc_flags(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            project_path = Path(temporary_directory) / "EvaluateWebTargets.proj"
            project_path.write_text(
                f"""<Project>
  <Import Project=\"{BROWSER_TARGETS.as_posix()}\" />
  <Target Name=\"PrintGodotEmccExtraLDFlags\">
    <Message Text=\"GodotEmccExtraLDFlags=$(EmccExtraLDFlags)\" Importance=\"High\" />
  </Target>
</Project>
""",
                encoding="utf-8",
            )

            result = subprocess.run(
                [
                    "dotnet",
                    "msbuild",
                    str(project_path),
                    "-nologo",
                    "-verbosity:minimal",
                    "-target:PrintGodotEmccExtraLDFlags",
                ],
                check=True,
                capture_output=True,
                encoding="utf-8",
                errors="replace",
                timeout=30,
            )

        self.assertIn(TRANSLATE_TO_EXNREF_FLAG, result.stdout)

    def test_project_editor_uses_net8_msbuild_locator(self):
        root = ET.parse(PROJECT_EDITOR_PROJECT).getroot()
        locator = root.find("./ItemGroup/PackageReference[@Include='Microsoft.Build.Locator']")
        nuget_frameworks = root.find("./ItemGroup/PackageReference[@Include='NuGet.Frameworks']")

        self.assertIsNotNone(locator)
        self.assertEqual(locator.get("Version"), "1.11.2")
        self.assertIsNotNone(nuget_frameworks)
        self.assertEqual(nuget_frameworks.get("ExcludeAssets"), "all")
        self.assertEqual(nuget_frameworks.get("PrivateAssets"), "all")
        self.assertEqual(nuget_frameworks.get("GeneratePathProperty"), "true")
        reference = root.find("./ItemGroup/Reference[@Include='NuGet.Frameworks']")
        self.assertIsNotNone(reference)
        self.assertEqual(reference.get("Private"), "true")


if __name__ == "__main__":
    unittest.main()
