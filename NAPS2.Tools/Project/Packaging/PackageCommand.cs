using System.Text;
using System.Text.RegularExpressions;
using NAPS2.Tools.Project.Targets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NAPS2.Tools.Project.Packaging;

public class PackageCommand : ICommand<PackageOptions>
{
    public int Run(PackageOptions opts)
    {
        foreach (var target in TargetsHelper.EnumeratePackageTargets(
                     opts.PackageType, opts.Platform, true, opts.XCompile))
        {
            PackageInfo GetPackageInfoForConfig() => GetPackageInfo(target.Platform, opts.Name);
            switch (target.Type)
            {
                case PackageType.Exe:
                    InnoSetupPackager.PackageExe(GetPackageInfoForConfig, target.Platform, opts.NoSign);
                    break;
                case PackageType.Msi:
                    WixToolsetPackager.PackageMsi(GetPackageInfoForConfig, opts.NoSign);
                    break;
                case PackageType.Msix:
                    MsixPackager.PackageMsix(GetPackageInfoForConfig, opts.NoSign);
                    break;
                case PackageType.Zip:
                    ZipArchivePackager.PackageZip(GetPackageInfoForConfig, target.Platform, opts.NoSign);
                    break;
                case PackageType.Deb:
                    DebPackager.PackageDeb(GetPackageInfoForConfig(), opts.NoSign);
                    break;
                case PackageType.Rpm:
                    RpmPackager.PackageRpm(GetPackageInfoForConfig(), opts.NoSign);
                    break;
                case PackageType.Flatpak:
                    FlatpakPackager.Package(GetPackageInfoForConfig(), opts.NoPre);
                    break;
                case PackageType.Pkg:
                    MacPackager.Package(GetPackageInfoForConfig(), opts.NoSign, opts.NoNotarize);
                    break;
            }
        }
        return 0;
    }

    private static PackageInfo GetPackageInfo(Platform platform, string? packageName)
    {
        var pkgInfo = new PackageInfo(platform, ProjectHelper.GetCurrentVersionName(),
            ProjectHelper.GetCurrentVersion(), packageName);

        if (!platform.IsWindows())
        {
            // We rely on "dotnet publish" to build the installer
            return pkgInfo;
        }

        string arch = platform == Platform.WinArm64 ? "win-arm64" : "win-x64";

        foreach (var project in new[]
                     { "NAPS2.App.WinForms", "NAPS2.App.Console" })
        {
            var buildPath = Path.Combine(Paths.SolutionRoot, project, "bin", "Release", "net9-windows", arch,
                "publish");
            if (!Directory.Exists(buildPath))
            {
                throw new Exception($"Could not find build path.");
            }
            PopulatePackageInfo(buildPath, platform, pkgInfo);
        }

        if (platform == Platform.Win64)
        {
            // No need for a 32-bit worker on ARM64
            var workerPath = Path.Combine(Paths.SolutionRoot, "NAPS2.App.Worker", "bin", "Release", "net9-windows",
                "win-x86", "publish");
            pkgInfo.AddFile(new PackageFile(workerPath, "lib", "NAPS2.Worker.exe"));
        }

        var appBuildPath = Path.Combine(Paths.SolutionRoot, "NAPS2.App.WinForms", "bin", "Release", "net9-windows",
            arch, "publish");
        if (platform == Platform.Win64)
        {
            AddPlatformFiles(pkgInfo, appBuildPath, "_win64");
            // Special case as we have a 64 bit main app and a 32 bit worker
            AddPlatformFile(pkgInfo, appBuildPath, "_win32", "twaindsm.dll");

            // Include VC++ redistributable files for Tesseract so the user doesn't need to install them separately
            var vcRedistPath = Path.Combine(Paths.SolutionRoot, "NAPS2.Setup", "lib", "win64", "vcredist");
            foreach (var file in new DirectoryInfo(vcRedistPath).EnumerateFiles())
            {
                pkgInfo.AddFile(new PackageFile(file.DirectoryName!, Path.Combine("lib", "_win64"), file.Name));
            }
        }
        if (platform == Platform.WinArm64)
        {
            AddPlatformFiles(pkgInfo, appBuildPath, "_winarm");

            // Include VC++ redistributable files for Tesseract so the user doesn't need to install them separately
            var vcRedistPath = Path.Combine(Paths.SolutionRoot, "NAPS2.Setup", "lib", "winarm", "vcredist");
            foreach (var file in new DirectoryInfo(vcRedistPath).EnumerateFiles())
            {
                pkgInfo.AddFile(new PackageFile(file.DirectoryName!, Path.Combine("lib", "_winarm"), file.Name));
            }
        }

        pkgInfo.AddFile(new PackageFile(appBuildPath, "", "appsettings.xml"));
        pkgInfo.AddFile(new PackageFile(Paths.SolutionRoot, "", "LICENSE", "license.txt"));
        pkgInfo.AddFile(new PackageFile(Paths.SolutionRoot, "", "CONTRIBUTORS", "contributors.txt"));

        return pkgInfo;
    }

