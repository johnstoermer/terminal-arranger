# Terminal Rearranger

A tiny, draggable, always-on-top monitor selector for Windows.

- Click a monitor to gather and tile terminal windows on it. Click again to refresh.
- Every terminal uses the same width. Incomplete rows leave empty space.
- The focused terminal gets a white outline with a moving white highlight. It follows the window as you move or resize it, lets clicks pass through, and disappears when another app has focus.
- Click the fork icon inside a terminal's bottom-right corner to open its Codex session in a new Windows Terminal window. The arrangement refreshes on the selected monitor, or the source monitor before a monitor has been selected.
- Drag the six dots to move the widget. The position and last selection are remembered.
- Close with the × control or Escape. Launch it again from the desktop shortcut.
- Monitor details and any arrangement errors appear only in tooltips.

Each click rescans visible terminal windows on the current virtual desktop, including minimized windows. Tabs and panes stay inside their existing windows. The taskbar area is excluded; other application windows are left alone. Launching the widget does not move anything.

Recognizes Windows Terminal, classic Command Prompt/PowerShell/WSL consoles, PuTTY/KiTTY, mintty/Git Bash, WezTerm, Alacritty, ConEmu, Hyper, Tabby, and Ghostty through their window class or process name. Embedded editor terminals are part of their editor window and cannot be tiled separately. An elevated terminal may require running the rearranger as administrator. Windows that enforce large minimum sizes may not fit a crowded layout; the widget reports partial arrangements.

## Forking Codex

Forking requires a local Windows `codex.exe` session and Windows Terminal. It runs `codex fork <session-id> --cd <source-directory>` using the source executable and Codex home. PowerShell stays open when Codex exits. WSL and SSH sessions are not supported by session detection.

Every visible terminal has a fork icon, including windows that have never received focus. The icon follows its terminal, stays below apps covering it, and hides when the terminal is minimized. Further clicks are ignored until the new window is ready and the layout has refreshed. Errors appear in a tooltip.

Session detection reads the metadata header of the rollout currently open for writing by each Codex process and maps its console to the terminal window. It does not choose the newest session or use `--last`. In a window with tabs or panes, the console title must uniquely match the active terminal title. Keep application titles enabled; if two sessions have the same title, move the source tab to its own window before forking. A session that has not yet been saved cannot be forked.

Detection was verified with Windows Terminal 1.24 and Codex CLI 0.155.1 on Windows 11. It depends on Windows process-handle information and Codex rollout metadata, which may change in future versions. The launcher identifies the new window through its own console handshake before arranging it.

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

Forking uses the [Codex CLI fork command](https://developers.openai.com/codex/cli/reference#codex-fork), [Windows Terminal's new-window option](https://learn.microsoft.com/en-us/windows/terminal/command-line-arguments), and [console attachment](https://learn.microsoft.com/en-us/windows/console/attachconsole) in a separate helper process.
