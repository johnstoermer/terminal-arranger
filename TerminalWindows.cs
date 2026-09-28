using System.Diagnostics;
using System.Text;

namespace TerminalRearranger;

internal sealed record TerminalWindow(nint Handle, uint ProcessId, string ProcessName, string ClassName);

internal static class TerminalWindows
{
    private static readonly HashSet<string> TerminalClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CASCADIA_HOSTING_WINDOW_CLASS", "ConsoleWindowClass", "mintty", "PuTTY",
        "KiTTY", "Alacritty", "org.wezfurlong.wezterm", "VirtualConsoleClassMain"
    };
    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "WindowsTerminalPreview", "mintty", "putty", "kitty",
        "wezterm-gui", "alacritty", "ConEmu64", "ConEmu", "Hyper", "Tabby", "ghostty"
    };

    internal static bool IsTerminal(string className, string processName) =>
        TerminalClasses.Contains(className) || TerminalProcesses.Contains(processName);

    internal static List<TerminalWindow> Find()
    {
        var windows = new List<TerminalWindow>();
        var processes = new Dictionary<uint, string>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, 4) != 0) return true;
            if ((Native.GetWindowLongPtr(hwnd, -20).ToInt64() & 0x80) != 0) return true;
            if (Native.GetDwmInt(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId) return true;
            var classBuffer = new StringBuilder(256);
            Native.GetClassName(hwnd, classBuffer, classBuffer.Capacity);
            string className = classBuffer.ToString();
            if (!processes.TryGetValue(pid, out string? processName))
            {
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    processName = process.ProcessName;
                }
                catch (ArgumentException) { return true; }
                catch (System.ComponentModel.Win32Exception) { processName = ""; }
                catch (InvalidOperationException) { return true; }
                processes[pid] = processName;
            }
            if (IsTerminal(className, processName))
                windows.Add(new TerminalWindow(hwnd, pid, processName, className));
            return true;
        }, 0);
        return windows.OrderBy(w => w.ProcessId).ThenBy(w => w.Handle.ToInt64()).ToList();
    }
}
