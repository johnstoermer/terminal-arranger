using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace TerminalRearranger;

internal sealed class TerminalForkButtons : IDisposable
{
    private readonly Func<List<TerminalWindow>> findWindows;
    private readonly Func<nint, CancellationToken, Task> fork;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<nint, ForkButtonWindow> buttons = [];
    private bool busy, disposed;

    internal IReadOnlyDictionary<nint, ForkButtonWindow> Buttons => buttons;

    internal TerminalForkButtons(Func<List<TerminalWindow>> findWindows, Func<nint, CancellationToken, Task> fork)
    {
        this.findWindows = findWindows;
        this.fork = fork;
        timer.Tick += (_, _) => Refresh();
    }

    internal void Start() { Refresh(); timer.Start(); }

    internal void Refresh()
    {
        if (disposed) return;
        var windows = findWindows().Where(w => Native.IsWindow(w.Handle)).ToList();
        var handles = windows.Select(w => w.Handle).ToHashSet();
        foreach (nint handle in buttons.Keys.Where(h => !handles.Contains(h) || buttons[h].IsDisposed).ToArray())
        {
            buttons[handle].Dispose();
            buttons.Remove(handle);
        }
        foreach (var window in windows)
        {
            if (!buttons.TryGetValue(window.Handle, out var button))
            {
                button = new ForkButtonWindow(window.Handle);
                button.ForkRequested += async (_, _) => await ForkAsync(window.Handle);
                buttons.Add(window.Handle, button);
            }
            button.Busy = busy;
            button.FollowOwner();
        }
    }

    internal async Task ForkAsync(nint source)
    {
        if (busy || disposed || !buttons.TryGetValue(source, out var button)) return;
        busy = true;
        foreach (var item in buttons.Values) item.Busy = true;
        try { await fork(source, lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException
            or UnauthorizedAccessException or TimeoutException or System.Text.Json.JsonException)
        {
            if (!disposed && !button.IsDisposed) button.ShowError(ex.Message);
        }
        finally
        {
            busy = false;
            if (!disposed) foreach (var item in buttons.Values) item.Busy = false;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        timer.Dispose();
        foreach (var button in buttons.Values) button.Dispose();
        buttons.Clear();
        lifetime.Dispose();
    }
}

internal sealed class ForkButtonWindow : Form
{
    internal const int Styles = 0x80 | 0x08000000; // Tool window; never activate on click.
    private readonly nint source;
    private readonly ToolTip tips = new() { InitialDelay = 700, ShowAlways = true };
    private float scale = 1;
    private bool hovered, busy;

    internal event EventHandler? ForkRequested;
    internal string? LastError { get; private set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Busy
    {
        get => busy;
        set { if (busy == value) return; busy = value; Cursor = value ? Cursors.WaitCursor : Cursors.Hand; Invalidate(); }
    }

    internal ForkButtonWindow(nint source)
    {
        this.source = source;
        Text = $"Terminal Rearranger Fork {source}";
        AccessibleName = "Fork Codex session";
        AccessibleRole = AccessibleRole.PushButton;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(23, 28, 35);
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        tips.SetToolTip(this, "Fork Codex session");
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= Styles; return cp; }
    }
    private sealed record OwnerWindow(nint Handle) : IWin32Window;

    internal static Rectangle Placement(Rectangle frame, float scale)
    {
        int side = (int)Math.Round(28 * scale);
        int rightInset = (int)Math.Round(20 * scale), bottomInset = (int)Math.Round(8 * scale);
        return new Rectangle(frame.Right - rightInset - side, frame.Bottom - bottomInset - side, side, side);
    }

    internal void FollowOwner()
    {
        if (IsDisposed) return;
        if (!Native.IsWindowVisible(source) || Native.IsIconic(source) ||
            (Native.GetDwmInt(source, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0)) { Hide(); return; }
        Rectangle frame = Native.VisibleBounds(source);
        if (frame.Width < 80 || frame.Height < 80) { Hide(); return; }
        scale = Math.Max(96, Native.GetDpiForWindow(source)) / 96f;
        Rectangle target = Placement(frame, scale);
        nint under = Native.GetAncestor(Native.WindowFromPoint(new Native.Point2(
            target.X + target.Width / 2, target.Y + target.Height / 2)), 2);
        if (under != source && (!IsHandleCreated || under != Handle)) { Hide(); return; }
        if (Bounds != target)
        {
            Bounds = target;
            using var outline = Drawing.RoundRect(new RectangleF(0, 0, Width, Height), 7 * scale);
            Region?.Dispose();
            Region = new Region(outline);
        }
        if (!Visible) Show(new OwnerWindow(source));
        // Moving a terminal in another process can leave its owned icon behind
        // it until activation. Restore the icon just above its owner on every
        // refresh, without raising or focusing the terminal itself.
        nint preceding = Native.GetWindow(source, 3); // GW_HWNDPREV
        if (preceding != Handle)
            Native.SetWindowPos(Handle, preceding, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
    }

    internal void ShowError(string message)
    {
        LastError = message;
        tips.SetToolTip(this, message);
        tips.Show(message, this, -220, -30, 5000);
        Invalidate();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = new nint(3); return; } // MA_NOACTIVATE
        base.WndProc(ref m);
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) InvokeFork();
    }
    internal void InvokeFork()
    {
        if (busy) return;
        LastError = null;
        tips.SetToolTip(this, "Fork Codex session");
        ForkRequested?.Invoke(this, EventArgs.Empty);
    }
    protected override AccessibleObject CreateAccessibilityInstance() => new ForkAccessibility(this);
    private sealed class ForkAccessibility(ForkButtonWindow owner) : ControlAccessibleObject(owner)
    {
        public override string? DefaultAction => "Fork";
        public override void DoDefaultAction() => owner.InvokeFork();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawGlyph(e.Graphics, scale, hovered, busy, LastError != null);
    }
    internal static void DrawGlyph(Graphics g, float scale, bool hovered, bool busy, bool error)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(scale, scale);
        if (hovered)
        {
            using var shape = Drawing.RoundRect(new RectangleF(.5f, .5f, 27, 27), 7);
            using var fill = new SolidBrush(Color.FromArgb(39, 46, 57));
            g.FillPath(fill, shape);
        }
        Color color = busy ? Color.FromArgb(100, 111, 127) : error ? Color.FromArgb(240, 188, 99) : Color.FromArgb(210, 219, 230);
        using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawEllipse(pen, 6, 5, 4, 4);
        g.DrawEllipse(pen, 18, 5, 4, 4);
        g.DrawEllipse(pen, 12, 19, 4, 4);
        g.DrawLine(pen, 14, 15, 14, 19);
        using var branch = new GraphicsPath();
        branch.AddLine(8, 9, 8, 11);
        branch.AddBezier(8, 11, 8, 16, 20, 16, 20, 11);
        branch.AddLine(20, 11, 20, 9);
        g.DrawPath(pen, branch);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) tips.Dispose();
        base.Dispose(disposing);
    }
}
