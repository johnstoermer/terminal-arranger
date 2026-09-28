using System.Text.RegularExpressions;

namespace TerminalRearranger;

internal sealed record DisplayInfo(string DeviceName, string Number, Rectangle Bounds, Rectangle WorkingArea, bool Primary)
{
    internal static List<DisplayInfo> ReadAll() => Screen.AllScreens.Select(s => new DisplayInfo(
        s.DeviceName,
        Regex.Match(s.DeviceName, @"\d+$").Value is { Length: > 0 } number ? number : "?",
        s.Bounds, s.WorkingArea, s.Primary))
        .OrderBy(s => int.TryParse(s.Number, out int n) ? n : int.MaxValue).ToList();
}

internal static class TileLayout
{
    internal static List<Rectangle> Plan(Rectangle workArea, int count, int requestedGap = 8)
    {
        if (count <= 0 || workArea.Width <= 0 || workArea.Height <= 0) return [];
        int gap = Math.Max(0, Math.Min(requestedGap, Math.Min(workArea.Width, workArea.Height) / (count + 2)));
        var area = Rectangle.Inflate(workArea, -gap, -gap);
        int bestColumns = 1;
        int bestRows = count;
        double bestScore = double.MaxValue;
        for (int columns = 1; columns <= count; columns++)
        {
            int rows = (count + columns - 1) / columns;
            double width = (area.Width - gap * (columns - 1)) / (double)columns;
            double height = (area.Height - gap * (rows - 1)) / (double)rows;
            if (width < 1 || height < 1) continue;
            double aspectError = Math.Log(width / height / 1.65);
            double score = aspectError * aspectError;
            if (score < bestScore) { bestScore = score; bestColumns = columns; bestRows = rows; }
        }

        var result = new List<Rectangle>(count);
        int cellWidth = (area.Width - gap * (bestColumns - 1)) / bestColumns;
        int cellHeight = (area.Height - gap * (bestRows - 1)) / bestRows;
        // Keep one shared cell size, including in an incomplete final row.
        // Unused cells and integer-rounding space stay empty.
        for (int index = 0; index < count; index++)
        {
            int row = index / bestColumns;
            int column = index % bestColumns;
            result.Add(new Rectangle(area.X + column * (cellWidth + gap),
                area.Y + row * (cellHeight + gap), cellWidth, cellHeight));
        }
        return result;
    }
}

internal sealed record ArrangementResult(int Found, int Arranged)
{
    internal string Message => Found == 0 ? "No terminal windows open" :
        Arranged == Found ? $"{Arranged} terminal{(Arranged == 1 ? "" : "s")} arranged" :
        $"{Arranged} of {Found} arranged. Some windows could not be moved or resized.";
}

internal sealed class WindowArranger
{
    private readonly Func<List<TerminalWindow>> findWindows;
    private List<nint> previousOrder = [];

    internal WindowArranger(Func<List<TerminalWindow>>? findWindows = null) =>
        this.findWindows = findWindows ?? TerminalWindows.Find;

    private List<TerminalWindow> OrderedWindows()
    {
        var rank = previousOrder.Select((hwnd, i) => (hwnd, i)).ToDictionary(x => x.hwnd, x => x.i);
        return findWindows().Where(w => Native.IsWindow(w.Handle))
            .OrderBy(w => rank.GetValueOrDefault(w.Handle, int.MaxValue))
            // After a restart, recover the existing grid's reading order.
            .ThenBy(w => Native.VisibleBounds(w.Handle).Top)
            .ThenBy(w => Native.VisibleBounds(w.Handle).Left)
            .ThenBy(w => w.ProcessId).ThenBy(w => w.Handle.ToInt64()).ToList();
    }

    internal void AppendWindow(nint handle)
    {
        var windows = OrderedWindows();
        previousOrder = windows.Where(w => w.Handle != handle).Select(w => w.Handle).ToList();
        if (windows.Any(w => w.Handle == handle)) previousOrder.Add(handle);
    }

    internal async Task<ArrangementResult> ArrangeAsync(DisplayInfo display)
    {
        var windows = OrderedWindows();
        previousOrder = windows.Select(w => w.Handle).ToList();
        if (windows.Count == 0) return new ArrangementResult(0, 0);

        nint foreground = Native.GetForegroundWindow();
        bool restored = false;
        foreach (var window in windows)
        {
            if (Native.IsIconic(window.Handle) || Native.IsZoomed(window.Handle))
            {
                Native.ShowWindowAsync(window.Handle, 9);
                restored = true;
            }
        }
        if (restored) await Task.Delay(180);

        var cells = TileLayout.Plan(display.WorkingArea, windows.Count);
        // A second pass settles invisible borders after cross-monitor DPI changes.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < windows.Count; i++)
                if (Native.IsWindow(windows[i].Handle)) Native.MoveVisibleFrame(windows[i].Handle, cells[i]);
            await Task.Delay(160);
        }

        int moved = 0;
        for (int i = 0; i < windows.Count; i++)
        {
            var hwnd = windows[i].Handle;
            if (!Native.IsWindow(hwnd) || Native.IsIconic(hwnd) || Native.IsZoomed(hwnd)) continue;
            var actual = Native.VisibleBounds(hwnd);
            var target = cells[i];
            if (Math.Abs(actual.Left - target.Left) <= 16 && Math.Abs(actual.Top - target.Top) <= 16 &&
                Math.Abs(actual.Right - target.Right) <= 32 && Math.Abs(actual.Bottom - target.Bottom) <= 32)
                moved++;
        }
        if (restored && Native.IsWindow(foreground)) Native.SetForegroundWindow(foreground);
        return new ArrangementResult(windows.Count, moved);
    }
}
