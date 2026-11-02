# Mic Mute – AJAZZ AKP03 / Stream Dock plugin

Toggles mute on your default Windows microphone. The key shows green (live) or red (muted),
and stays in sync if you mute from Teams, Windows settings or a headset button.

## Install
1. Close the AJAZZ Stream Dock app (check the system tray too).
2. Run `install.bat`. It compiles the plugin with Windows' built-in C# compiler and copies it to
   `%APPDATA%\HotSpot\StreamDock\Plugins\com.local.micmute.sdPlugin`.
3. Start Stream Dock and drag **Mic Mute → Toggle Mic Mute** onto a key.

To build without installing, run `build.bat`.

## How it works
- `src/Program.cs` is launched by Stream Dock, connects over its local WebSocket and handles
  `willAppear`, `willDisappear` and `keyDown`.
- Mute uses the Windows Core Audio API (`IAudioEndpointVolume`) on the default capture device,
  for both the "default" and "default communications" roles, so it works for Teams/Zoom/Discord too.
- It polls the mute state every second to keep the key in sync with outside changes.

## Tray icon
While Stream Dock is running, a mic icon sits in the taskbar notification area: green when live, red when muted.
Left-click it to toggle mute; right-click → **Hide tray icon** to hide it until the plugin restarts. If it lands in
the overflow (^) menu, pin it via Settings → Personalization → Taskbar → Other system tray icons.

The built-in Windows microphone indicator (the "app is using your mic" icon) is controlled by Windows and can't be
changed by a plugin.

## Troubleshooting
- Key shows a warning triangle: no default microphone was found.
- Anything else: check `bin\micmute.log` inside the installed plugin folder.
- Plugin not listed: make sure the folder name ends in `.sdPlugin` and restart Stream Dock fully.

## Customising
- Rename the plugin: change `UUID` values in `manifest.json` and the folder name to match.
- Icons: replace the PNGs in `images/` (72x72 plus 144x144 `@2x` versions for keys).
