using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TerminalRearranger;

internal static partial class Checks
{
    private static CodexSession Session(long window = 123, string title = "Codex") => new(window, 42,
        Guid.Parse("01a00000-1234-7000-8000-000000000001"), @"C:\Users\example\a's work; 文",
        @"C:\Program Files\Codex\codex.exe", @"C:\Users\example\.codex", title);

    private static void ForkSelection()
    {
        var source = Session();
        var other = Session(456) with { Id = Guid.NewGuid() };
        Assert(CodexSessions.Select([other, source], new nint(123), "Codex") == source,
            "Fork selects the clicked window, independent of recency or enumeration order");
        var tab = Session(title: "Other task") with { Id = Guid.NewGuid() };
        Assert(CodexSessions.Select([source, tab], new nint(123), "Other task") == tab,
            "Selected tab title resolves multiple sessions in one window");
        var spinning = source with { ConsoleTitle = "\u280b Working" };
        Assert(CodexSessions.Select([spinning, tab], new nint(123), "\u2819 Working") == spinning,
            "Animated Codex spinner does not break tab matching");
        Reject(() => CodexSessions.Select([source, source with { Id = Guid.NewGuid() }], new nint(123), "Codex"),
            "Ambiguous tabs never fork an arbitrary session");
        Reject(() => CodexSessions.Select([source], new nint(456), "Codex"), "No fallback to another window");
        Reject(() => CodexSessions.Select([source], new nint(123), "PowerShell"), "Inactive Codex tab does not get forked");

        string Header(string id, object origin, string? agent = null) => JsonSerializer.Serialize(new
        {
            type = "session_meta", payload = new { id, cwd = source.Directory, source = origin,
                agent_path = agent, forked_from_id = other.Id.ToString() }
        });
        string path = @"C:\Users\example\.codex\sessions\2026\09\27\rollout-test.jsonl";
        var parsed = CodexSessions.ParseHeader(Header(source.Id.ToString(), "cli"), path, 123, 42, source.Executable, "Codex");
        Assert(parsed?.Id == source.Id && parsed.CodexHome == source.CodexHome && parsed.Directory == source.Directory,
            "Fork metadata uses the new session ID and original workspace/home");
        Assert(CodexSessions.ParseHeader(Header("invalid", "cli"), path, 123, 42, source.Executable, "Codex") == null,
            "Malformed session IDs cannot reach the command line");
        Assert(CodexSessions.ParseHeader(Header(source.Id.ToString(), new { subagent = "spawn" }), path, 123, 42, source.Executable, "Codex") == null,
            "Subagent rollouts are ignored");
        Assert(CodexSessions.ParseHeader(Header(source.Id.ToString(), "cli", "/root/child"), path, 123, 42, source.Executable, "Codex") == null,
            "A child agent cannot replace its parent as the fork source");

        var command = CodexFork.LaunchCommand(source, @"C:\a'b\helper.exe", @"C:\temp\window.json");
        string script = Encoding.Unicode.GetString(Convert.FromBase64String(command.ArgumentList[^1]));
        Assert(command.ArgumentList.Take(3).SequenceEqual(new[] { "-w", "new", "new-tab" }), "Each fork explicitly requests a new window");
        Assert(script.Contains($"fork '{source.Id}' --cd 'C:\\Users\\example\\a''s work; 文'"),
            "Fork receives the explicit session ID and safely quoted directory");
        Assert(!script.Contains("--last") && !script.Contains("Bypass"), "Fork uses its source session and normal execution settings");
        Assert(script.Contains("& 'C:\\a''b\\helper.exe' --report-console $PID"), "Window handshake uses the new shell PID");
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        {
            var frame = new Rectangle(-2000, -300, 900, 500);
            var icon = ForkButtonWindow.Placement(frame, scale);
            Assert(frame.Contains(icon) && icon.Width == icon.Height && frame.Right - icon.Right == (int)Math.Round(8 * scale),
                "Fork icon remains inside the bottom-right corner at each DPI");
        }
    }

