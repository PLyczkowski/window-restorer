# How Window Restorer works

Detailed behaviour, files, limits and code layout. For a quick overview see the [README](../README.md).

## Using it

Right-click the tray icon:

- **Remember now** saves the current layout.
- **Restore now** puts windows back where they were saved.
- **Preview restore…** shows what a restore would do (which saved spot each window matched and where it would go) without moving anything.
- **Settings…** opens the triggers, fallback and startup options, plus the **Saved windows** list, where entries can be forgotten. Starting the exe again while it runs also opens Settings.
- **Open data folder** opens the saved files and the log.

## What it does automatically

All of these can be switched off in Settings.

- **Saving before shutdown.** Before shutdown, restart or sign-out, it saves the layout. It asks Windows to notify it before other programs, so their windows are still open.
- **Auto-save.** It saves after you move, resize or minimize a window, and every 60 seconds.
- **Restore at login.** When started by the login task, it watches for 3 minutes and places each window as it opens. Each window gets about 1.5 seconds first, because many programs position themselves right after opening.
- **Restore on monitor change.** When monitors are connected, disconnected or rearranged, it waits until the setup has been stable for 3 seconds, then restores the layout for the monitors now connected. It restores even when the same monitors are back as before, since a monitor that switched off and on again still made Windows move windows. It checks again 5 seconds later, because Windows sometimes moves windows once more after a monitor returns.
- **Restore after wake.** After waking from sleep and when the screens turn back on, it restores the same way. Some monitors (especially over DisplayPort) report themselves as unplugged while asleep, which is what makes Windows pile windows onto one screen.

Auto-save pauses while monitors are changing, while the PC sleeps, while the screens are off and during the login placement. That way a layout Windows has scrambled never overwrites the good one. Before every save it also checks that the monitors are still the ones it last saw settle.

## How windows are remembered

- **Profiles per monitor set.** Layouts are kept separately for each combination of connected monitors, e.g. "laptop only" and "laptop + 2 monitors". Monitors are identified by their hardware id, so saving while undocked never overwrites the docked layout.
- **Matching windows.** Window handles change after every reboot, so a saved window is recognised by its program path + window class + title. Matching goes from strict to loose: the very same window (while it stays open, even if its title changes) → exact title → same program with the most similar title (restore only).
- **Closed windows are kept.** Saving updates the entries of open windows and leaves the entries of closed windows untouched. When that window opens again it gets its old spot back. An entry is only replaced when a matching window is saved over it, or forgotten by hand in Settings → Saved windows.

## Fallbacks

- **Same monitors, different arrangement or resolution:** the position is moved and scaled along with the monitor.
- **Saved monitor not connected** (only possible when no layout exists for exactly these monitors and the closest saved one is used): the window moves to the nearest monitor, or is left alone, depending on the setting.
- **Saved spot off-screen:** the window is pulled back into view.
- **Saved window not open:** it's skipped and its entry kept.
- **Program not responding:** it's skipped, so it can't freeze this app.
- **Window moved to a monitor with different scaling:** some windows resize themselves after the move. Every restore is checked and retried once, and anything still off is written to the log.

## Admin rights and starting with Windows

Windows doesn't let a normal program move windows of programs running as administrator. With **Run as administrator** on (the default), the app restarts itself as admin, which shows the Windows security prompt once per manual start.

**Start with Windows** creates a Task Scheduler task named `WindowRestorer` that runs at login, as admin when that setting is on, so there's no prompt at every login. Creating or removing the task needs one security prompt. The task has no battery or time limits.

Since the task runs the exe as admin without asking, any program running as you could swap the exe in the default install folder (below) and get admin rights at the next login. If that matters on your PC, copy the installed folder to `C:\Program Files\WindowRestorer` and use that copy instead.

## Installing

The simplest way is to download `WindowRestorer.exe` from the [latest release](https://github.com/PLyczkowski/window-restorer/releases/latest), put it in a folder of your choice and run it. It needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). If **Start with Windows** is on, turn it off and on again after moving the exe, so the login task points to the new place.

To install from source, `pwsh tools/install.ps1` builds a release copy into `%LocalAppData%\Programs\WindowRestorer` and creates a **Window Restorer** shortcut on the desktop. Run it again to update after code changes. Exit the app from its tray menu first, since a running exe can't be replaced.

## Files

Everything is in `%AppData%\WindowRestorer`:

- `layouts.json`: saved profiles and windows (readable JSON).
- `settings.json`
- `log.txt`: what was saved and restored and why, plus any failures. It rolls over to `log.old.txt` at 2 MB.

`--data-dir <folder>` uses another folder instead (one running copy per folder).

## Limits

- Windows on other virtual desktops are ignored (Windows 10 has no public way to move them between desktops).
- Snapped windows come back at the same position and size, but Windows no longer treats them as snapped.
- Store apps and some Electron apps are harder to tell apart. When titles change a lot, a window may pick a different saved spot of the same program; Preview shows the matches.
- Windows that only exist while the program runs (dialogs, tool palettes, popups) aren't tracked.

## Building

Needs the .NET 8 SDK.

```
dotnet build
dotnet test tests/WindowRestorer.Tests
dotnet publish src/WindowRestorer -c Release -o publish -p:PublishSingleFile=true --self-contained false -p:DebugType=none
```

The published app is a single exe of about 300 KB (the one attached to releases) and needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

### Code layout

- `src/WindowRestorer/Core`: no UI.
  - `WindowEnumerator`: lists and reads windows.
  - `Displays`: monitors and profile keys.
  - `WindowMatcher`: saved entry ↔ open window.
  - `PlacementPlanner`: target position and fallbacks.
  - `WindowMover`: applies a placement.
  - `LayoutService`: remember / plan / restore, and the saved file.
- `src/WindowRestorer/App`: the app around the core.
  - `TrayApp`: the tray menu.
  - `TriggerController`: auto-save, settle logic, login placement.
  - `SystemEventsWindow`: shutdown, display and power notifications.
  - `StartupTask`: the login task.
  - The Settings and Preview windows.
- `tests/WindowRestorer.Tests`:
  - End-to-end tests that open real windows through `tests/TestWindows`, then remember, disturb and restore them with the same `LayoutService` the app uses. They only touch their own windows, but those windows briefly appear on screen.
  - Planner tests with made-up monitor setups for the monitor fallbacks.
- `tools/make-icon.ps1`: rebuilds `assets/icon.ico` from the same design as `assets/icon.svg`.
