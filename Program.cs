using System.Text.Json;

namespace TerminalRearranger;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Length == 2 && args[0] == "--inspect")
        {
            nint widget = Native.FindWindow(null, "Terminal Rearranger");
            var report = new
            {
                Widget = widget == 0 ? null : new
                {
                    Visible = Native.IsWindowVisible(widget),
                    Topmost = (Native.GetWindowLongPtr(widget, -20).ToInt64() & 8) != 0,
                    Bounds = Native.VisibleBounds(widget)
                },
                Displays = DisplayInfo.ReadAll(),
                FocusBorder = Enumerable.Range(1, 4).Select(i => Native.FindWindow(null, $"Terminal Rearranger Focus Border {i}"))
                    .Where(hwnd => hwnd != 0).Select(hwnd => new
                    {
                        Handle = hwnd.ToInt64(), Visible = Native.IsWindowVisible(hwnd),
                        Styles = Native.GetWindowLongPtr(hwnd, -20).ToInt64(), Bounds = Native.VisibleBounds(hwnd)
                    }),
                Terminals = TerminalWindows.Find().Select(w => new
                {
                    Handle = w.Handle.ToInt64(), w.ProcessId, w.ProcessName, w.ClassName,
                    Bounds = Native.VisibleBounds(w.Handle),
                    Minimized = Native.IsIconic(w.Handle),
                    Maximized = Native.IsZoomed(w.Handle)
                })
            };
            string output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        using var instance = new Mutex(true, @"Local\TerminalRearranger-" + Environment.UserName, out bool first);
        if (!first)
        {
            nint previous = Native.FindWindow(null, "Terminal Rearranger");
            if (previous != 0)
            {
                Native.ShowWindowAsync(previous, 4);
                Native.KeepOnTop(previous);
            }
            return;
        }

        Application.Run(new FloatingBar());
    }
}
