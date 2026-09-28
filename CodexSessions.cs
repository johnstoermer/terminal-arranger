using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace TerminalRearranger;

internal sealed record CodexSession(long Window, int ProcessId, Guid Id, string Directory,
    string Executable, string CodexHome, string ConsoleTitle);

internal static class CodexSessions
{
    // Run in a short-lived helper: console attachment is process-wide and must never
    // change the widget's console, standard handles, or Ctrl+C handlers.
    internal static List<CodexSession> Scan()
    {
        var terminals = TerminalWindows.Find().Select(w => w.Handle).ToHashSet();
        var sessions = new List<CodexSession>();
        foreach (var process in Process.GetProcessesByName("codex"))
        {
            using (process)
            {
                try
                {
                    var console = ConsoleOwner(process.Id);
                    if (!terminals.Contains(console.Window)) continue;
                    string? executable = process.MainModule?.FileName;
                    if (executable == null) continue;
                    foreach (string path in OpenRollouts(process.Id))
                    {
                        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(file);
                        var line = new StringBuilder();
                        // Only the metadata header is read; conversation messages are not needed.
                        while (line.Length < 2 * 1024 * 1024 && reader.Read() is int c && c >= 0 && c != '\n')
                            line.Append((char)c);
                        var session = ParseHeader(line.ToString(), path, console.Window.ToInt64(),
                            process.Id, executable, console.Title);
                        if (session != null) sessions.Add(session);
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException
                    or UnauthorizedAccessException or JsonException or ArgumentException or KeyNotFoundException) { }
            }
        }
        return sessions.DistinctBy(s => (s.ProcessId, s.Id)).ToList();
    }

    internal static CodexSession? ParseHeader(string line, string path, long window, int pid,
        string executable, string title)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.GetProperty("type").GetString() != "session_meta") return null;
        var meta = root.GetProperty("payload");
        // A fork contains copied metadata further down the file. Its first header
        // identifies the new session. Subagent rollouts must never be offered.
        if (!meta.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String ||
            source.GetString() != "cli") return null;
        if (meta.TryGetProperty("agent_path", out var agent) && agent.ValueKind == JsonValueKind.String &&
            agent.GetString() is not (null or "/root")) return null;
        if (!Guid.TryParse(meta.GetProperty("id").GetString(), out var id)) return null;
        string? cwd = meta.GetProperty("cwd").GetString();
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd)) return null;
        var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (directory != null && directory.Name != "sessions") directory = directory.Parent;
        if (directory?.Parent == null) return null;
        return new CodexSession(window, pid, id, cwd, executable, directory.Parent.FullName, title);
    }

    internal static string StableTitle(string title)
    {
        // Codex animates a leading braille spinner. It can change between these two
        // native title reads even though the selected tab has not changed.
        if (title.Length > 1 && title[1] == ' ' &&
            (title[0] is >= '\u2800' and <= '\u28ff' or '\u25cf' or '\u25cb'))
            return title[2..];
        return title;
    }

    internal static CodexSession Select(IEnumerable<CodexSession> sessions, nint window, string title)
    {
        var candidates = sessions.Where(s => s.Window == window.ToInt64()).ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException("No local Codex session found in this terminal.");
        var matches = candidates.Where(s => StableTitle(s.ConsoleTitle) == StableTitle(title)).ToList();
        if (matches.Count == 1) return matches[0];
        throw new InvalidOperationException(matches.Count > 1
            ? "Several Codex sessions have this title. Move the source tab to its own window."
            : "Select the Codex tab and use its default title, then try again.");
    }

    internal static (nint Window, string Title) ConsoleOwner(int processId)
    {
        FreeConsole();
        if (!AttachConsole((uint)processId)) return default;
        try
        {
            nint window = GetConsoleWindow();
            for (int i = 0; i < 8; i++)
            {
                nint owner = Native.GetWindow(window, 4);
                if (owner == 0) break;
                window = owner;
            }
            var title = new StringBuilder(4096);
            GetConsoleTitle(title, title.Capacity);
            return (window, title.ToString());
        }
        finally { FreeConsole(); }
    }

    private static IEnumerable<string> OpenRollouts(int processId)
    {
        using var process = OpenProcess(0x0440, false, processId); // Query + duplicate handles; no write access.
        if (process.IsInvalid) yield break;
        int size = 65536;
        nint buffer = 0;
        try
        {
            int status;
            do
            {
                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size);
                status = NtQueryInformationProcess(process, 51, buffer, size, out int needed);
                if (status >= 0) break;
                if (status != unchecked((int)0xc0000004) || size >= 16 * 1024 * 1024) yield break;
                size = Math.Min(16 * 1024 * 1024, Math.Max(size * 2, needed));
            } while (true);
            int entrySize = Marshal.SizeOf<HandleEntry>();
            long count = Marshal.ReadIntPtr(buffer).ToInt64();
            if (count < 0 || count > (size - 2 * IntPtr.Size) / entrySize) yield break;
            for (int i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<HandleEntry>(buffer + 2 * IntPtr.Size + i * entrySize);
                // The active recorder holds write/append access. Ignore a temporary
                // read handle for an older session during /resume or /fork.
                if ((entry.Access & 6) == 0 || !DuplicateHandle(process, entry.Handle,
                    new nint(-1), out var duplicate, 0, false, 2)) continue;
                using (duplicate)
                {
                    if (GetFileType(duplicate) != 1) continue; // Never query named pipes.
                    var path = new StringBuilder(32768);
                    uint length = GetFinalPathNameByHandle(duplicate, path, (uint)path.Capacity, 0);
                    if (length == 0 || length >= path.Capacity) continue;
                    string value = path.ToString();
                    if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) value = @"\\" + value[8..];
                    else if (value.StartsWith(@"\\?\", StringComparison.Ordinal)) value = value[4..];
                    if (Path.GetFileName(value).StartsWith("rollout-", StringComparison.Ordinal) &&
                        value.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) yield return value;
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleEntry
    {
        public nint Handle, HandleCount, PointerCount;
        public uint Access, Type, Attributes, Reserved;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetConsoleTitleW")]
    private static extern uint GetConsoleTitle(StringBuilder title, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass,
        nint buffer, int size, out int needed);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(SafeProcessHandle process, nint source, nint targetProcess,
        out SafeFileHandle target, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(SafeFileHandle file);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
}
