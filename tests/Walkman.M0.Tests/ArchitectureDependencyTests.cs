using System.Xml.Linq;
using Walkman.Contracts.Plugins;
using Walkman.L1.Orchestrator.Plugins;

namespace Walkman.M0.Tests;

/// <summary>
/// Enforces the L1/L2/L3 anti-spaghetti dependency rules and zero-third-party spine invariants.
/// </summary>
public sealed class ArchitectureDependencyTests
{
    private static readonly string CodebaseRoot = FindCodebaseRoot();

    [Fact]
    public void ContractsAssembly_HasZeroProjectAndPackageDependencies()
    {
        var csprojPath = Path.Combine(CodebaseRoot, "src", "Walkman.Contracts", "Walkman.Contracts.csproj");
        var (projects, packages) = ReadReferences(csprojPath);

        Assert.Empty(projects);
        Assert.Empty(packages);

        var referencedAssemblies = typeof(IWalkmanPlugin).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        Assert.All(referencedAssemblies, name =>
            Assert.True(name is "System.Runtime" or "System.Collections" or "netstandard",
                $"Unexpected reference '{name}' in Walkman.Contracts."));
    }

    [Fact]
    public void OrchestratorAssembly_HasZeroNuGetPackagesAndReferencesOnlyContracts()
    {
        var csprojPath = Path.Combine(
            CodebaseRoot,
            "src",
            "L1.Core",
            "Walkman.L1.Orchestrator",
            "Walkman.L1.Orchestrator.csproj");

        var (projects, packages) = ReadReferences(csprojPath);

        Assert.Empty(packages);
        var singleProject = Assert.Single(projects);
        Assert.Equal("Walkman.Contracts.csproj", singleProject);

        var referencedAssemblies = typeof(ModuleOrchestrator).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        Assert.DoesNotContain(referencedAssemblies, name =>
            name is not null &&
            (name.Contains("CommunityToolkit", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("MediatR", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Mediator", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void AllL2EngineProjects_ReferenceOnlyWalkmanContracts()
    {
        var l2Projects = Directory.GetFiles(
            Path.Combine(CodebaseRoot, "src", "L2.Engines"),
            "Walkman.L2.*.csproj",
            SearchOption.AllDirectories);

        Assert.Equal(6, l2Projects.Length);

        foreach (var csproj in l2Projects)
        {
            var (projects, _) = ReadReferences(csproj);
            var singleRef = Assert.Single(projects);
            Assert.Equal("Walkman.Contracts.csproj", singleRef);
        }
    }

    [Fact]
    public void DynamicL3PluginStudios_ReferenceOnlyWalkmanContracts()
    {
        string[] pluginStudios =
        [
            Path.Combine(CodebaseRoot, "src", "L3.Presentation", "Walkman.L3.ScreensaverStudio", "Walkman.L3.ScreensaverStudio.csproj"),
            Path.Combine(CodebaseRoot, "src", "L3.Presentation", "Walkman.L3.DumpWizard", "Walkman.L3.DumpWizard.csproj")
        ];

        foreach (var csproj in pluginStudios)
        {
            var (projects, _) = ReadReferences(csproj);
            var singleRef = Assert.Single(projects);
            Assert.Equal("Walkman.Contracts.csproj", singleRef);
        }
    }

    [Fact]
    public void AppShellHost_DoesNotDirectlyReferenceAnyL2OrDynamicL3Plugin()
    {
        var appShellCsproj = Path.Combine(
            CodebaseRoot,
            "src",
            "L3.Presentation",
            "Walkman.L3.AppShell",
            "Walkman.L3.AppShell.csproj");

        var (projects, _) = ReadReferences(appShellCsproj);

        Assert.DoesNotContain(projects, p =>
            p.Contains("Walkman.L2.", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("ScreensaverStudio", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("DumpWizard", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("Walkman.Contracts.csproj", projects);
        Assert.Contains("Walkman.L1.Orchestrator.csproj", projects);
    }

    private static (string[] ProjectRefs, string[] PackageRefs) ReadReferences(string csprojPath)
    {
        Assert.True(File.Exists(csprojPath), $"Expected csproj at '{csprojPath}'.");
        var doc = XDocument.Load(csprojPath);

        var projectRefs = doc.Descendants("ProjectReference")
            .Select(el => Path.GetFileName(el.Attribute("Include")?.Value ?? string.Empty))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        var packageRefs = doc.Descendants("PackageReference")
            .Select(el => el.Attribute("Include")?.Value ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        return (projectRefs, packageRefs);
    }

    internal static string FindCodebaseRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WalkmanUtility.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate WalkmanUtility.sln above test execution directory.");
    }
}
