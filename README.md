# MissionView

<img src="assets/branding/missionview.svg" alt="MissionView logo" width="64" height="64">

A Mission Control-style window overview for Windows.

See your open windows across displays, preview one, and switch to it.

## Get started

Download the latest **MissionView Setup** from [Releases](https://github.com/JUANES545/missionview/releases/latest), run it, then open MissionView from the Start menu.

Requires **Windows 11 x64, version 24H2 (build 26100) or later**. The download includes the .NET runtime.

Press `Win+W` to open the overview. Click a window to switch to it, or press `Escape` to return.

## Find your window

Hold `Space` to enlarge a live preview. Keep holding it while you navigate to preview another window.

Overlapping previews stay together near their original positions when a compact group fits, without moving independent previews. Otherwise, MissionView keeps its individual-placement and grid fallbacks.

Point at an application and press `Win+Shift+W` to see its windows with App Exposé. Browse its minimized windows in the bottom shelf; they stay minimized until you choose one.

Drag a window preview to another display and release to move it. Press `Escape` to cancel the drag.

Use the desktop button or `Alt+S` in the overview to show or restore the desktop. You can also use the tray menu.

## Keyboard controls

| Key | Action |
| --- | --- |
| Overview shortcut (default `Win+W`) | Open the overview; press again to switch to the selection |
| App Exposé shortcut (default `Win+Shift+W`) | Show windows from the application under the pointer; press again to switch to the selection |
| Arrow keys | Select a window in that direction |
| `Tab` / `Shift+Tab` | Select the next / previous window in visual order |
| `Enter` | Switch to the selected window |
| Hold `Space` | Enlarge the selected live preview |
| `Alt+S` | Show or restore the desktop from the overview |
| `Escape` | Cancel without switching |

Set either shortcut in **Settings → Shortcuts**. App Exposé follows `Shift` + the overview shortcut until you assign it a separate combination. If the overview shortcut already includes `Shift`, assign App Exposé its own shortcut.

MissionView can intercept supported occupied shortcuts while it runs. Settings shows a notice before **Save**; `Win+W` uses MissionView instead of Widgets. OS-protected shortcuts such as `Win+L` and `Ctrl+Alt+Delete` remain unavailable.

## Make it yours

Right-click the tray icon to open **Settings**. Select an info button for inline help. Choose startup, shortcuts, hot corners, thumbnail size, background dimming and a White, Blue or Mint selection border (White by default). Show the overview on all displays or only the display under the pointer; moving between displays requires all displays. Hide minimized windows from App Exposé or reduce animations. **Reset settings** loads defaults for review; **Save** applies changes and **Cancel** discards them. Check for updates from Settings or the tray; MissionView asks before starting the installer.

[MIT](LICENSE). Based on [miguelo96/WindowScatter](https://github.com/miguelo96/WindowScatter), via [RuoChen5251/WindowScatter](https://github.com/RuoChen5251/WindowScatter). Original copyright notices remain in LICENSE.
