using Walkman.Contracts.Engines;

namespace Walkman.Contracts.Messaging;

/// <summary>
/// Mediator command sent by L3.DumpWizard and handled by L2.Telemetry.Dumper
/// to capture a sanitized ~50 KB diagnostic archive from a mounted player.
/// </summary>
public sealed record GenerateDiagnosticDumpCommand(
    DiagnosticDumpRequest Request) : ICommand<DiagnosticDumpResult>;
