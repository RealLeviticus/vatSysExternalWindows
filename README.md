# vatSys External Windows

This plugin mirrors native vatSys windows to a browser on your local network (for example, iPad Safari).

## What It Does

- Hosts an embedded web UI from inside the vatSys plugin process.
- Lists currently open vatSys windows (ASD, Ground, and other default forms).
- Streams a live JPEG view of the selected window over LAN.
- Sends tap interactions back to the window:
  - Tap = left click
  - Long press (or right click) = right click

## Build Requirements

- .NET Framework 4.8.1 build tools
- vatSys installed locally (for `vatSys.exe` reference)

## Build

From the workspace root:

```powershell
dotnet build .\VatSysExternalWindows\VatSysExternalWindows.csproj -p:VatSysDir="C:\Path\To\vatSys"
```

Optional output override:

```powershell
dotnet build .\VatSysExternalWindows\VatSysExternalWindows.csproj -p:VatSysDir="C:\Path\To\vatSys" -p:VatSysProfilePluginsDir="C:\Path\To\Profiles\Australia\Plugins\VatSysExternalWindows\"
```

The project defaults output to:

`%UserProfile%\Documents\vatSys Files\Profiles\Australia\Plugins\VatSysExternalWindows\`

## Use In vatSys

1. Start vatSys and load profile/plugins.
2. Open the windows you want mirrored (for example ASD or Ground windows).
3. In vatSys menu, click `External Windows Web UI`.
4. On iPad, open one of the shown URLs, such as `http://<your-pc-ip>:5300/`.
5. Select a window in the web UI and begin using it.

## Notes

- For reliable capture, keep the source window visible and not minimized.
- If input stops responding, click once in the source window on PC and retry from iPad.
- High FPS and high JPEG quality increase CPU and network usage.
