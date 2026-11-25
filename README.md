# MissionView

<img src="assets/branding/missionview.svg" alt="MissionView logo" width="64" height="64">

A Mission Control-style window overview for Windows.

See your open windows across displays, preview one, and switch to it.

## Get started

Download the latest **MissionView Setup** from [Releases](https://github.com/JUANES545/missionview/releases/latest), run it, then open MissionView from the Start menu.

Requires **Windows 11 x64, version 24H2 (build 26100) or later**. The download includes the .NET runtime.

Press `Win+W` to open the overview. Click a window to switch to it, or press `Escape` to return.

## Find your window

Type part of a window title to filter the overview, including App Exposé. Matching previews stay in their slots. Press `Escape` to clear the search.

With the search empty, hold `Space` to enlarge a live preview. Keep holding it while you navigate to preview another window.

Overlapping previews stay together near their original positions when a compact group fits, without moving independent previews. Otherwise, MissionView keeps its individual-placement and grid fallbacks.

Closing or adding a window keeps unchanged previews in their last positions when a local placement fits. Reopening the same overview uses that layout too. Moving or resizing a real window reanchors its preview; changing displays, DPI, thumbnail size, overview scope or application discards the incompatible layout. Positions stay in memory only, without saving window titles or screenshots.

Point at an application and press `Win+Shift+W` to see its windows with App Exposé. Browse its minimized windows in the bottom shelf; they stay minimized until you choose one.

Drag a window preview to another display and release to move it. Press `Escape` to cancel the drag.

Once the overview settles, middle-click a preview or click its circular **X** to request normal window closure. The X appears at the top-left when you point at or select a preview, including displayed minimized windows in App Exposé. Select a window and open its menu with `Shift+F10` or the Menu key to choose **Close window** without a mouse.

Ordinary successful closes keep you in the overview. MissionView keeps the preview until Windows no longer identifies the original window. It leaves the overview for a visible, enabled owned prompt when its owner is disabled, or when the last requested window or one of its owned windows takes focus. It checks window identity and ownership before handing off; it does not treat other windows from the same process as prompts.

Custom or in-window prompts may not be identifiable. Press `Escape` to check the last window you requested closed, rather than the window you were using before opening the overview, if it still matches its captured identity. MissionView restores that window only if needed and respects another application's foreground focus. It sends one close request per window in an overview session; reopening the overview lets you make a new request. It does not dismiss save prompts or force an application to quit.

Use the desktop button or `Alt+S` in the overview to show or restore the desktop. You can also use the tray menu.

## Keyboard controls

| Key | Action |
| --- | --- |
| Overview shortcut (default `Win+W`) | Open the overview; press again to switch to the selection |
| App Exposé shortcut (default `Win+Shift+W`) | Show windows from the application under the pointer; press again to switch to the selection |
| Arrow keys | Select a matching window in that direction |
| `Tab` / `Shift+Tab` | Select the next / previous matching window in visual order |
| `Enter` | Switch to the selected window |
| Hold `Space` | Enlarge the selected live preview when the search is empty |
| `Shift+F10` / Menu key | Open the selected window's menu, including **Close window** |
| `Alt+S` | Show or restore the desktop from the overview |
| `Escape` | Clear the search, or cancel without switching if the search is empty |

Set either shortcut in **Settings → Shortcuts**. App Exposé follows `Shift` + the overview shortcut until you assign it a separate combination. If the overview shortcut already includes `Shift`, assign App Exposé its own shortcut.

MissionView can intercept supported occupied shortcuts while it runs. Settings shows a notice before **Save**; `Win+W` uses MissionView instead of Widgets. OS-protected shortcuts such as `Win+L` and `Ctrl+Alt+Delete` remain unavailable.

### Switch windows during a file drag (experimental)

Start dragging a disposable test file in Explorer with the left mouse button and keep it held. Press your overview shortcut, wait for the previews to settle, then move onto the body of a non-minimized window preview on the current virtual desktop. With **All displays** enabled, you can hover a preview on another display. Press the same shortcut again while still holding the button, then move to the real destination and release there.

MissionView checks only for the file-drop format and never accepts the drop. Press `Escape` to cancel. If you release before switching, press the shortcut again to close the overview without switching. App Exposé and swapped mouse buttons are outside this prototype. Windows must deliver the shortcut and drag events during the Explorer drag and allow the target to take focus; the target app decides whether it accepts the file.

## Make it yours

Right-click the tray icon to open **Settings**. Select an info button for inline help. Choose startup, shortcuts, hot corners, thumbnail size, background dimming and a White, Blue or Mint selection border (White by default). Show the overview on all displays or only the display under the pointer; moving between displays requires all displays. Hide minimized windows from App Exposé or reduce animations. **Reset settings** loads defaults for review; **Save** applies changes and **Cancel** discards them. Check for updates from Settings or the tray; MissionView asks before starting the installer.

[MIT](LICENSE). Based on [miguelo96/WindowScatter](https://github.com/miguelo96/WindowScatter), via [RuoChen5251/WindowScatter](https://github.com/RuoChen5251/WindowScatter). Original copyright notices remain in LICENSE.
