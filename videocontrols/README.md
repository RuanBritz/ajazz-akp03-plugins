# Video Controls – AJAZZ AKP03 / Stream Dock plugin

Camera control on the AKP03. Separate from Audio Controls, with its own build and install.

| Action | Put it on | What it does |
|---|---|---|
| **Toggle Camera** | a key | Blocks / allows the camera for all apps. Green = allowed, red = blocked. |

> **Being reworked.** The current key uses the Windows camera privacy setting, which doesn't reach video
> that goes through NVIDIA Broadcast (e.g. Google Meet with "Camera (NVIDIA Broadcast)"). The plan to switch
> Broadcast to a blank virtual camera instead is in [`docs/blank-camera-plan.md`](docs/blank-camera-plan.md).

## Install
1. Close the AJAZZ Stream Dock app (check the system tray too).
2. Run `install.bat`. It compiles the plugin with Windows' built-in C# compiler and copies it to
   `%APPDATA%\HotSpot\StreamDock\Plugins\com.local.videocontrols.sdPlugin`.
3. Start Stream Dock and drag **Toggle Camera** from the **Video Controls** category onto a key.

To build without installing, run `build.bat`.

To uninstall, close Stream Dock and run `uninstall.bat`. It deletes the installed plugin folder; then remove
any leftover Toggle Camera keys in Stream Dock.

If you placed a Toggle Camera key from Audio Controls earlier, remove it and use the one from Video Controls.
Reinstalling Audio Controls doesn't delete the old camera files from its installed folder, but they're unused.

## Toggle Camera
- Flips the Windows camera privacy setting (Settings > Privacy & security > Camera): both
  **Let apps access your camera** and **Let desktop apps access your camera**, so Teams / Zoom / Discord /
  browsers are all blocked. No admin rights needed.
- Unblocking turns both of those switches on. Per-app camera permissions are left alone.
- An app that already has the camera open may keep its picture until it reopens the camera (e.g. turn
  video off and on in the call, or rejoin). New attempts to use the camera are blocked straight away.
- Doesn't affect apps using a virtual camera such as NVIDIA Broadcast's (see the note above).
- Stays in sync if you change the setting in Windows Settings.
- Warning triangle on the key: **Camera access** for the whole device is turned off in Windows. The key
  shows red, and only an admin can turn that back on in Settings.

## How it works
- Stream Dock starts `bin\VideoControls.exe`, which connects to Stream Dock over a local WebSocket and routes
  each event to its action by action UUID. Every second it re-checks the camera setting.
- Source, in `src/`:
  - `Program.cs` – WebSocket loop, event routing, shared send / log helpers.
  - `CameraAction.cs` – the Toggle Camera key.
  - `Camera.cs` – reads and writes the camera privacy setting in the registry
    (`HKCU\...\CapabilityAccessManager\ConsentStore\webcam`).

## Adding an action
1. Add it to `Actions` in `manifest.json` with a UUID starting `com.local.videocontrols.`.
2. Add a class deriving from `PluginAction` in `src/` and register it in the `actions` array in `Program.cs`.

## Troubleshooting
- Check `bin\videocontrols.log` inside the installed plugin folder.
- Plugin not listed: make sure the folder name ends in `.sdPlugin` and restart Stream Dock fully.