    private static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, message);
    }

    private static async Task VerifyForkButtons()
    {
        using var source = new Form { Text = "Fork button test", StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(Screen.PrimaryScreen!.WorkingArea.Left + 150, 150, 600, 400) };
        source.Show();
        var release = new TaskCompletionSource();
        int launches = 0;
        nint clicked = 0;
        using var buttons = new TerminalForkButtons(() => [new(source.Handle, (uint)Environment.ProcessId, "test", "test")],
            async (window, token) => { launches++; clicked = window; await release.Task.WaitAsync(token); });
        buttons.Start();
        await Until(() => buttons.Buttons.TryGetValue(source.Handle, out var b) && b.Visible, "Fork icon appears on its terminal");
        var button = buttons.Buttons[source.Handle];
        Assert(Native.GetWindow(button.Handle, 4) == source.Handle, "Fork icon is owned by its source window");
        long styles = Native.GetWindowLongPtr(button.Handle, -20).ToInt64();
        Assert((styles & ForkButtonWindow.Styles) == ForkButtonWindow.Styles && (styles & 0x20) == 0,
            "Fork icon accepts clicks without activating or adding a taskbar entry");
        nint foreground = Native.GetForegroundWindow();
        button.AccessibilityObject.DoDefaultAction();
        button.AccessibilityObject.DoDefaultAction();
        Assert(launches == 1 && clicked == source.Handle && button.Busy, "Repeat clicks cannot launch duplicate forks");
        Assert(Native.GetForegroundWindow() == foreground, "Fork control does not steal focus");
        release.SetResult();
        await Until(() => !button.Busy, "Fork icon becomes available after launch");
        source.Location = new Point(source.Left + 90, source.Top + 50);
        await Until(() => button.Bounds == ForkButtonWindow.Placement(Native.VisibleBounds(source.Handle), source.DeviceDpi / 96f),
            "Fork icon follows movement");
        source.Size = new Size(700, 460);
        await Until(() => button.Bounds == ForkButtonWindow.Placement(Native.VisibleBounds(source.Handle), source.DeviceDpi / 96f),
            "Fork icon follows resizing");
        using (var preview = new Bitmap(112, 112))
        {
            using var g = Graphics.FromImage(preview);
            g.Clear(Color.FromArgb(23, 28, 35));
            ForkButtonWindow.DrawGlyph(g, 4, true, false, false);
            preview.Save(Path.Combine(ArtifactPath, "fork-icon.png"));
        }
        source.WindowState = FormWindowState.Minimized;
        await Until(() => !button.Visible, "Fork icon hides with a minimized terminal");
        source.WindowState = FormWindowState.Normal;
        await Until(() => button.Visible, "Fork icon returns after restore");
        using (var cover = new Form { StartPosition = FormStartPosition.Manual, Bounds = source.Bounds })
        {
            cover.Show();
            await Until(() => !button.Visible, "Fork icon stays behind an app covering its terminal");
        }
        await Until(() => button.Visible, "Fork icon returns when the terminal is uncovered");
        nint handle = button.Handle;
        buttons.Dispose();
        Assert(!Native.IsWindow(handle), "Fork overlay is destroyed on app shutdown");
    }

    private static int FakeCodex(string[] args)
    {
        string home = Environment.GetEnvironmentVariable("CODEX_HOME")!;
        File.WriteAllText(Path.Combine(home, "fork-invocation.json"), JsonSerializer.Serialize(new
        { Args = args, Directory = Environment.CurrentDirectory, Home = home, Pid = Environment.ProcessId }));
        Thread.Sleep(30000);
        return 0;
    }

    private static async Task VerifyTerminalLaunch()
    {
        string directory = Path.Combine(ArtifactPath, "fork's workspace; 文");
        Directory.CreateDirectory(directory);
        string invocation = Path.Combine(directory, "fork-invocation.json");
        File.Delete(invocation);
        string helper = Path.Combine(AppContext.BaseDirectory, "TerminalRearranger.exe");
        var session = Session() with { Directory = directory, CodexHome = directory, Executable = Environment.ProcessPath! };
        nint window = 0;
        try
        {
            window = await CodexFork.OpenAsync(session, CancellationToken.None, helper);
            await Until(() => File.Exists(invocation), "Windows Terminal starts the fork command");
            using var data = JsonDocument.Parse(File.ReadAllText(invocation));
            var actual = data.RootElement;
            Assert(actual.GetProperty("Args").EnumerateArray().Select(a => a.GetString()).SequenceEqual(
                new[] { "fork", session.Id.ToString(), "--cd", directory }), "Real shell preserves the exact fork arguments, including Unicode and punctuation");
            Assert(actual.GetProperty("Directory").GetString() == directory && actual.GetProperty("Home").GetString() == directory,
                "New terminal inherits the source workspace and Codex home");
            var arrangement = await new WindowArranger(() => [new(window, 0, "test", "test")]).ArrangeAsync(DisplayInfo.ReadAll()[0]);
            Assert(arrangement.Arranged == 1, "The new terminal is ready for arrangement after the handshake");
            using var child = Process.GetProcessById(actual.GetProperty("Pid").GetInt32());
            child.Kill();
            await child.WaitForExitAsync();
        }
        finally
        {
            // Only close the fresh test window identified by its own console handshake.
            if (window != 0 && Native.IsWindow(window)) PostMessage(window, 0x10, 0, 0);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