    private static void PopulatePackageInfo(string buildPath, Platform platform, PackageInfo pkgInfo)
    {
        string[] excludeDlls =
        {
            // DLLs that are unneeded but missed by the built-in trimming
            "Microsoft.VisualBasic",
            "System.Data",
            "System.Private.DataContract",
            "System.Windows.Forms.Design",
            // For WPF
            "D3D",
            "Presentation",
            "Reach",
            "System.Windows.Controls.Ribbon",
            "System.Windows.Input",
            "System.Windows.Presentation",
            "System.Xaml",
            "UIAutomation",
            "WindowsBase",
            "wpfgfx",
            // For debugging
            "createdump",
            "Microsoft.DiaSymReader",
            "mscordaccore",
            "mscordbi",
        };

        var dir = new DirectoryInfo(buildPath);
        if (!dir.Exists)
        {
            throw new Exception($"Could not find path: {dir.FullName}");
        }

        // Parse the NAPS2.deps.json file to strip out dependencies we're "manually" trimming via "excludeDlls"
        var depsFile = dir.EnumerateFiles("*.deps.json").First();
        JObject deps;
        using (var stream = depsFile.OpenText())
        using (var reader = new JsonTextReader(stream))
            deps = (JObject) JToken.ReadFrom(reader);
        string arch = platform == Platform.WinArm64 ? "win-arm64" : "win-x64";
        var targets = (JObject) deps["targets"]![$".NETCoreApp,Version=v9.0/{arch}"]!;
        foreach (var pair in targets)
        {
            var target = (JObject) pair.Value!;
            if (target.TryGetValue("runtime", out var runtime))
            {
                foreach (var runtimeDlls in new Dictionary<string, JToken?>((JObject) runtime))
                {
                    var parts = runtimeDlls.Key.Split("/");
                    var dllName = parts.Last();
                    if (excludeDlls.Any(exclude => dllName.StartsWith(exclude)))
                    {
                        ((JObject) runtime).Remove(runtimeDlls.Key);
                    }
                }
            }
            if (target.TryGetValue("resources", out var resources))
            {
                foreach (var runtimeDlls in new Dictionary<string, JToken?>((JObject) resources))
                {
                    var dllName = runtimeDlls.Key.Split("/").Last();
                    if (excludeDlls.Any(exclude => dllName.StartsWith(exclude)))
                    {
                        ((JObject) resources).Remove(runtimeDlls.Key);
                    }
                }
            }
            if (target.TryGetValue("native", out var native))
            {
                foreach (var runtimeDlls in new Dictionary<string, JToken?>((JObject) native))
                {
                    var dllName = runtimeDlls.Key.Split("/").Last();
                    if (excludeDlls.Any(exclude => dllName.StartsWith(exclude)))
                    {
                        ((JObject) native).Remove(runtimeDlls.Key);
                    }
                }
            }
        }
        using (StreamWriter file = depsFile.CreateText())
        using (JsonTextWriter writer = new JsonTextWriter(file) { Formatting = Formatting.Indented })
            deps.WriteTo(writer);

        // Add each included file to the package contents
        foreach (var exeFile in dir.EnumerateFiles("*.exe"))
        {
            if (excludeDlls.All(exclude => !exeFile.Name.StartsWith(exclude)))
            {
                if (exeFile.Name == "NAPS2.Worker.exe") continue;
                PatchExe(exeFile);
                pkgInfo.AddFile(exeFile, "");
            }
        }
        foreach (var configFile in dir.EnumerateFiles("*.json"))
        {
            pkgInfo.AddFile(configFile, "lib");
        }
        foreach (var dllFile in dir.EnumerateFiles("*.dll"))
        {
            if (excludeDlls.All(exclude => !dllFile.Name.StartsWith(exclude)))
            {
                pkgInfo.AddFile(dllFile, "lib");
            }
        }
        foreach (var langFolder in dir.EnumerateDirectories()
                     .Where(x => Regex.IsMatch(x.Name, "[a-z]{2}(-[A-Za-z]+)?")))
        {
            foreach (var resourceDll in langFolder.EnumerateFiles("*.resources.dll"))
            {
                if (excludeDlls.All(exclude => !resourceDll.Name.StartsWith(exclude)))
                {
                    pkgInfo.AddFile(resourceDll, Path.Combine("lib", langFolder.Name));
                    pkgInfo.Languages.Add(langFolder.Name);
                }
            }
        }
    }

