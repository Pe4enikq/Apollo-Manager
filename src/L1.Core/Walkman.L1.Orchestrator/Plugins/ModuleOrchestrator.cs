using System.Runtime.CompilerServices;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Messaging;
using Walkman.Contracts.Plugins;
using Walkman.L1.Orchestrator.Messaging;

namespace Walkman.L1.Orchestrator.Plugins;

/// <summary>
/// L1 runtime orchestrator coordinating dynamic loading, contract-based initialization,
/// and deterministic garbage-collectible unloading of L2/L3 plugin scopes.
/// </summary>
public sealed class ModuleOrchestrator : IModuleOrchestrator
{
    private sealed class ScopeRecord
    {
        public ScopeRecord(string scopeId) => ScopeId = scopeId;
        public string ScopeId { get; }
        public List<string> ModuleIds { get; } = new();
        public List<PluginLoadContext> LoadContexts { get; } = new();
        public List<IWalkmanPlugin> Plugins { get; } = new();
    }

    private sealed record RegisteredEngineEntry(string ScopeId, object Implementation);

    private readonly IWalkmanEventBus _eventBus;
    private readonly WalkmanMediator _mediator;
    private readonly Func<string, string?> _moduleAssemblyPathResolver;

    private readonly object _gate = new();
    private readonly Dictionary<string, ScopeRecord> _scopes = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, RegisteredEngineEntry> _engineServices = new();

