using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TerminalRearranger;

internal sealed class CodexFork
{
    internal static string Executable => Environment.ProcessPath!;

    internal static async Task<List<CodexSession>> ReadSessionsAsync(CancellationToken cancellationToken, string? helper = null)
    {
        string output = Path.Combine(Path.GetTempPath(), $"terminal-rearranger-sessions-{Guid.NewGuid():N}.json");
        try
        {
            var start = new ProcessStartInfo(helper ?? Executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--codex-sessions");
            start.ArgumentList.Add(output);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not read Codex sessions.");
            try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(8), cancellationToken); }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(CancellationToken.None); } }
            if (process.ExitCode != 0 || !File.Exists(output)) throw new InvalidOperationException("Could not read Codex sessions.");
            return JsonSerializer.Deserialize<List<CodexSession>>(await File.ReadAllTextAsync(output, cancellationToken)) ?? [];
        }
        finally { TryDelete(output); }
    }

    internal static ProcessStartInfo LaunchCommand(CodexSession session, string helper, string report)
    {
        // Encoding the script keeps paths, quotes, Unicode, and semicolons out of
        // both wt's command parser and PowerShell's command-line parser.
        string script = $"$env:CODEX_HOME = {Quote(session.CodexHome)}; " +
            $"Set-Location -LiteralPath {Quote(session.Directory)}; " +
            $"& {Quote(helper)} --report-console $PID {Quote(report)}; " +
            $"& {Quote(session.Executable)} fork {Quote(session.Id.ToString())} --cd {Quote(session.Directory)}";
        string terminal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");
        var start = new ProcessStartInfo(terminal) { UseShellExecute = false, CreateNoWindow = true };
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        foreach (string argument in new[] { "-w", "new", "new-tab", "--useApplicationTitle", shell,
            "-NoLogo", "-NoProfile", "-NoExit", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        return start;
    }

    internal static async Task<nint> OpenAsync(CodexSession session, CancellationToken cancellationToken, string? helper = null)
    {
        if (!System.IO.Directory.Exists(session.Directory) || !File.Exists(session.Executable))
            throw new InvalidOperationException("The source session's directory or Codex executable is unavailable.");
        var before = TerminalWindows.Find().Select(w => w.Handle).ToHashSet();
        string report = Path.Combine(Path.GetTempPath(), $"terminal-rearranger-window-{Guid.NewGuid():N}.json");
        try
        {
            using var launcher = Process.Start(LaunchCommand(session, helper ?? Executable, report))
                ?? throw new InvalidOperationException("Windows Terminal could not be opened.");
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(report) && long.TryParse(await File.ReadAllTextAsync(report, cancellationToken), out long handle))
                {
                    nint window = new(handle);
                    if (!before.Contains(window) && TerminalWindows.Find().Any(w => w.Handle == window))
                    {
                        // Let the console finish its initial size negotiation before tiling.
                        await Task.Delay(450, cancellationToken);
                        return window;
                    }
                }
                if (launcher.HasExited && launcher.ExitCode != 0)
                    throw new InvalidOperationException("Windows Terminal could not be opened.");
                await Task.Delay(100, cancellationToken);
            }
            throw new TimeoutException("The new terminal did not become ready. Use the monitor button to refresh.");
        }
        finally { TryDelete(report); }
    }

    internal static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
