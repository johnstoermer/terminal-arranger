using System.ComponentModel;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace TerminalRearranger;

internal sealed class FloatingBar : Form
{
    private static readonly Color Surface = Color.FromArgb(23, 28, 35);
    private const int SideWidth = 28;
    private readonly UserSettings settings;
    private readonly bool persist;
    private readonly WindowArranger arranger;
    private readonly FocusBorderController focusBorder;
    private readonly ToolTip tips = new() { InitialDelay = 500, ReshowDelay = 150, AutoPopDelay = 3500, ShowAlways = true };
    private readonly System.Windows.Forms.Timer pulse = new() { Interval = 75 };
    private readonly List<MonitorButton> monitorButtons = [];
    private readonly CloseButton closeButton = new();
    private List<DisplayInfo> displays = [];
    private bool busy;
    private bool dragging;
    private Point dragOffset;
    private int tick;
    private string? pendingDisplay;

    internal ArrangementResult? LastResult { get; private set; }
    internal bool Busy => busy;
    internal FocusBorderController FocusBorder => focusBorder;

    internal FloatingBar(Func<List<TerminalWindow>>? findWindows = null, bool persist = true, Func<nint>? foregroundWindow = null)
    {
        this.persist = persist;
        settings = persist ? UserSettings.Load() : new UserSettings();
        arranger = new WindowArranger(findWindows);
        Func<List<TerminalWindow>> findTerminals = findWindows ?? TerminalWindows.Find;
        focusBorder = new FocusBorderController(hwnd => findTerminals().Any(w => w.Handle == hwnd), foregroundWindow);
        Text = "Terminal Rearranger";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Surface;
        DoubleBuffered = true;
        KeyPreview = true;
        AccessibleName = "Terminal Rearranger";
        closeButton.AccessibleName = "Close Terminal Rearranger";
        closeButton.Click += (_, _) => Close();
        Controls.Add(closeButton);
        tips.SetToolTip(closeButton, "Close");
        tips.SetToolTip(this, "Drag to move");
        RebuildMonitors();

        var primary = displays.FirstOrDefault(s => s.Primary) ?? displays.First();
        Location = settings.X is int x && settings.Y is int y
            ? new Point(x, y)
            : new Point(primary.WorkingArea.Left + (primary.WorkingArea.Width - Width) / 2, primary.WorkingArea.Top + 12);
        ClampToScreen();
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        pulse.Tick += (_, _) =>
        {
            tick++;
            foreach (var button in monitorButtons)
                if (button.Busy) { button.SpinAngle = (tick * 24) % 360; button.Invalidate(); }
            if (tick % 40 == 0 && IsHandleCreated) Native.KeepOnTop(Handle);
        };
        pulse.Start();
    }