    public ModuleOrchestrator(
        IWalkmanEventBus eventBus,
        WalkmanMediator mediator,
        Func<string, string?> moduleAssemblyPathResolver)
    {
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _moduleAssemblyPathResolver = moduleAssemblyPathResolver ?? throw new ArgumentNullException(nameof(moduleAssemblyPathResolver));
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> LoadedModuleIds
    {
        get
        {
            lock (_gate)
            {
                return _scopes.Values
                    .SelectMany(s => s.ModuleIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    /// <summary>
    /// Branch B: Loads the exact L2 engines and L3 studios required by a resolved device profile
    /// under a dedicated device scope and broadcasts DeviceConnectedEvent.
    /// </summary>
    public async Task<IReadOnlyList<string>> HydrateForDeviceAsync(
        ConnectedDeviceIdentity device,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        var scopeId = BuildDeviceScopeId(device.DeviceInstanceId);
        var requestedModules = device.Profile.RequiredL2Modules
            .Concat(device.Profile.SupportedL3Modules)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var newlyLoaded = new List<string>();
        foreach (var moduleId in requestedModules)
        {
            if (await LoadModuleInternalAsync(moduleId, scopeId, ct).ConfigureAwait(false))
            {
                newlyLoaded.Add(moduleId);
            }
        }

        _eventBus.Publish(new DeviceConnectedEvent(device, DateTimeOffset.UtcNow));
        return newlyLoaded;
    }

    /// <summary>
    /// Branch C: Hydrates telemetry collection and dump wizard modules for unrecognized Sony hardware.
    /// </summary>
    public async Task<IReadOnlyList<string>> HydrateForUnrecognizedDeviceAsync(
        DeviceDetectionProbe probe,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);

        const string scopeId = "dump-wizard";
        string[] modules =
        [
            WellKnownModules.L2TelemetryDumper,
            WellKnownModules.L3DumpWizard
        ];

        var newlyLoaded = new List<string>();
        foreach (var moduleId in modules)
        {
            if (await LoadModuleInternalAsync(moduleId, scopeId, ct).ConfigureAwait(false))
            {
                newlyLoaded.Add(moduleId);
            }
        }

        var reason = $"Unrecognized Sony device VID=0x{probe.VendorId:X4}, PID=0x{probe.ProductId:X4}";
        _eventBus.Publish(new UnrecognizedSonyDeviceDetectedEvent(probe, reason, DateTimeOffset.UtcNow));
        return newlyLoaded;
    }

    /// <inheritdoc />
    public async Task LoadModuleAsync(string moduleId, string scopeId, CancellationToken ct = default)
    {
        await LoadModuleInternalAsync(moduleId, scopeId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UnloadScopeAsync(string scopeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);

        ScopeRecord? scope;
        lock (_gate)
        {
            if (!_scopes.Remove(scopeId, out scope))
            {
                return;
            }
        }

        var unloadedModules = scope.ModuleIds.ToArray();

        // Shut down plugins in reverse initialization order before releasing scope handlers and ALCs.
        for (int i = scope.Plugins.Count - 1; i >= 0; i--)
        {
            await scope.Plugins[i].ShutdownAsync(ct).ConfigureAwait(false);
        }

        RemoveScopedEngineServices(scopeId);
        _mediator.UnregisterScope(scopeId);
        _eventBus.UnsubscribeScope(scopeId);

        scope.Plugins.Clear();
        scope.ModuleIds.Clear();

        foreach (var context in scope.LoadContexts)
        {
            context.Unload();
        }

        scope.LoadContexts.Clear();

        foreach (var moduleId in unloadedModules)
        {
            _eventBus.Publish(new ModuleUnloadedEvent(moduleId, scopeId));
        }
    }

    /// <inheritdoc />
    public TService? TryResolveEngine<TService>() where TService : class
    {
        lock (_gate)
        {
            return _engineServices.TryGetValue(typeof(TService), out var entry)
                ? (TService)entry.Implementation
                : null;
        }
    }

    /// <inheritdoc />
    public TService ResolveRequiredEngine<TService>() where TService : class
    {
        return TryResolveEngine<TService>()
            ?? throw new InvalidOperationException(
                $"Required engine service '{typeof(TService).FullName}' is not currently loaded.");
    }

    internal IReadOnlyList<WeakReference> GetScopeContextWeakReferences(string scopeId)
    {
        lock (_gate)
        {
            if (!_scopes.TryGetValue(scopeId, out var scope))
            {
                return Array.Empty<WeakReference>();
            }

            return scope.LoadContexts
                .Select(static alc => new WeakReference(alc, trackResurrection: true))
                .ToArray();
        }
    }

    private static string BuildDeviceScopeId(string deviceInstanceId) => $"device:{deviceInstanceId}";

    private async Task<bool> LoadModuleInternalAsync(string moduleId, string scopeId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);

        lock (_gate)
        {
            if (_scopes.TryGetValue(scopeId, out var existingScope) &&
                existingScope.ModuleIds.Contains(moduleId, StringComparer.Ordinal))
            {
                return false;
            }
        }

        var assemblyPath = _moduleAssemblyPathResolver(moduleId);
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
        {
            throw new FileNotFoundException(
                $"Could not resolve plugin assembly for module '{moduleId}'. Resolved path: '{assemblyPath ?? "<null>"}'.",
                assemblyPath);
        }

        var fullAssemblyPath = Path.GetFullPath(assemblyPath);
        var (alc, plugin) = ActivatePluginFromPath(moduleId, scopeId, fullAssemblyPath);

        if (!string.Equals(plugin.ModuleId, moduleId, StringComparison.Ordinal))
        {
            alc.Unload();
            throw new InvalidOperationException(
                $"Plugin assembly '{fullAssemblyPath}' reported ModuleId '{plugin.ModuleId}' instead of expected '{moduleId}'.");
        }

        var context = new PluginRegistrationContext(this, _eventBus, _mediator, scopeId);
        await plugin.InitializeAsync(context, ct).ConfigureAwait(false);

        lock (_gate)
        {
            if (!_scopes.TryGetValue(scopeId, out var scope))
            {
                scope = new ScopeRecord(scopeId);
                _scopes[scopeId] = scope;
            }

            scope.ModuleIds.Add(moduleId);
            scope.LoadContexts.Add(alc);
            scope.Plugins.Add(plugin);
        }

        _eventBus.Publish(new ModuleLoadedEvent(plugin.ModuleId, plugin.Tier, scopeId));
        return true;
    }

    // Isolated without inlining so local Reflection objects do not keep the collectible ALC rooted on the stack.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PluginLoadContext Context, IWalkmanPlugin Plugin) ActivatePluginFromPath(
        string moduleId,
        string scopeId,
        string fullAssemblyPath)
    {
        var alc = new PluginLoadContext($"{scopeId}:{moduleId}", fullAssemblyPath);
        var assembly = alc.LoadFromAssemblyPath(fullAssemblyPath);
        var pluginContract = typeof(IWalkmanPlugin);

        var pluginTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && pluginContract.IsAssignableFrom(t))
            .ToArray();

        if (pluginTypes.Length != 1)
        {
            alc.Unload();
            throw new InvalidOperationException(
                $"Plugin assembly '{fullAssemblyPath}' must expose exactly one concrete IWalkmanPlugin type (found {pluginTypes.Length}).");
        }

        var instance = Activator.CreateInstance(pluginTypes[0]) as IWalkmanPlugin
            ?? throw new InvalidOperationException($"Failed to instantiate plugin type '{pluginTypes[0].FullName}'.");

        return (alc, instance);
    }

    private void RegisterEngineService<TService>(string scopeId, TService implementation) where TService : class
    {
        ArgumentNullException.ThrowIfNull(implementation);

        lock (_gate)
        {
            _engineServices[typeof(TService)] = new RegisteredEngineEntry(scopeId, implementation);
        }
    }

    private void RemoveScopedEngineServices(string scopeId)
    {
        lock (_gate)
        {
            var keysToRemove = _engineServices
                .Where(kvp => string.Equals(kvp.Value.ScopeId, scopeId, StringComparison.Ordinal))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _engineServices.Remove(key);
            }
        }
    }

    private sealed class PluginRegistrationContext : IPluginRegistrationContext
    {
        private readonly ModuleOrchestrator _orchestrator;
        private readonly WalkmanMediator _mediator;

        public PluginRegistrationContext(
            ModuleOrchestrator orchestrator,
            IWalkmanEventBus eventBus,
            WalkmanMediator mediator,
            string scopeId)
        {
            _orchestrator = orchestrator;
            EventBus = eventBus;
            _mediator = mediator;
            ScopeId = scopeId;
        }

        public string ScopeId { get; }

        public IWalkmanEventBus EventBus { get; }

        public IWalkmanMediator Mediator => _mediator;

        public void RegisterEngineService<TService>(TService implementation) where TService : class
        {
            _orchestrator.RegisterEngineService(ScopeId, implementation);
        }

        public void RegisterCommandHandler<TCommand, TResponse>(ICommandHandler<TCommand, TResponse> handler)
            where TCommand : ICommand<TResponse>
        {
            _mediator.RegisterCommandHandler(handler, ScopeId);
        }

        public void RegisterQueryHandler<TQuery, TResponse>(IQueryHandler<TQuery, TResponse> handler)
            where TQuery : IQuery<TResponse>
        {
            _mediator.RegisterQueryHandler(handler, ScopeId);
        }

        public IDisposable SubscribeEvent<TEvent>(Action<TEvent> handler) where TEvent : class, IEvent
        {
            return EventBus.Subscribe(handler, ScopeId);
        }
    }
}
