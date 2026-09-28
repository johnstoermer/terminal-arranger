using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;
using TerminalRearranger;

internal static partial class Checks
{
    private static int assertions;
    private static readonly List<Form> samples = [];
    private static readonly List<object> results = [];
    private static nint simulatedForeground;
    private static string ArtifactPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts"));

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "fork") return FakeCodex(args);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            Geometry(); Classification(); ForkSelection();
            Directory.CreateDirectory(ArtifactPath);
            BorderPixels();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        Directory.CreateDirectory(ArtifactPath);
        bool interactive = args.Contains("--ui");
        int exitCode = 0;
        using var bar = new FloatingBar(FindSamples, persist: false, foregroundWindow: interactive ? null : () => simulatedForeground)
        {
            Text = "Terminal Rearranger - UI test",
            ShowInTaskbar = true
        };
        using var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) => WriteSnapshot(bar);
        bar.Shown += async (_, _) =>
        {
            try
            {
                AddSample(); AddSample(); AddSample();
                samples[0].WindowState = FormWindowState.Maximized;
                samples[1].WindowState = FormWindowState.Minimized;
                if (interactive) { timer.Start(); return; }

                foreach (var display in DisplayInfo.ReadAll())
                {
                    await ClickMonitor(bar, display);
                    VerifyPlacement(display, 3, bar);
                    Assert(!samples.Any(s => Native.IsIconic(s.Handle) || Native.IsZoomed(s.Handle)), "Restore minimized and maximized windows");

                    // A newly opened window must be included on a repeat click.
                    AddSample();
                    await ClickMonitor(bar, display);
                    VerifyPlacement(display, 4, bar);
                    samples[^1].Dispose(); samples.RemoveAt(samples.Count - 1);

                    // Moving a window by hand must be corrected on another click.
                    samples[0].Bounds = new Rectangle(display.WorkingArea.Left + 60, display.WorkingArea.Top + 100, 360, 240);
                    await ClickMonitor(bar, display);
                    VerifyPlacement(display, 3, bar);
                    var first = samples.Select(s => Native.VisibleBounds(s.Handle)).ToArray();
                    await ClickMonitor(bar, display);
                    Assert(first.SequenceEqual(samples.Select(s => Native.VisibleBounds(s.Handle))), "Repeated click keeps a stable arrangement");

                    // Seven windows exercise a partially occupied grid on both monitors.
                    while (samples.Count < 7) AddSample();
                    await ClickMonitor(bar, display);
                    VerifyPlacement(display, 7, bar);
                    while (samples.Count > 3) { samples[^1].Dispose(); samples.RemoveAt(samples.Count - 1); }
                    await VerifyFocusBorder(bar, display);
                    results.Add(new { Display = display.Number, display.Bounds, display.WorkingArea, Status = "passed" });
                }
                Assert((Native.GetWindowLongPtr(bar.Handle, -20).ToInt64() & 8) != 0, "Widget has topmost window style");
                Assert(!TerminalWindows.Find().Any(w => samples.Any(s => s.Handle == w.Handle)), "Real terminal discovery excludes ordinary application windows");
                await VerifyForkButtons();
                await VerifyTerminalLaunch();
                var empty = await new WindowArranger(() => []).ArrangeAsync(DisplayInfo.ReadAll()[0]);
                Assert(empty.Found == 0 && empty.Arranged == 0, "Empty desktop result");
                WriteSnapshot(bar);
                var overlayHandles = bar.FocusBorder.Strips.Select(strip => strip.Handle).ToArray();
                bar.FocusBorder.Dispose();
                Assert(overlayHandles.All(hwnd => !Native.IsWindow(hwnd)), "Closing the controller destroys all overlay windows");
                Console.WriteLine($"PASS: {assertions} assertions; {DisplayInfo.ReadAll().Count} physical monitors; arrangement, border, fork selection, fork buttons, and Windows Terminal launch.");
                File.WriteAllText(Path.Combine(ArtifactPath, "test-results.json"), JsonSerializer.Serialize(new { Status = "passed", Assertions = assertions, Displays = results }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                exitCode = 1;
                Console.Error.WriteLine(ex);
                Console.Error.WriteLine(JsonSerializer.Serialize(new { Foreground = Native.GetForegroundWindow().ToInt64(),
                    Target = bar.FocusBorder.TrackedWindow.ToInt64(), bar.FocusBorder.LastError,
                    Samples = samples.Select(s => s.Handle.ToInt64()),
                    Strips = bar.FocusBorder.Strips.Select(s => new { Handle = s.Handle.ToInt64(), s.Visible }) }));
                WriteSnapshot(bar);
                File.WriteAllText(Path.Combine(ArtifactPath, "test-results.json"), JsonSerializer.Serialize(new { Status = "failed", Error = ex.ToString() }));
            }
            finally
            {
                if (!interactive)
                {
                    foreach (var sample in samples) sample.Dispose();
                    samples.Clear();
                    bar.Close();
                }
            }
        };
        Application.Run(bar);
        foreach (var sample in samples) sample.Dispose();
        return exitCode;
    }

    private static void AddSample()
    {
        int number = samples.Count + 1;
        var sample = new Form
        {
            Text = $"Rearranger test window {number}",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(100 + number * 60, 180 + number * 60, 460, 300),
            BackColor = Color.FromArgb(20 + number * 6, 28 + number * 8, 40 + number * 10),
            MinimumSize = new Size(120, 80)
        };
        sample.Controls.Add(new Label
        {
            Text = number.ToString(), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(111, 224, 195), Font = new Font("Segoe UI", 32)
        });
        sample.Show();
        samples.Add(sample);
    }

    private static List<TerminalWindow> FindSamples() => samples.Where(f => !f.IsDisposed)
        .Select(f => new TerminalWindow(f.Handle, (uint)Environment.ProcessId, "test-window", "test-window")).ToList();

    private static async Task ClickMonitor(FloatingBar bar, DisplayInfo display)
    {
        bar.Controls.OfType<MonitorButton>().Single(b => b.Number == display.Number).PerformClick();
        for (int i = 0; i < 100 && bar.Busy; i++) await Task.Delay(40);
        Assert(!bar.Busy, "Arrangement completes within 4 seconds");
        Assert(bar.LastResult != null, "Monitor button dispatches arrangement");
    }

    private static void VerifyPlacement(DisplayInfo display, int expected, FloatingBar bar)
    {
        Assert(bar.LastResult?.Found == expected && bar.LastResult.Arranged == expected,
            $"All {expected} windows arranged on display {display.Number}: {bar.LastResult}");
        var frames = samples.Select(f => Native.VisibleBounds(f.Handle)).ToArray();
        Assert(frames.Max(f => f.Width) - frames.Min(f => f.Width) <= 1, "Native windows retain equal visible widths across all rows");
        foreach (var frame in frames)
        {
            var allowed = Rectangle.Inflate(display.WorkingArea, 2, 2);
            Assert(allowed.Contains(frame), $"Frame is inside display work area: {frame}; allowed {allowed}");
        }
        for (int i = 0; i < frames.Length; i++)
            for (int j = i + 1; j < frames.Length; j++)
                Assert(!frames[i].IntersectsWith(frames[j]), "Native frames do not overlap");
    }

    private static void WriteSnapshot(FloatingBar bar)
    {
        var snapshot = new
        {
            bar.Busy, bar.LastResult,
            Focus = new { Target = bar.FocusBorder.TrackedWindow.ToInt64(), bar.FocusBorder.TargetBounds,
                VisibleStrips = bar.FocusBorder.Strips.Count(s => s.Visible), bar.FocusBorder.LastError },
            Widget = bar.Bounds,
            Displays = DisplayInfo.ReadAll(),
            Windows = samples.Where(f => !f.IsDisposed).Select(f => new { f.Text, Bounds = Native.VisibleBounds(f.Handle), Minimized = Native.IsIconic(f.Handle) })
        };
        File.WriteAllText(Path.Combine(ArtifactPath, "ui-test-state.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task VerifyFocusBorder(FloatingBar bar, DisplayInfo display)
    {
        nint originalForeground = Native.GetForegroundWindow();
        simulatedForeground = samples[0].Handle;
        await Until(() => bar.FocusBorder.TrackedWindow == samples[0].Handle && bar.FocusBorder.Strips.All(s => s.Visible),
            "Border follows the focused test terminal on monitor " + display.Number);
        Assert(Native.GetForegroundWindow() == originalForeground, "Displaying the border does not take focus from the user's current app");
        foreach (var strip in bar.FocusBorder.Strips)
        {
            long styles = Native.GetWindowLongPtr(strip.Handle, -20).ToInt64();
            Assert((styles & BorderStrip.OverlayStyles) == BorderStrip.OverlayStyles,
                "Native overlay is layered, transparent to mouse input, nonactivating, topmost, and absent from the taskbar");
        }
        Assert(bar.FocusBorder.LastError == null, "Layered-window rendering succeeds");

        simulatedForeground = samples[1].Handle;
        await Until(() => bar.FocusBorder.TrackedWindow == samples[1].Handle, "Focus border switches to another terminal");
        var area = display.WorkingArea;
        samples[1].Bounds = new Rectangle(area.Left + 75, area.Top + 100, 640, 410);
        await Until(() => bar.FocusBorder.TargetBounds == Native.VisibleBounds(samples[1].Handle), "Border tracks moving and resizing");

        // Moving the same active window across displays must update all overlay coordinates.
        var otherDisplay = DisplayInfo.ReadAll().FirstOrDefault(d => d.DeviceName != display.DeviceName);
        if (otherDisplay != null)
        {
            samples[1].Location = new Point(otherDisplay.WorkingArea.Left + 85, otherDisplay.WorkingArea.Top + 95);
            await Until(() => bar.FocusBorder.TargetBounds == Native.VisibleBounds(samples[1].Handle), "Border follows its window across monitors");
        }

        samples[1].WindowState = FormWindowState.Maximized;
        await Until(() => Native.IsZoomed(samples[1].Handle) && bar.FocusBorder.TargetBounds == Native.VisibleBounds(samples[1].Handle), "Border adapts to maximized windows");
        samples[1].WindowState = FormWindowState.Minimized;
        await Until(() => bar.FocusBorder.TrackedWindow != samples[1].Handle, "Minimized terminal loses its border");

        simulatedForeground = bar.Handle;
        await Until(() => bar.FocusBorder.TrackedWindow == 0 && bar.FocusBorder.Strips.All(s => !s.Visible),
            "Border disappears when a nonterminal app has focus");
        await ClickMonitor(bar, display);
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        for (int i = 0; i < 75 && !condition(); i++) await Task.Delay(40);
        Assert(condition(), message);
    }

    private static void BorderPixels()
    {
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        {
            using var renderer = new BorderRenderer(new Size(720, 430), scale, false);
            using var first = renderer.RenderFrame(.12);
            using var second = renderer.RenderFrame(.56);
            Assert(first.GetPixel(first.Width / 2, first.Height / 2).A == 0, "Terminal content stays fully transparent through the overlay");
            int changed = 0, visible = 0;
            for (int y = 0; y < first.Height; y += 2)
                for (int x = 0; x < first.Width; x += 2)
                {
                    Color a = first.GetPixel(x, y), b = second.GetPixel(x, y);
                    if (a.A > 0) visible++;
                    if (a.ToArgb() != b.ToArgb()) changed++;
                    Assert(a.R == a.G && a.G == a.B, "Border remains monochrome");
                }
            Assert(visible > 100 && changed > 100, "Highlight visibly moves around the outline");
            Assert(renderer.Areas.All(r => !r.Contains(first.Width / 2, first.Height / 2)), "Overlay surfaces do not cover the terminal's center");
            if (scale == 1)
            {
                first.Save(Path.Combine(ArtifactPath, "focus-border-alpha.png"), ImageFormat.Png);
                using var preview = new Bitmap(first.Width, first.Height);
                using var graphics = Graphics.FromImage(preview);
                graphics.Clear(Color.FromArgb(35, 40, 48));
                using var background = new SolidBrush(Color.FromArgb(16, 19, 24));
                graphics.FillRectangle(background, renderer.Padding, renderer.Padding, 720, 430);
                graphics.DrawImageUnscaled(first, 0, 0);
                preview.Save(Path.Combine(ArtifactPath, "focus-border-preview.png"), ImageFormat.Png);
            }
        }

        using var large = new BorderRenderer(new Size(3440, 1440), 1, false);
        Assert(large.Areas.Sum(r => (long)r.Width * r.Height) < (long)large.Size.Width * large.Size.Height / 20,
            "Animation touches under five percent of a full-size terminal's area");
        var surfaces = large.Areas.Select(r => new Bitmap(r.Width, r.Height, PixelFormat.Format32bppPArgb)).ToArray();
        var drawings = surfaces.Select(Graphics.FromImage).ToArray();
        try
        {
            var watch = Stopwatch.StartNew();
            for (int frame = 0; frame < 60; frame++)
                for (int strip = 0; strip < 4; strip++) large.Draw(drawings[strip], strip, frame / 60d);
            Console.WriteLine($"Border rendering: {watch.Elapsed.TotalMilliseconds / 60:F2} ms/frame at 3440 × 1440.");
        }
        finally
        {
            foreach (var drawing in drawings) drawing.Dispose();
            foreach (var surface in surfaces) surface.Dispose();
        }
    }

    private static void Geometry()
    {
        Rectangle[] areas = [new(0, 0, 1920, 1040), new(-3840, -180, 3840, 2080), new(2560, -2160, 2160, 3800), new(0, 0, 800, 560)];
        foreach (var area in areas)
        {
            for (int count = 0; count <= 64; count++)
            {
                var cells = TileLayout.Plan(area, count);
                Assert(cells.Count == count, "One cell per window");
                Assert(cells.Select(cell => cell.Width).Distinct().Count() <= 1, "All windows have exactly the same width, including incomplete rows");
                foreach (var cell in cells) Assert(cell.Width > 0 && cell.Height > 0 && area.Contains(cell), "Positive cell inside work area");
                for (int i = 0; i < count; i++)
                    for (int j = i + 1; j < count; j++)
                        Assert(!cells[i].IntersectsWith(cells[j]), "No overlapping cells");
                Assert(cells.SequenceEqual(TileLayout.Plan(area, count)), "Deterministic layout");
            }
        }
        Assert(TileLayout.Plan(Rectangle.Empty, 3).Count == 0, "Invalid work area is ignored");
        Assert(TileLayout.Plan(areas[0], -1).Count == 0, "Negative count is ignored");
    }

    private static void Classification()
    {
        Assert(TerminalWindows.IsTerminal("CASCADIA_HOSTING_WINDOW_CLASS", "WindowsTerminal"), "Windows Terminal");
        Assert(TerminalWindows.IsTerminal("ConsoleWindowClass", "conhost"), "Classic console");
        Assert(TerminalWindows.IsTerminal("mintty", "mintty"), "Git Bash");
        Assert(TerminalWindows.IsTerminal("PuTTY", "putty"), "PuTTY");
        Assert(TerminalWindows.IsTerminal("unknown", "wezterm-gui"), "WezTerm process fallback");
        Assert(!TerminalWindows.IsTerminal("Chrome_WidgetWin_1", "Code"), "VS Code is not a terminal window");
        Assert(!TerminalWindows.IsTerminal("Chrome_WidgetWin_1", "chrome"), "Chrome is excluded");
        Assert(!TerminalWindows.IsTerminal("OtherClass", "powershell"), "Background PowerShell host is excluded");
    }

    private static void Assert(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException("FAILED: " + message);
    }
}