    private static void PatchExe(FileInfo exeFile)
    {
        // The dotnet base exes (e.g. NAPS2.exe) have a hard-coded path for the relevant dll (e.g. NAPS2.dll).
        // This path is also the path at which all the dependencies are searched. By default, the path is in the current
        // directory, but we can easily replace it with a subpath to the "lib" folder. (Note that the path is padded so
        // we don't even need to offset the bytes afterward.) This means everything other than the exes can live in
        // that "lib" subfolder once we do this patch.
        var bytes = File.ReadAllBytes(exeFile.FullName);
        var from = Path.ChangeExtension(exeFile.Name, ".dll");
        var to = @"lib\" + from;
        var fromBytes = Encoding.UTF8.GetBytes(from);
        var toBytes = Encoding.UTF8.GetBytes(to);
        var index = SearchBytes(bytes, fromBytes);
        if (bytes[(index - 4)..index] is [(byte) 'l', (byte) 'i', (byte) 'b', (byte) '\\'])
        {
            // Already patched
            return;
        }
        for (int i = 0; i < toBytes.Length; i++)
        {
            bytes[index + i] = toBytes[i];
        }
        File.WriteAllBytes(exeFile.FullName, bytes);
    }

    private static int SearchBytes(byte[] haystack, byte[] needle)
    {
        var len = needle.Length;
        var limit = haystack.Length - len;
        for (var i = 0; i <= limit; i++)
        {
            var k = 0;
            for (; k < len; k++)
            {
                if (needle[k] != haystack[i + k]) break;
            }
            if (k == len) return i;
        }
        return -1;
    }

    private static void AddPlatformFiles(PackageInfo pkgInfo, string buildPath, string platformPath)
    {
        var folder = new DirectoryInfo(Path.Combine(buildPath, platformPath));
        foreach (var file in folder.EnumerateFiles())
        {
            pkgInfo.AddFile(new PackageFile(file.DirectoryName ?? "", Path.Combine("lib", platformPath), file.Name));
        }
    }

    private static void AddPlatformFile(PackageInfo pkgInfo, string buildPath, string platformPath, string fileName)
    {
        pkgInfo.AddFile(new PackageFile(Path.Combine(buildPath, platformPath), Path.Combine("lib", platformPath),
            fileName));
    }
}