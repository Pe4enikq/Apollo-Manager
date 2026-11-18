using System.Reflection;
using System.Runtime.Loader;
using Walkman.Contracts.Plugins;

namespace Walkman.L1.Orchestrator.Plugins;

/// <summary>
/// Collectible AssemblyLoadContext isolating an individual L2/L3 plugin assembly while delegating
/// Walkman.Contracts resolution to the default host context to preserve shared interface type identity.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string ContractsAssemblyName =
        typeof(IWalkmanPlugin).Assembly.GetName().Name!;

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string scopeName, string pluginAssemblyPath)
        : base(name: scopeName, isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginAssemblyPath);
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Returning null for Walkman.Contracts delegates resolution to AssemblyLoadContext.Default
        // so plugin types implement the exact same contract interfaces as L1 Core.
        if (string.Equals(assemblyName.Name, ContractsAssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is not null ? LoadFromAssemblyPath(assemblyPath) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is not null ? LoadUnmanagedDllFromPath(libraryPath) : IntPtr.Zero;
    }
}
