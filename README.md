<img src="assets/icon.svg" width="64" height="64" alt="">

# Window Restorer

A small Windows 10 tray app that remembers where your windows are and puts them back when Windows moves them.

## The problem

On Windows 10 with more than one monitor, windows often end up in the wrong place:

- After waking from sleep, everything is piled onto one screen. Many monitors (especially over DisplayPort) report themselves as unplugged while asleep, so Windows moves their windows away and doesn't move them back.
- After unplugging a laptop from a dock and plugging it back in, the windows stay on the laptop screen.
- After a reboot, programs open wherever they like instead of where you left them.

Windows has no built-in way to fix this, so you end up dragging every window back by hand.

## What it does

Window Restorer saves the position, size, maximized/minimized state and monitor of every window. It saves automatically as you work and right before shutdown. When monitors change, the PC wakes up, or you log in, it puts the windows back.

It keeps a separate layout for each set of monitors (e.g. "laptop only" and "laptop + 2 monitors"), so working undocked never overwrites your docked layout. Windows are recognised by program and title, so it still works after a reboot.

You can also do it by hand from the tray icon menu: **Remember now**, **Restore now** and **Preview restore…** (shows what would move, without moving anything).

## Installing

1. Install the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) if you don't have it.
2. Download `WindowRestorer.exe` from the [latest release](https://github.com/PLyczkowski/window-restorer/releases/latest) and put it in a folder where it can stay, e.g. `C:\Program Files\WindowRestorer`.
3. Run it, then turn on **Start with Windows** in **Settings…** (tray icon menu).

By default it runs as administrator, because Windows doesn't let normal programs move windows of programs running as admin. This can be switched off in Settings.

To build it yourself instead, see [How it works](docs/how-it-works.md#installing).

## More

See [How it works](docs/how-it-works.md) for all triggers and settings, fallbacks, saved files, known limits and building from source.

## Licence

[MIT](LICENSE)
