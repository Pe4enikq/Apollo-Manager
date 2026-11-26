using System.Buffers.Binary;
using System.Reflection;
using Walkman.Contracts.Plugins;
using Walkman.L3.AppShell;
using Walkman.L3.AppShell.Core;
using Walkman.L3.AppShell.Services;

namespace Walkman.M0.Tests;

public sealed class MilestoneM50ReleasePackagingTests
{
    [Fact]
    public void Cod25_AppShellAssembly_HasReleaseBrandingAndBetaVersion()
    {
        Assembly asm = typeof(App).Assembly;
        Assert.Equal("Apollo Manager", asm.GetName().Name);
        Assert.Equal(new Version(0, 1, 1, 0), asm.GetName().Version);

        string? infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.NotNull(infoVer);
        Assert.StartsWith("0.1.1b", infoVer, StringComparison.Ordinal);

        string? product = asm.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
        Assert.Equal("Apollo Manager", product);
    }

    [Fact]
    public void Cod25_AppUserSettings_DefaultsToLiveHardwareModeWithoutDemoOverlay()
    {
        var defaults = new AppUserSettings();
        Assert.False(defaults.IsDemoModeEnabled);
    }

    [Fact]
    public void Cod25_ResolvePluginAssemblyPath_ResolvesPackagedPluginsWithoutCodebaseRoot()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"ApolloPkgResolve_{Guid.NewGuid():N}");
        try
        {
            string pluginDir = Path.Combine(tempRoot, "plugins", "Walkman.L2.Codec.Pipeline");
            Directory.CreateDirectory(pluginDir);
            string dllPath = Path.Combine(pluginDir, "Walkman.L2.Codec.Pipeline.dll");
            File.WriteAllBytes(dllPath, [0x4D, 0x5A]);

            string? resolved = WalkmanBackendService.ResolvePluginAssemblyPath(
                tempRoot,
                codebaseRoot: null,
                configuration: "Release",
                moduleId: WellKnownModules.L2CodecPipeline);

            Assert.Equal(dllPath, resolved);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Cod25_BrandIconAssets_ExistWithAllSevenMultiResIcoFrames()
    {
        string codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        string assetsDir = Path.Combine(codebaseRoot, "src", "L3.Presentation", "Walkman.L3.AppShell", "Assets");
        string icoPath = Path.Combine(assetsDir, "apollo-icon.ico");
        string pngPath = Path.Combine(assetsDir, "apollo-icon.png");

        Assert.True(File.Exists(icoPath), $"Missing {icoPath}");
        Assert.True(File.Exists(pngPath), $"Missing {pngPath}");

        byte[] icoBytes = File.ReadAllBytes(icoPath);
        Assert.True(icoBytes.Length > 6 + 7 * 16);
        ushort reserved = BinaryPrimitives.ReadUInt16LittleEndian(icoBytes.AsSpan(0, 2));
        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(icoBytes.AsSpan(2, 2));
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(icoBytes.AsSpan(4, 2));

        Assert.Equal(0, reserved);
        Assert.Equal(1, type);
        Assert.Equal(7, count);

        int[] widths = new int[count];
        for (int i = 0; i < count; i++)
        {
            byte w = icoBytes[6 + i * 16];
            widths[i] = w == 0 ? 256 : w;
        }

        Assert.Equal([16, 24, 32, 48, 64, 128, 256], widths);
    }
}
