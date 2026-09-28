# Terminal Rearranger

A tiny, draggable, always-on-top monitor selector for Windows.

- Click a monitor to gather and tile terminal windows on it. Click again to refresh.
- Every terminal uses the same width. Incomplete rows leave empty space.
- The focused terminal gets a white outline with a moving white highlight. It follows the window as you move or resize it, lets clicks pass through, and disappears when another app has focus.
- Drag the six dots to move the widget. The position and last selection are remembered.
- Close with the × control or Escape. Launch it again from the desktop shortcut.
- Monitor details and any arrangement errors appear only in tooltips.

Each click rescans visible terminal windows on the current virtual desktop, including minimized windows. Tabs and panes stay inside their existing windows. The taskbar area is excluded; other application windows are left alone. Launching the widget does not move anything.

Recognizes Windows Terminal, classic Command Prompt/PowerShell/WSL consoles, PuTTY/KiTTY, mintty/Git Bash, WezTerm, Alacritty, ConEmu, Hyper, Tabby, and Ghostty through their window class or process name. Embedded editor terminals are part of their editor window and cannot be tiled separately. An elevated terminal may require running the rearranger as administrator. Windows that enforce large minimum sizes may not fit a crowded layout; the widget reports partial arrangements.

## Build

Requires the .NET 9 SDK. The published executable uses the .NET 9 Desktop Runtime.

```powershell
.\build.ps1
dotnet run --project tests/TerminalRearranger.Checks.csproj -c Release
```

The executable is `dist/TerminalRearranger.exe`. Preferences are stored in `%LOCALAPPDATA%\TerminalRearranger\settings.json`. The app runs only when launched; it does not install a background service or startup entry.

For read-only diagnostics:

```powershell
Start-Process .\dist\TerminalRearranger.exe -ArgumentList '--inspect', '.\artifacts\desktop.json' -Wait
```

Window placement uses [SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos) and [DWM visible frame bounds](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute) to account for invisible borders and different monitor scale factors.
