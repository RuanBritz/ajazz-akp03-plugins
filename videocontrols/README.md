# Video Controls – AJAZZ AKP03 / Stream Dock plugin

Camera control on the AKP03. Separate from Audio Controls, with its own build and install.

| Action | Put it on | What it does |
|---|---|---|
| **Toggle Camera** | a key | Blanks / restores your video in NVIDIA Broadcast. Green = live, red = blank. |

## One-time setup: Blank Camera
The key works by switching NVIDIA Broadcast's camera to a virtual camera called **Blank Camera**, which only
ever shows black. That camera is a small native DLL that has to be built and installed once.

1. **C++ tools (once):** in the Visual Studio Installer, add **Desktop development with C++** with a
   Windows 11 SDK.
2. **Build:** run `build-blankcam.bat`. It builds `blankcam\bin\BlankCamera.dll` and `BlankCamSetup.exe`.
3. **Install:** run `setup-blankcam.bat` and accept the one admin prompt. It copies both files to
   `%ProgramFiles%\VideoControls\BlankCamera\` (Windows' Frame Server can't read your user profile),
   registers the DLL and creates the camera. The camera survives reboots.
4. **Broadcast:** restart NVIDIA Broadcast if **Blank Camera** isn't in its camera list yet.

To remove it, run `setup-blankcam.bat uninstall`. Blank Camera also shows up in other apps' camera lists
(for example Meet's own picker); that's harmless.

## Install the plugin
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
- **Press while live:** the key asks NVIDIA Broadcast which camera is active, remembers it and its
  resolution, and switches Broadcast to Blank Camera. The key turns red straight away.
- **Press while blank:** Broadcast goes back to the remembered camera at the same resolution. Green.
- Meet / Teams / Zoom keep using **Camera (NVIDIA Broadcast)** the whole time, so the call never loses its
  camera and nobody sees "camera off": they just see black.
- The key colour comes from Broadcast itself (checked every 3 s), so switching the camera in Broadcast's own
  window shows up on the key too.
- The remembered camera is saved in the key's settings, so it survives restarting Stream Dock or Broadcast.
- Talks to Broadcast's local control server (`http://127.0.0.1:<port>/gateway`, port from
  `%APPDATA%\nvidia-broadcast\gateway.json`). No admin rights per press. The plugin checks that the server
  really is NVIDIA Broadcast before sending anything.
- With background replace on, a blank feed may show your background image rather than pure black.

## How it works
- Stream Dock starts `bin\VideoControls.exe`, which connects to Stream Dock over a local WebSocket and routes
  each event to its action by action UUID.
- Source, in `src/`:
  - `Program.cs` – WebSocket loop, event routing, shared send / log helpers.
  - `CameraAction.cs` – the Toggle Camera key.
  - `Broadcast.cs` – minimal MCP client for NVIDIA Broadcast's gateway (`list_devices`, `set_active_device`).
- `blankcam/` – the Blank Camera, C++: a Media Foundation media source that outputs black NV12 / RGB32 frames
  at 1080p30 and 720p30 (`MediaSource.cpp`, `Dll.cpp`), and `BlankCamSetup.cpp`, which registers it with
  `MFCreateVirtualCamera`. Design notes: [`docs/blank-camera-plan.md`](docs/blank-camera-plan.md).

## Adding an action
1. Add it to `Actions` in `manifest.json` with a UUID starting `com.local.videocontrols.`.
2. Add a class deriving from `PluginAction` in `src/` and register it in the `actions` array in `Program.cs`.

## Troubleshooting
- Check `bin\videocontrols.log` inside the installed plugin folder.
- Plugin not listed: make sure the folder name ends in `.sdPlugin` and restart Stream Dock fully.
- **Warning triangle, log says "Broadcast gateway not found" or "Broadcast unavailable":** NVIDIA Broadcast
  isn't running (or is older than 2.3, which has no gateway). Start it; the key colour is left alone until then.
- **Warning triangle, log says "Blank Camera is not in Broadcast's camera list":** run `setup-blankcam.bat`,
  then restart NVIDIA Broadcast. Check that **Blank Camera** appears in the Windows Camera app.
- **Video comes back at a different resolution:** the log notes it; pick the resolution in Broadcast once and
  the key remembers it.