    protected override bool ShowWithoutActivation => true;
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        focusBorder.Start();
    }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (!ShowInTaskbar) cp.ExStyle |= 0x80; // Small tool window, no Alt+Tab entry.
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int rounded = 2;
        Native.DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
        Native.KeepOnTop(Handle);
    }

    private int Px(float value) => (int)Math.Round(value * DeviceDpi / 96f);

    private void LayoutButtons()
    {
        int sideWidth = Px(SideWidth);
        int buttonWidth = Px(44);
        int gap = Px(4);
        ClientSize = new Size(sideWidth * 2 + displays.Count * buttonWidth + Math.Max(0, displays.Count - 1) * gap, Px(56));
        for (int i = 0; i < monitorButtons.Count; i++)
            monitorButtons[i].Bounds = new Rectangle(sideWidth + i * (buttonWidth + gap), Px(7), buttonWidth, Px(42));
        closeButton.Bounds = new Rectangle(ClientSize.Width - sideWidth, 0, sideWidth, ClientSize.Height);
        using var outline = Drawing.RoundRect(new RectangleF(0, 0, Width, Height), Px(13));
        Region?.Dispose();
        Region = new Region(outline);
        Invalidate();
    }

    private void RebuildMonitors()
    {
        displays = DisplayInfo.ReadAll();
        foreach (var button in monitorButtons) { Controls.Remove(button); button.Dispose(); }
        monitorButtons.Clear();
        foreach (var display in displays)
        {
            var button = new MonitorButton(display.Number)
            {
                AccessibleName = $"Arrange terminals on monitor {display.Number}",
                AccessibleDescription = "Click again to refresh the arrangement",
                Selected = settings.SelectedDisplay == display.DeviceName,
                TabIndex = monitorButtons.Count
            };
            button.Click += async (_, _) => await Arrange(display.DeviceName);
            tips.SetToolTip(button, MonitorTip(display));
            monitorButtons.Add(button);
            Controls.Add(button);
        }
        closeButton.TabIndex = monitorButtons.Count;
        LayoutButtons();
    }

    private static string MonitorTip(DisplayInfo display) =>
        $"Monitor {display.Number}  ·  {display.Bounds.Width} × {display.Bounds.Height}\nArrange terminals · click again to refresh";

    internal async Task Arrange(string deviceName)
    {
        if (busy) { pendingDisplay = deviceName; return; }
        var display = DisplayInfo.ReadAll().FirstOrDefault(d => d.DeviceName == deviceName);
        if (display == null) { RebuildMonitors(); return; }
        busy = true;
        settings.SelectedDisplay = display.DeviceName;
        LastResult = null;
        foreach (var b in monitorButtons)
        {
            b.Selected = b.Number == display.Number;
            b.Busy = b.Selected;
            b.Warning = false;
            b.Invalidate();
        }
        try
        {
            LastResult = await arranger.ArrangeAsync(display);
            if (IsDisposed) return;
            var button = monitorButtons.FirstOrDefault(b => b.Number == display.Number);
            if (button != null)
            {
                button.Warning = LastResult.Found == 0 || LastResult.Arranged != LastResult.Found;
                tips.SetToolTip(button, LastResult.Message + "\n" + MonitorTip(display));
                if (button.Warning) tips.Show(LastResult.Message, button, 0, button.Height + Px(6), 2800);
            }
            SavePosition();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (!IsDisposed) tips.Show("Could not arrange these windows. Click to retry.", this, 0, Height + 4, 3000);
        }
        finally
        {
            busy = false;
            if (!IsDisposed)
            {
                foreach (var b in monitorButtons) { b.Busy = false; b.Invalidate(); }
                Native.KeepOnTop(Handle);
            }
        }
        if (!IsDisposed && pendingDisplay is string next)
        {
            pendingDisplay = null;
            await Arrange(next);
        }
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed && IsHandleCreated)
            BeginInvoke(() => { if (!IsDisposed) { RebuildMonitors(); ClampToScreen(); } });
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        LayoutButtons();
        ClampToScreen();
    }

    private void ClampToScreen()
    {
        var screen = Screen.FromRectangle(Bounds).WorkingArea;
        Location = new Point(Math.Clamp(Left, screen.Left, Math.Max(screen.Left, screen.Right - Width)),
            Math.Clamp(Top, screen.Top, Math.Max(screen.Top, screen.Bottom - Height)));
    }

    private void SavePosition()
    {
        if (!persist) return;
        settings.X = Left;
        settings.Y = Top;
        settings.Save();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        dragging = true;
        dragOffset = e.Location;
        Capture = true;
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging) Location = new Point(MousePosition.X - dragOffset.X, MousePosition.Y - dragOffset.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!dragging) return;
        dragging = false;
        Capture = false;
        Cursor = Cursors.Default;
        ClampToScreen();
        SavePosition();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = Drawing.RoundRect(new RectangleF(.5f, .5f, Width - 1, Height - 1), Px(13));
        using var border = new Pen(Color.FromArgb(58, 66, 78));
        e.Graphics.DrawPath(border, outline);
        using var grip = new SolidBrush(Color.FromArgb(100, 111, 127));
        float scale = DeviceDpi / 96f;
        float gripLeft = Px(SideWidth) / 2f - 4 * scale;
        float gripTop = ClientSize.Height / 2f - 7 * scale;
        for (int col = 0; col < 2; col++)
            for (int row = 0; row < 3; row++)
                e.Graphics.FillEllipse(grip, gripLeft + col * 6 * scale, gripTop + row * 6 * scale, 2 * scale, 2 * scale);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            SavePosition();
            pulse.Dispose();
            tips.Dispose();
            focusBorder.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class Drawing
{
    internal static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal abstract class GlyphButton : Button
{
    protected bool Hovered;
    protected GlyphButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnMouseEnter(EventArgs e) { Hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

internal sealed class MonitorButton(string number) : GlyphButton
{
    internal string Number { get; } = number;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Selected { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Busy { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Warning { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int SpinAngle { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        g.ScaleTransform(s, s);
        Color accent = Warning ? Color.FromArgb(240, 188, 99) : Color.FromArgb(111, 224, 195);
        Color foreground = Selected ? accent : Color.FromArgb(179, 190, 204);
        using var shape = Drawing.RoundRect(new RectangleF(1, 1, 42, 40), 8);
        if (Selected || Hovered)
        {
            using var fill = new SolidBrush(Selected ? Color.FromArgb(31, 59, 57) : Color.FromArgb(39, 46, 57));
            g.FillPath(fill, shape);
        }
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Color.FromArgb(100, 125, 145));
            g.DrawPath(focus, shape);
        }
        using var pen = new Pen(foreground, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var monitor = Drawing.RoundRect(new RectangleF(10, 12, 24, 18), 2);
        g.DrawPath(pen, monitor);
        using var font = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(foreground);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Number, font, brush, new RectangleF(10, 11, 24, 19), format);
        if (Busy) g.DrawArc(pen, 32, 29, 7, 7, SpinAngle, 260);
    }
}

internal sealed class CloseButton : GlyphButton
{
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f;
        g.ScaleTransform(scale, scale);
        float centerX = ClientSize.Width / (2 * scale);
        float centerY = ClientSize.Height / (2 * scale);
        if (Hovered || (Focused && ShowFocusCues))
        {
            using var shape = Drawing.RoundRect(new RectangleF(centerX - 12, centerY - 12, 24, 24), 6);
            using var fill = new SolidBrush(Color.FromArgb(65, 42, 49));
            g.FillPath(fill, shape);
        }
        using var pen = new Pen(Hovered ? Color.FromArgb(242, 159, 157) : Color.FromArgb(116, 129, 145), 1.5f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, centerX - 4, centerY - 4, centerX + 4, centerY + 4);
        g.DrawLine(pen, centerX + 4, centerY - 4, centerX - 4, centerY + 4);
    }
}
