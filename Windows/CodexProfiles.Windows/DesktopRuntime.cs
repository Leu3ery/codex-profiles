using System.Diagnostics;

namespace CodexProfiles.Windows;

internal static class DesktopRuntime
{
    public static async Task<string?> QuitAsync(CancellationToken cancellationToken)
    {
        var processes = Process.GetProcessesByName("Codex");
        string? relaunch = null;
        try
        {
            foreach (var process in processes)
            {
                if (process.HasExited || process.MainWindowHandle == IntPtr.Zero) continue;
                relaunch ??= process.MainModule?.FileName;
                if (!process.CloseMainWindow()) throw new IOException("Close Codex manually before switching accounts.");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try { await Task.WhenAll(processes.Select(x => x.WaitForExitAsync(timeout.Token))); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new IOException("Codex is still running. Close its windows and CLI sessions, then try again. No credentials were changed."); }
            return relaunch;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static void Launch(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
