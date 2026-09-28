using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TerminalRearranger;

internal sealed class FocusBorderController : IDisposable
{
    private readonly Func<nint, bool> isTerminal;
    private readonly Func<nint> foregroundWindow;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private readonly Stopwatch animation = Stopwatch.StartNew();
    private readonly BorderStrip[] strips = Enumerable.Range(0, 4).Select(i => new BorderStrip(i)).ToArray();
    private BorderRenderer? renderer;
    private nint lastForeground;
    private bool recognized;
    private uint lastDpi;
    private bool lastMaximized;
    private bool disposed;

    internal nint TrackedWindow { get; private set; }
    internal Rectangle TargetBounds { get; private set; }
    internal IReadOnlyList<BorderStrip> Strips => strips;
    internal string? LastError { get; private set; }

    internal FocusBorderController(Func<nint, bool> isTerminal, Func<nint>? foregroundWindow = null)
    {
        this.isTerminal = isTerminal;
        this.foregroundWindow = foregroundWindow ?? Native.GetForegroundWindow;
        timer.Tick += (_, _) => Refresh();
    }

    internal void Start() { timer.Start(); Refresh(); }

    internal void Refresh()
    {
        if (disposed) return;
        try
        {
            nint foreground = foregroundWindow();
            if (foreground != lastForeground)
            {
                lastForeground = foreground;
                recognized = foreground != 0 && isTerminal(foreground);
            }
            if (!recognized || !Native.IsWindow(foreground) || !Native.IsWindowVisible(foreground) ||
                Native.IsIconic(foreground) ||
                (Native.GetDwmInt(foreground, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
            {
                Hide();
                timer.Interval = 100;
                return;
            }

            Rectangle bounds = Native.VisibleBounds(foreground);
            if (bounds.Width < 64 || bounds.Height < 40) { Hide(); return; }
            uint dpi = Math.Max(96, Native.GetDpiForWindow(foreground));
            bool maximized = Native.IsZoomed(foreground);
            if (renderer == null || bounds.Size != TargetBounds.Size || dpi != lastDpi || maximized != lastMaximized)
            {
                renderer?.Dispose();
                renderer = new BorderRenderer(bounds.Size, dpi / 96f, maximized);
                lastDpi = dpi;
                lastMaximized = maximized;
            }
            TargetBounds = bounds;
            TrackedWindow = foreground;
            timer.Interval = 33;
            double phase = animation.Elapsed.TotalSeconds / 5.5 % 1;
            Point origin = new(bounds.Left - renderer.Padding, bounds.Top - renderer.Padding);
            for (int i = 0; i < strips.Length; i++) strips[i].Render(renderer, i, origin, phase);
            LastError = null;
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException)
        {
            // A display switch can briefly invalidate a surface. Retry without interrupting the widget.
            LastError = ex.Message;
            Hide();
            timer.Interval = 250;
        }
    }

    private void Hide()
    {
        foreach (var strip in strips) strip.Hide();
        TrackedWindow = 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Dispose();
        foreach (var strip in strips) strip.Dispose();
        renderer?.Dispose();
    }
}

internal sealed class BorderStrip(int index) : NativeWindow, IDisposable
{
    internal const int OverlayStyles = 0x00080000 | 0x00000020 | 0x00000080 | 0x08000000 | 0x00000008;
    private nint dc, dib, previousBitmap;
    private Bitmap? bitmap;
    private Graphics? graphics;
    private Size surfaceSize;
    internal bool Visible { get; private set; }

    internal void Render(BorderRenderer renderer, int stripIndex, Point origin, double phase)
    {
        Rectangle area = renderer.Areas[stripIndex];
        if (Handle == 0)
        {
            CreateHandle(new CreateParams
            {
                Caption = $"Terminal Rearranger Focus Border {index + 1}",
                Style = unchecked((int)0x80000000), // WS_POPUP
                ExStyle = OverlayStyles
            });
        }
        EnsureSurface(area.Size);
        renderer.Draw(graphics!, stripIndex, phase);
        graphics!.Flush(FlushIntention.Sync);
        var destination = new Native.Point2(origin.X + area.X, origin.Y + area.Y);
        var size = new Native.Size2(area.Width, area.Height);
        var source = new Native.Point2(0, 0);
        var blend = new Native.BlendFunction { ConstantAlpha = 255, AlphaFormat = 1 };
        if (!Native.UpdateLayeredWindow(Handle, 0, ref destination, ref size, dc, ref source, 0, ref blend, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!Visible)
        {
            Native.SetWindowPos(Handle, new nint(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
            Visible = true;
        }
    }

    private void EnsureSurface(Size size)
    {
        if (bitmap != null && surfaceSize == size) return;
        ReleaseSurface();
        dc = Native.CreateCompatibleDC(0);
        if (dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new Native.BitmapInfo
        {
            HeaderSize = 40, Width = size.Width, Height = -size.Height,
            Planes = 1, BitCount = 32
        };
        dib = Native.CreateDIBSection(dc, ref info, 0, out nint bits, 0, 0);
        if (dib == 0) { int error = Marshal.GetLastWin32Error(); ReleaseSurface(); throw new Win32Exception(error); }
        previousBitmap = Native.SelectObject(dc, dib);
        bitmap = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, bits);
        graphics = Graphics.FromImage(bitmap);
        surfaceSize = size;
    }

    internal void Hide()
    {
        if (!Visible) return;
        Native.ShowWindow(Handle, 0);
        Visible = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0021) { m.Result = new nint(3); return; } // MA_NOACTIVATE
        if (m.Msg == 0x0084) { m.Result = new nint(-1); return; } // HTTRANSPARENT
        base.WndProc(ref m);
    }

    private void ReleaseSurface()
    {
        graphics?.Dispose(); graphics = null;
        bitmap?.Dispose(); bitmap = null;
        if (previousBitmap != 0 && dc != 0) Native.SelectObject(dc, previousBitmap);
        if (dib != 0) Native.DeleteObject(dib);
        if (dc != 0) Native.DeleteDC(dc);
        dc = dib = previousBitmap = 0;
    }

    public void Dispose()
    {
        Hide();
        ReleaseSurface();
        if (Handle != 0) DestroyHandle();
    }
}

internal sealed class BorderRenderer : IDisposable
{
    private readonly GraphicsPath path;
    private readonly Pen basePen;
    private readonly Pen shadowPen;
    private readonly Pen[] glowPens;
    private readonly Pen[] shinePens;
    private readonly Segment[][] segments;
    private readonly PointF[][] lineBuffers;
    private readonly record struct Segment(PointF From, PointF To, double Position);
    internal int Padding { get; }
    internal Size Size { get; }
    internal Rectangle[] Areas { get; }

    internal BorderRenderer(Size targetSize, float scale, bool squareCorners)
    {
        Padding = (int)Math.Ceiling(4 * scale);
        Size = new Size(targetSize.Width + Padding * 2, targetSize.Height + Padding * 2);
        int band = Math.Min((int)Math.Ceiling(18 * scale), Math.Min(Size.Width, Size.Height) / 2 - 1);
        Areas = [new(0, 0, Size.Width, band), new(0, Size.Height - band, Size.Width, band),
            new(0, band, band, Size.Height - band * 2), new(Size.Width - band, band, band, Size.Height - band * 2)];
        float inset = scale;
        var outline = new RectangleF(Padding + inset, Padding + inset,
            targetSize.Width - inset * 2, targetSize.Height - inset * 2);
        path = new GraphicsPath();
        if (squareCorners) path.AddRectangle(outline);
        else { path.Dispose(); path = Drawing.RoundRect(outline, 8 * scale); }

        basePen = MakePen(Color.FromArgb(65, Color.White), 1.4f * scale);
        shadowPen = MakePen(Color.FromArgb(65, Color.Black), 3.5f * scale);
        glowPens = Enumerable.Range(0, 48).Select(i => MakePen(Color.FromArgb(i * 48 / 47, Color.White), 7 * scale)).ToArray();
        shinePens = Enumerable.Range(0, 48).Select(i => MakePen(Color.FromArgb(i * 245 / 47, Color.White), 2 * scale)).ToArray();

        using var flatPath = (GraphicsPath)path.Clone();
        flatPath.Flatten(null, .15f);
        var vertices = flatPath.PathPoints.ToList();
        vertices.Add(vertices[0]);
        var distances = new double[vertices.Count];
        for (int i = 1; i < vertices.Count; i++)
            distances[i] = distances[i - 1] + Math.Sqrt(Math.Pow(vertices[i].X - vertices[i - 1].X, 2) + Math.Pow(vertices[i].Y - vertices[i - 1].Y, 2));
        double perimeter = distances[^1];
        int steps = (int)Math.Ceiling(perimeter / (4 * scale));
        var points = new PointF[steps + 1];
        int vertex = 1;
        for (int i = 0; i <= steps; i++)
        {
            double distance = perimeter * i / steps;
            while (vertex < distances.Length - 1 && distances[vertex] < distance) vertex++;
            double length = distances[vertex] - distances[vertex - 1];
            float t = length > 0 ? (float)((distance - distances[vertex - 1]) / length) : 0;
            points[i] = new PointF(vertices[vertex - 1].X + (vertices[vertex].X - vertices[vertex - 1].X) * t,
                vertices[vertex - 1].Y + (vertices[vertex].Y - vertices[vertex - 1].Y) * t);
        }
        var all = Enumerable.Range(0, steps).Select(i => new Segment(points[i], points[i + 1], (i + .5) / steps)).ToArray();
        segments = Areas.Select(area => all.Where(s =>
        {
            var bounds = RectangleF.FromLTRB(Math.Min(s.From.X, s.To.X), Math.Min(s.From.Y, s.To.Y),
                Math.Max(s.From.X, s.To.X), Math.Max(s.From.Y, s.To.Y));
            bounds.Inflate(4 * scale, 4 * scale);
            return bounds.IntersectsWith(area);
        }).ToArray()).ToArray();
        lineBuffers = segments.Select(part => new PointF[part.Length + 1]).ToArray();
    }

    private static Pen MakePen(Color color, float width) => new(color, width)
    {
        LineJoin = LineJoin.Round, StartCap = LineCap.Flat, EndCap = LineCap.Flat
    };

    private static int Brightness(double position, double phase)
    {
        double distance = (phase - position + 1) % 1;
        const double tail = .24;
        if (distance >= tail) return 0;
        double fade = 1 - distance / tail;
        return (int)Math.Round(47 * fade * fade * Math.Min(1, distance / .008));
    }

    internal void Draw(Graphics graphics, int stripIndex, double phase)
    {
        var state = graphics.Save();
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(-Areas[stripIndex].X, -Areas[stripIndex].Y);
        graphics.DrawPath(shadowPen, path);
        DrawHighlight(graphics, stripIndex, phase, glowPens);
        graphics.DrawPath(basePen, path);
        DrawHighlight(graphics, stripIndex, phase, shinePens);
        graphics.Restore(state);
    }

    private void DrawHighlight(Graphics graphics, int stripIndex, double phase, Pen[] pens)
    {
        PointF[] points = lineBuffers[stripIndex];
        int used = 0, previousLevel = 0;
        foreach (var segment in segments[stripIndex])
        {
            int level = Brightness(segment.Position, phase);
            if (used > 0 && (level != previousLevel || segment.From != points[used - 1]))
            {
                graphics.DrawLines(pens[previousLevel], points.AsSpan(0, used));
                used = 0;
            }
            if (level == 0) continue;
            if (used == 0) points[used++] = segment.From;
            points[used++] = segment.To;
            previousLevel = level;
        }
        if (used > 0) graphics.DrawLines(pens[previousLevel], points.AsSpan(0, used));
    }

    internal Bitmap RenderFrame(double phase)
    {
        var frame = new Bitmap(Size.Width, Size.Height, PixelFormat.Format32bppPArgb);
        using var composite = Graphics.FromImage(frame);
        for (int i = 0; i < Areas.Length; i++)
        {
            using var strip = new Bitmap(Areas[i].Width, Areas[i].Height, PixelFormat.Format32bppPArgb);
            using (var drawing = Graphics.FromImage(strip)) Draw(drawing, i, phase);
            composite.DrawImageUnscaled(strip, Areas[i].Location);
        }
        return frame;
    }

    public void Dispose()
    {
        path.Dispose(); basePen.Dispose(); shadowPen.Dispose();
        foreach (var pen in glowPens) pen.Dispose();
        foreach (var pen in shinePens) pen.Dispose();
    }
}
