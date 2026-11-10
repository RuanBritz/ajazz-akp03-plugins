# Audio Controls – AJAZZ AKP03 / Stream Dock plugin

Windows audio and camera on the AKP03, in one plugin with four actions:

| Action | Put it on | What it does |
|---|---|---|
| **Toggle Mic Mute** | a key | Mutes / unmutes your default microphone. Green = live, red = muted. |
| **Device Volume** | a knob | Turn to change a chosen output device's volume, press to mute. |
| **Switch Output Device** | a key | Press to make the next device in your list the Windows default output. |
| **Toggle Camera** | a key | Blocks / allows the camera for all apps. Green = allowed, red = blocked. |

## Install
1. Close the AJAZZ Stream Dock app (check the system tray too).
2. Run `install.bat`. It compiles the plugin with Windows' built-in C# compiler and copies it to
   `%APPDATA%\HotSpot\StreamDock\Plugins\com.local.audiocontrols.sdPlugin`.
3. Start Stream Dock and drag actions from the **Audio Controls** category onto keys / knobs.

To build without installing, run `build.bat`.

## Toggle Mic Mute
- Mutes the default capture device for both the "default" and "default communications" roles,
  so it works for Teams / Zoom / Discord too.
- Stays in sync if you mute from Teams, Windows settings or a headset button.
- Warning triangle on the key: no default microphone was found.

## Device Volume
- In the settings panel pick the **Device** and the **Step** (volume change per knob click).
- **Default output device** follows whatever Windows currently uses; any other choice stays on that device,
  so each knob can control a different device (e.g. headset on one, speakers on another).
- Turning the knob up while muted unmutes, like the Windows volume keys.
- The knob title shows the volume (or "Muted"). If the chosen device is unplugged it shows "No device"
  and picks the device up again when it's reconnected.

## Switch Output Device
- In the settings panel tick the devices to switch between. Each press moves to the next ticked device.
  - Tick **one** device for a "switch to this device" key.
  - Tick **none** to cycle through every active output.
- **Also switch communications device** (on by default) moves Teams / Zoom / Discord audio too.
  Untick it to keep calls on the same device.
- The key shows the current default output and updates if you change it in Windows.
  Names are shortened ("Speakers (JBL Charge 3)" → "Speakers"); if two devices share a short name the
  part in brackets is shown instead. Renaming a device in Windows sound settings changes what's shown.
- The key colour shows which of your devices is active: 1st ticked device green, 2nd blue, 3rd orange,
  4th purple, then teal and red. It's grey when the current output isn't one of the ticked devices, so a
  one-device key lights up only while that device is in use.
- The plugin draws the key image itself (`src/KeyImage.cs`), so the name is sized to fit. The title
  you can type in Stream Dock isn't used for this action.
- Ticked devices that are unplugged are skipped. Warning triangle: none of the ticked devices are connected.

## Toggle Camera
- Flips the Windows camera privacy setting (Settings > Privacy & security > Camera): both
  **Let apps access your camera** and **Let desktop apps access your camera**, so Teams / Zoom / Discord /
  browsers are all blocked. No admin rights needed.
- Unblocking turns both of those switches on. Per-app camera permissions are left alone.
- An app that already has the camera open may keep its picture until it reopens the camera (e.g. turn
  video off and on in the call, or rejoin). New attempts to use the camera are blocked straight away.
- Stays in sync if you change the setting in Windows Settings.
- Warning triangle on the key: **Camera access** for the whole device is turned off in Windows. The key
  shows red, and only an admin can turn that back on in Settings.

## How it works
- Stream Dock starts `bin\AudioControls.exe` once, and it serves every key and knob using these actions.
  It connects to Stream Dock over a local WebSocket and routes each event to its action by action UUID.
- Every second it re-checks mic mute, volumes, the default device and the camera setting, so changes made outside the
  plugin show up on the keys.
- Source, in `src/`:
  - `Program.cs` – WebSocket loop, event routing, shared send / log helpers.
  - `MicMuteAction.cs`, `VolumeAction.cs`, `OutputSwitchAction.cs`, `CameraAction.cs` – one file per action.
  - `KeyImage.cs` – draws the Switch Output Device key (colour, glyph, device name).
  - `Audio.cs` – Windows Core Audio API (`IAudioEndpointVolume`, device enumeration) and default-device
    switching via `IPolicyConfig`. That interface is undocumented but is what Windows itself and other audio
    switchers use.
  - `Interop.cs` – the COM interface declarations.
  - `Camera.cs` – reads and writes the camera privacy setting in the registry
    (`HKCU\...\CapabilityAccessManager\ConsentStore\webcam`).
- `inspectors/` holds the settings panels for the volume knob and the switch key.

## Adding an action
1. Add it to `Actions` in `manifest.json` with a UUID starting `com.local.audiocontrols.`.
2. Add a class deriving from `PluginAction` in `src/` and register it in the `actions` array in `Program.cs`.

## Troubleshooting
- Device missing from a settings panel: make sure it's enabled in Windows sound settings, then click
  **Refresh device list**.
- Anything else: check `bin\audiocontrols.log` inside the installed plugin folder.
- Plugin not listed: make sure the folder name ends in `.sdPlugin` and restart Stream Dock fully.

## Customising
- Icons: replace the PNGs in `images/` (72x72 plus 144x144 `@2x` versions for key / knob images,
  20x20 plus 40x40 for the `*Action` list icons).
