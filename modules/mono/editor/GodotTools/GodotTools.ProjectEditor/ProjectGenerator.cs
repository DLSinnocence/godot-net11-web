using System;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using GodotTools.Shared;

namespace GodotTools.ProjectEditor
{
    public static class ProjectGenerator
    {
        public static string GodotSdkAttrValue => $"Godot.NET.Sdk/{GeneratedGodotNupkgsVersions.GodotNETSdk}";

        public static string GodotMinimumRequiredTfm => "net8.0";

        public static ProjectRootElement GenGameProject(string name)
        {
            if (name.Length == 0)
                throw new ArgumentException("Project name is empty.", nameof(name));

            var root = ProjectRootElement.Create(NewProjectFileOptions.None);

            root.Sdk = GodotSdkAttrValue;

            var mainGroup = root.AddPropertyGroup();
            mainGroup.AddProperty("TargetFramework", GodotMinimumRequiredTfm);

            // Mobile and web exports use the experimental .NET 11 CoreCLR runtime packs.
            var net11Android = mainGroup.AddProperty("TargetFramework", "net11.0");
            net11Android.Condition = " '$(GodotTargetPlatform)' == 'android' ";

            var net11Ios = mainGroup.AddProperty("TargetFramework", "net11.0");
            net11Ios.Condition = " '$(GodotTargetPlatform)' == 'ios' ";

            var net11Web = mainGroup.AddProperty("TargetFramework", "net11.0");
            net11Web.Condition = " '$(GodotTargetPlatform)' == 'web' ";

            mainGroup.AddProperty("EnableDynamicLoading", "true");

            // Disable dynamic loading when building for web.
            var dynLoad = mainGroup.AddProperty("EnableDynamicLoading", "false");
            dynLoad.Condition = " '$(GodotTargetPlatform)' == 'web' ";

            string sanitizedName = IdentifierUtils.SanitizeQualifiedIdentifier(name, allowEmptyIdentifiers: true);

            // If the name is not a valid namespace, manually set RootNamespace to a sanitized one.
            if (sanitizedName != name)
                mainGroup.AddProperty("RootNamespace", sanitizedName);

            return root;
        }

        public static string GenAndSaveGameProject(string dir, string name)
        {
            if (name.Length == 0)
                throw new ArgumentException("Project name is empty.", nameof(name));

            string path = Path.Combine(dir, name + ".csproj");

            var root = GenGameProject(name);

            // Save (without BOM)
            root.Save(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return Guid.NewGuid().ToString().ToUpperInvariant();
        }
    }
}
