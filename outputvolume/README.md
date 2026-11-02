# Output Volume – AJAZZ AKP03 / Stream Dock plugin

Puts a Windows output device on one of the AKP03 knobs. Turn the knob to change that device's volume,
press it to mute / unmute. Each knob can control a different device (e.g. headset on one, speakers on another).

## Install
1. Close the AJAZZ Stream Dock app (check the system tray too).
2. Run `install.bat`. It compiles the plugin with Windows' built-in C# compiler and copies it to
   `%APPDATA%\HotSpot\StreamDock\Plugins\com.local.outputvolume.sdPlugin`.
3. Start Stream Dock and drag **Output Volume → Device Volume** onto a knob.
4. In the settings panel below, pick the **Device** and the **Step** (volume change per knob click).

To build without installing, run `build.bat`.

## Behaviour
- **Default output device** follows whatever Windows currently uses; any other choice stays on that device.
- Turning the knob up while muted unmutes, like the Windows volume keys.
- The knob title shows the volume (or "Muted") and refreshes every second, so changes made in the
  Windows mixer show up too.
- If the chosen device is unplugged the title shows "No device" and turning the knob shows a warning.
  It picks the device up again automatically when it's reconnected.

## How it works
- `src/Program.cs` is launched by Stream Dock, connects over its local WebSocket and handles
  `willAppear`, `willDisappear`, `dialRotate`, `dialDown` and the settings / property inspector events.
- Volume uses the Windows Core Audio API (`IAudioEndpointVolume`) on the selected render endpoint.
- `inspector.html` is the settings panel. It asks the plugin for the list of active output devices
  and saves `deviceId` and `step` as the knob's settings.

## Troubleshooting
- Device missing from the list: make sure it's enabled in Windows sound settings, then click
  **Refresh device list**.
- Anything else: check `bin\outputvolume.log` inside the installed plugin folder.
- Plugin not listed: make sure the folder name ends in `.sdPlugin` and restart Stream Dock fully.

## Customising
- Icons: replace the PNGs in `images/` (72x72 plus 144x144 `@2x` versions for the knob image).
