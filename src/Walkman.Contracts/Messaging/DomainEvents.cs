using Walkman.Contracts.Devices;
using Walkman.Contracts.Plugins;

namespace Walkman.Contracts.Messaging;

/// <summary>
/// Raised after a known Sony Walkman is resolved and bound to an active device instance.
/// </summary>
public sealed record DeviceConnectedEvent(
    ConnectedDeviceIdentity Device,
    DateTimeOffset TimestampUtc) : IEvent;

/// <summary>
/// Raised when an active Walkman volume disconnects or its bound serial disappears.
/// </summary>
public sealed record DeviceDisconnectedEvent(
    string DeviceInstanceId,
    uint? VolumeSerialNumber,
    DateTimeOffset TimestampUtc) : IEvent;

/// <summary>
/// Raised when a Sony 0x054C device or unknown OMGAUDIO volume triggers the diagnostic dump flow.
/// </summary>
public sealed record UnrecognizedSonyDeviceDetectedEvent(
    DeviceDetectionProbe Probe,
    string Reason,
    DateTimeOffset TimestampUtc) : IEvent;

/// <summary>
/// Raised whenever an L2 or L3 plugin module is initialized inside a plugin scope.
/// </summary>
public sealed record ModuleLoadedEvent(
    string ModuleId,
    PluginTier Tier,
    string ScopeId) : IEvent;

/// <summary>
/// Raised after a plugin scope's handlers, services, and AssemblyLoadContext are unloaded.
/// </summary>
public sealed record ModuleUnloadedEvent(
    string ModuleId,
    string ScopeId) : IEvent;

/// <summary>
/// Progress update broadcast during queued audio transcoding and device synchronization.
/// </summary>
public sealed record TransferProgressEvent(
    string JobId,
    int CompletedTracks,
    int TotalTracks,
    string CurrentTrackTitle,
    double PercentComplete) : IEvent;
