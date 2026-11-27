# vatSys External Windows

A vatSys plugin that serves browser-based Ground (A-SMGCS) and Air (ASD) displays to devices on your local network. It is designed for an iPad or other secondary display used alongside vatSys on your PC.

## Features

- **Ground and Air windows** — the vatSys A-SMGCS Ground window and a secondary ASD window, reproduced in the browser: the same window frame, menus, maps, tracks, labels and tool graphics
- **One renderer, ported from vatSys** — vatSys draws both window types with one control (`ASDControlDX`); the page is a port of that control's paint code (projection, map painting, track symbols, label layout, tools), not an approximation of how it looks
- **Decisions made inside vatSys** — the plugin decides each track's symbols, colour and label from vatSys's own state, exactly as vatSys's paint code does, and sends the result ready to draw
- **vatSys's live state, not the profile files** — positions, maps, colours (with your brightness settings applied), fonts, label definitions, display settings and runtime maps are read from the running vatSys, so the page follows whatever vatSys is showing
- **On-demand map loading** — layers load individually when toggled, keeping the initial page load fast
- **Works on touch devices** — drag to pan, pinch to zoom, tap to move a label, alongside vatSys's own mouse controls
- **Self-contained** — a single plugin DLL (the page is embedded in it), no dependencies beyond vatSys itself

## How It Works

The plugin starts a lightweight HTTP server on **port 5300** inside the vatSys process. It listens on the PC's network interfaces so other devices on the same local network can connect. It serves one HTML5 Canvas page for both window types (`/ground` and `/asd`), which:

1. Loads the window's positions, its Maps menu and the track symbol geometry
2. Loads map-layer geometry on demand
3. Polls live tracks every 500 milliseconds
4. Polls colours, fonts, display settings, runtime maps and tool graphics every 2 seconds
5. Paints the window on a `<canvas>` in vatSys's own coordinate frame, in vatSys's paint order

The server has no login or encryption and is intended only for a trusted local network. Do not expose port `5300` directly to the internet. It answers `GET`/`HEAD` only, sends no CORS headers (the pages are same-origin), drops requests that do not arrive within 10 seconds, and caps concurrent connections. The page only reads from vatSys; nothing you do on it changes anything in vatSys.

### API Endpoints

`type` is `ground` or `asd` (the default).

| Endpoint | Description |
|---|---|
| `GET /` | Landing page for choosing a window type |
| `GET /ground`, `GET /asd` | The window page (the same page; it takes its type from the path) |
| `GET /api/static?type=` | The window's positions (from `LogicalPositions.Positions`: name, group, airport, default centre and range, rotation, magnetic variation, maps), the maps for its Maps menu, and the track symbol geometry from Tracks.xml (DefaultTracks.xml backfill) |
| `GET /api/state?type=` | What changes while vatSys runs: the colour table as the renderer and the window chrome see it, font sizes and character cells, display settings, the runtime maps in paint order (`Below` / `Above` the file maps), the tool graphics (ATC list, visibility centres, conflict areas, closest approach, graphic routes) and, for Ground, the Crossing/Release maps the vatSys Ground window shows |
| `GET /api/maplayers?maps=Folder/Name,...` | Geometry (infills, lines, labels, symbols, runways) and paint order for specific maps |
| `GET /api/aircraft?type=&lat=&lon=&range=&icao=` | Every track the window paints, in paint order and ready to draw: position, colour, symbols, history, velocity vector, short route probe, and the label as rows of character cells. `lat`/`lon` is the window's screen centre and `range` how far its view reaches; `icao` is the Ground window's airport |

### What is reproduced

