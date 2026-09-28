using System.Runtime.InteropServices;
using System.Text;

namespace TerminalRearranger;

internal static class Native
{
    internal const uint MoveFlags = 0x0010 | 0x0200 | 0x4000; // No activation, no owner reorder, async.
    internal delegate bool EnumWindowCallback(nint hwnd, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point2(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Size2(int width, int height) { public int Width = width, Height = height; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BlendFunction
    {
        public byte Operation, Flags, ConstantAlpha, AlphaFormat;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        public uint HeaderSize;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPixelsPerMeter, YPixelsPerMeter;
        public uint ColorsUsed, ColorsImportant, FirstColor;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")]
    internal static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(nint hwnd, StringBuilder name, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindow(string? className, string? title);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hwnd, out Rect bounds);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateLayeredWindow(nint hwnd, nint destinationDc,
        ref Point2 destination, ref Size2 size, nint sourceDc, ref Point2 source,
        uint colorKey, ref BlendFunction blend, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage,
        out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")]
    internal static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint dc);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    internal static extern int GetDwmRect(nint hwnd, uint attribute, out Rect value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    internal static extern int GetDwmInt(nint hwnd, uint attribute, out int value, int size);
    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int size);

    internal static Rectangle VisibleBounds(nint hwnd)
    {
        if (GetDwmRect(hwnd, 9, out var frame, Marshal.SizeOf<Rect>()) == 0 && frame.Right > frame.Left)
            return frame.ToRectangle();
        return GetWindowRect(hwnd, out var outer) ? outer.ToRectangle() : Rectangle.Empty;
    }

    internal static bool MoveVisibleFrame(nint hwnd, Rectangle target)
    {
        if (!GetWindowRect(hwnd, out var outer)) return false;
        Rectangle frame = VisibleBounds(hwnd);
        // DWM's visible frame omits the invisible resize borders. Recalculate each pass
        // because the destination monitor may use a different DPI.
        int left = Math.Clamp(frame.Left - outer.Left, 0, 48);
        int top = Math.Clamp(frame.Top - outer.Top, 0, 48);
        int right = Math.Clamp(outer.Right - frame.Right, 0, 48);
        int bottom = Math.Clamp(outer.Bottom - frame.Bottom, 0, 48);
        return SetWindowPos(hwnd, 0, target.X - left, target.Y - top,
            target.Width + left + right, target.Height + top + bottom, MoveFlags);
    }

    internal static void KeepOnTop(nint hwnd) =>
        SetWindowPos(hwnd, new nint(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
}
