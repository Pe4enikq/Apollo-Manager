using System.Diagnostics;

namespace Walkman.L2.Codec.Pipeline;

/// <summary>
/// Isolated out-of-process runner for external LGPL/ISC codec CLIs (atracdenc.exe, vgmstream-cli.exe).
/// </summary>
internal static class ExternalCodecToolRunner
{
    public static string ResolveToolExecutable(string toolSubFolder, string executableName)
    {
        string[] searchRoots =
        [
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        ];

        foreach (var start in searchRoots)
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                string directTool = Path.Combine(current.FullName, "tools", toolSubFolder, executableName);
                if (File.Exists(directTool))
                {
                    return directTool;
                }

                current = current.Parent;
            }
        }

        throw new FileNotFoundException(
            $"External codec tool '{executableName}' was not found under /tools/{toolSubFolder}/.");
    }

    public static async Task RunProcessAsync(string exePath, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch external codec process '{exePath}'.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
        CancellationToken effectiveCt = timeoutCts.Token;

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(effectiveCt);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(effectiveCt);

        try
        {
            await process.WaitForExitAsync(effectiveCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort process tree termination on cancellation/timeout.
            }

            throw;
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"External tool '{Path.GetFileName(exePath)}' exited with code {process.ExitCode}. Stderr: {stderr} Stdout: {stdout}");
        }
    }
}