| Element | How |
|---|---|
| Window frame | `BaseForm`: 2 px border, title bar with the window title and the send-back, minimise, maximise and close buttons, in the profile's window colours. Close returns to the landing page and maximise toggles full screen; minimise and send-back have nothing to act on in a browser |
| Menus | The `MenuStrip` with cascading drop-downs: Position (Free, groups, positions), Maps (folders, maps; stays open while you toggle), Tools. Checked items use vatSys's menu check style setting |
| Projection | `ConvertLLToScreen` / `ConvertScreenToLL`: stereographic about the screen centre, rotated by the position's Rotation + MagneticVariation (Air positions too). `SetZoom` / `CalculateZoom` with vatSys's range limits (10–9999 NM Air, 0.1–30 NM Ground) and zoom clamp |
| Maps | `PaintMap`, in `DisplayMaps.Maps` order: infills (window-background and checkerboard infills included), lines per segment with vatSys's dash styles and the two-tone dashed lines of System maps, runways with extended centrelines and ticks, the 14 map symbol types cropped to their 16 px cell, labels with leader, alignment, border and per-map overlap suppression. Maps of the other window type listed by a position are painted too |
| Runtime maps | Controlled-sector infill (when vatSys's Dynamic Infill setting is on) and active restricted areas, beneath and above the file maps as vatSys paints them |
| Track symbols | Tracks.xml geometry with per-squawk variants, filled or stroked as vatSys's geometry realisations are; rotation to heading with the north correction. `PaintGroundTracks` and `PaintASDTrack` decide which symbols: wake-category ground symbols, Mode S / Mode A / ADS-B / primary, coast and cancelled symbols, flight-plan tracks, the selection symbol, ident flash |
| History, vectors, probes | History dots (`MMI.HistoryDots`), velocity vectors with minute markers, short route probes with time or distance markers |
| Labels | `PaintLabel`: the label definition vatSys parsed from Labels.xml, walked item by item with vatSys's text, widths, colours, alert handling, toggled and shared lines, leader anchor and alert box. Text is drawn the way vatSys draws it: one bi-level glyph per character cell. Handover-in and post-jurisdiction labels flash as vatSys flashes them (alert colours stay lit) |
| Colours | `Colours.GetColourDX`, so vatSys's brightness settings apply; plugin track and label colours included |
| Paint order | Tracks sorted by HMI state; on the Air window each state's symbols, then that state's labels |
| Tool graphics | Compass rose, scale marker, ATC list, visibility centres, conflict areas (LATC), closest approach (TOP) and graphic routes, when they are on in vatSys. Bearing range lines can be drawn on the page from the track selected in vatSys |

### What is not reproduced

- **Acting on flights.** Clicking label items (level, route, handoff menus), selecting tracks, quick tags, the conflict tools and Contact Me / Decouple act on vatSys's flight data; the page is a read-only view, and those Tools entries are shown disabled.
- **Text areas, lat/long points and terrain heights** are not drawn.
- **Edge anti-aliasing of infills on Air-window file maps.** vatSys draws those without anti-aliasing; a browser canvas cannot switch it off for shapes.
- **Map labels wider than 200 px** are cut off at that width; vatSys wraps them inside the same box.
- The small "visual" flight-rules glyphs are not in the Terminus font, so vatSys and the browser each fall back to a font of their own.

## Build Requirements

- Windows with .NET Framework 4.8.1 build tools (x86 target)
- vatSys installed locally (for the `vatSys.exe` assembly reference)
- Visual Studio or Build Tools with MSBuild for `build-release.ps1`
- PowerShell 5.1 or later for release packaging

## Build

From the workspace root:

```powershell
dotnet build .\VatSysExternalWindows\VatSysExternalWindows.csproj -p:VatSysDir="C:\Program Files (x86)\vatSys"
```

To create a release folder and versioned ZIP under `dist\`:

```powershell
.\build-release.ps1
```

Pass `-OpenZip` to open the completed ZIP in Windows Explorer.

Override the normal build output directory from PowerShell:

```powershell
dotnet build .\VatSysExternalWindows\VatSysExternalWindows.csproj `
  -p:VatSysDir="C:\Program Files (x86)\vatSys" `
  -p:VatSysProfilePluginsDir="C:\Path\To\Profiles\Australia\Plugins\VatSysExternalWindows\"
```

Default output: `%UserProfile%\Documents\vatSys Files\Profiles\Australia\Plugins\VatSysExternalWindows\`

## Install and Access the Display

### 1. Install the plugin

Extract the release ZIP and copy the `VatSysExternalWindows` folder into the `Plugins` folder of the vatSys profile you use. For the standard Australian profile, this is normally:

```text
C:\Users\<your-Windows-username>\Documents\vatSys Files\Profiles\Australia\Plugins\VatSysExternalWindows
```

The installed folder must contain `VatSysExternalWindows.dll`. Restart vatSys after installing or updating the plugin.

### 2. Start vatSys

Open vatSys and load your profile. The plugin starts its web server automatically on port `5300`—there is no separate program to run.

To confirm it is working on the vatSys PC, open this address in a browser on that PC:

```text
http://localhost:5300/
```

The landing page provides buttons for the Ground and Air displays. You can also open them directly at `http://localhost:5300/ground` and `http://localhost:5300/asd`.

### 3. Find your vatSys PC's IP address

The easiest method on Windows is:

1. Press **Windows + R**, type `cmd`, and press **Enter**.
2. Run `ipconfig`.
3. Find the active **Wireless LAN adapter Wi-Fi** or **Ethernet adapter** section.
4. Note its **IPv4 Address**, such as `192.168.1.42` or `10.0.0.15`.

Use the address for the adapter connected to the same home or workplace network as your iPad. Ignore disconnected adapters and addresses belonging to VPNs, virtual machines, or Bluetooth connections.

You can also find it in **Windows Settings → Network & internet → Wi-Fi (or Ethernet) → Properties**, under **IPv4 address**.

### 4. Open the display on another device

Connect the iPad, tablet, laptop, or phone to the same local network as the vatSys PC. In its web browser, enter:

```text
http://<your-pc-ip>:5300/
```

For example, if the PC's IPv4 address is `192.168.1.42`, open:

```text
http://192.168.1.42:5300/
```

Keep vatSys running while using the display. From the landing page, choose **Ground Window** or **Air Window**, then pick a position from the **Position** menu. The position's maps load, tracks update live, and other maps can be toggled from the **Maps** menu.

> The address can change when the PC reconnects to the network or the router restarts. If a previously saved address stops working, check `ipconfig` again.

### Connection Troubleshooting

- If `http://localhost:5300/` does not open on the vatSys PC, confirm vatSys is running, the correct profile is loaded, and the DLL is in that profile's plugin folder.
- If it works on the PC but not on another device, confirm both devices are on the same network and that the URL begins with `http://`, not `https://`.
- If Windows Firewall prompts for access, allow vatSys on **Private networks**. Public network access is not normally required.
- Guest Wi-Fi and some managed networks block communication between devices. Connect both devices to a normal private Wi-Fi or Ethernet network.
- Temporarily disconnecting a VPN can help if it is routing local traffic through a different adapter.
- Port `5300` must be available. Another application using that port can prevent the plugin server from starting.

### Controls

Mouse buttons behave as they do in vatSys:

| Action | Effect |
|---|---|
| Right-click a track, move, right-click | Move that track's label (in this window only) |
| Right-click the map, move, right-click | Zoom to the box drawn; middle-click cancels |
| Middle-button drag | Quick look-around; the view returns when the button is released |
| Right-click or middle-click the scale marker or ATC list | Pick it up; right-click to put it down |
| Tools → Bearing Range Line | Draws a BRL from the track selected in vatSys; click to anchor its end, click its box to pick it up again, middle-click to remove it |

The page adds these, which vatSys does not have:

| Action | Effect |
|---|---|
| Drag (left button or one finger) | Pan |
| Scroll wheel / pinch | Zoom in vatSys's 10% range steps, about the cursor / between the fingers |
| Double-click / double-tap | Return to the position's default centre and range |
| Tap a track, then drag or tap | Move that track's label |

The page reopens on the position it last showed.

## Project Structure

```text
vatSysExternalWindows/
├── build-release.ps1          # Release rebuild and versioned ZIP packaging
├── README.md
└── VatSysExternalWindows/
    ├── ExternalWindowsPlugin.cs   # IPlugin entry point; starts/stops the server
    ├── EmbeddedWebServer.cs       # TCP HTTP server and JSON API routing
    ├── WebAssets.cs               # The embedded page files
    ├── Web/
    │   ├── display.html           # The window: frame, title bar, menu bar, canvas
    │   ├── display.css            # BaseForm / MenuStrip look
    │   ├── display.js             # Port of ASDControlDX rendering (both window types)
    │   └── display-ui.js          # Menus, pointer handling, data polling
    ├── MapSource.cs               # Maps, positions and runtime maps from vatSys's loaded state
    ├── TrackBuilder.cs            # Ports of PaintGroundTracks / PaintASDTrack / PaintASDLabel
    ├── TrackLabelBuilder.cs       # Port of PaintLabel: labels as character cells
    ├── TrackData.cs               # Track DTOs and font measurement
    ├── TrackSymbolData.cs         # Tracks.xml / DefaultTracks.xml symbol geometry
    ├── DisplayState.cs            # Colour table, display settings, tool graphics
    ├── VatSysInternals.cs         # Reflection access to non-public vatSys state
    ├── WindowDataService.cs       # Symbol cache; Crossing/Release map state
    ├── FontRepair.cs              # Makes the Terminus TTF loadable as a web font
    ├── ProfileLocator.cs          # Resolves the currently loaded profile directory
    ├── LandingPage.cs             # Ground/Air window chooser
    └── VatSysExternalWindows.csproj
```
