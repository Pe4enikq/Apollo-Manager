using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Walkman.L3.AppShell.Services;
using Walkman.L3.AppShell.ViewModels;

namespace Walkman.L3.AppShell;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string[] args = desktop.Args ?? Array.Empty<string>();
            bool autostart = args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);
            string? captureDir = ParseCaptureDir(args);

            var backend = new WalkmanBackendService();
            var player = new AudioPlaybackService();
            var vm = new MainWindowViewModel(
                backend,
                player,
                autostart: autostart || captureDir is not null,
                suppressSettingsSave: captureDir is not null);
            var window = new MainWindow(vm, backend, captureDir);

            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) =>
            {
                vm.Dispose();
                backend.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string? ParseCaptureDir(string[] args)
    {
        string? raw = args.FirstOrDefault(a => a.StartsWith("--capture-screens=", StringComparison.OrdinalIgnoreCase));
        if (raw is null) return null;
        string path = raw["--capture-screens=".Length..].Trim('"');
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }
}
