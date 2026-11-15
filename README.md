# vatSys External Windows

A vatSys plugin that serves browser-based Ground (A-SMGCS) and Air (ASD) displays to devices on your local network. It is designed for an iPad or other secondary display used alongside vatSys on your PC.

## Features

- **Ground and Air displays** — choose an A-SMGCS Ground window or an ASD Air window from the browser landing page
- **Profile-driven rendering** — uses the loaded profile's colours, symbols, fonts, labels, positions, map layers, ranges, and rotations to closely reproduce the corresponding vatSys window
- **Live aircraft tracking** — refreshes tracks twice per second; the plugin decides each track's symbols, colour and label inside vatSys, from vatSys's own state, the way vatSys paints them
- **Runtime map synchronisation** — Ground crossing/release maps follow vatSys visibility; the controlled-sector infill (when vatSys's Dynamic Infill setting is on) and active restricted areas refresh automatically, painted in vatSys's order
- **Full profile support** — builds every label from the profile's `Labels.xml` as vatSys parsed it (any label layout, including items other plugins supply), reads `Positions.xml`, `Colours.xml`, `Tracks.xml`, and related settings from whichever profile vatSys has loaded, and takes map geometry straight from the maps vatSys itself loaded (airways, navaid and bearing/range points, labels, symbols and runways resolve exactly as they do in vatSys)
- **On-demand map loading** — layers load individually when toggled, keeping the initial page load fast
- **Touch-optimised UI** — pan, pinch-zoom, scroll-zoom, double-tap reset, movable labels, and touch-friendly map/position menus
- **Self-contained** — single plugin DLL, no external dependencies beyond vatSys itself

## How It Works

The plugin starts a lightweight HTTP server on **port 5300** inside the vatSys process. It listens on the PC's network interfaces so other devices on the same local network can connect. It serves HTML5 Canvas applications that:

1. Fetch Ground or ASD positions and map metadata from the currently loaded profile
2. Load selected map-layer geometry on demand
3. Poll live radar tracks every 500 milliseconds
4. Refresh the runtime sector-infill/restricted-area maps every 2 seconds
5. Render the display client-side on a `<canvas>` using profile data and vatSys-style rendering rules

The server has no login or encryption and is intended only for a trusted local network. Do not expose port `5300` directly to the internet. It answers `GET`/`HEAD` only, sends no CORS headers (the pages are same-origin), drops requests that do not arrive within 10 seconds, and caps concurrent connections.

### API Endpoints

| Endpoint | Description |
|---|---|
| `GET /` | Landing page for choosing a window type |
| `GET /ground`, `GET /asd` | Ground / Air window web applications |
| `GET /api/airports` | All ASMGCS positions with name, airport, group, center, range, rotation, magnetic variation, and map references |
| `GET /api/groundmaps` | All Ground-category map metadata + colour scheme (for the Maps dropdown) |
| `GET /api/symbols` | Track symbol geometry parsed from the profile's Tracks.xml (DefaultTracks.xml backfill) + the label/map font sizes and character cell sizes vatSys lays text out with |
| `GET /api/maplayers?maps=Folder/Name,...` | Geometry (infills, lines, labels, symbols, runways) for specific map layers |
| `GET /api/aircraft?icao=XXXX&lat=...&lon=...&range=NM` | Tracks within `range` NM of the client's screen centre (a small box around the airport's position centre when omitted), each ready to paint: position, colour, symbols, history, and the label as rows of character cells |
| `GET /api/visiblemaps` | Crossing/Release map layers currently visible in the vatSys Ground window |
| `GET /api/asd/positions` | ASD positions from the loaded profile |
| `GET /api/asd/maps` | ASD map metadata and colours |
| `GET /api/asd/dynamicmaps` | Runtime maps in vatSys paint order: `Below` the file maps (controlled-sector infill, restricted-area infills) and `Above` them (restricted-area outlines, also drawn on the Ground page) |
| `GET /api/asd/maplayers?maps=Folder/Name,...` | Geometry for selected ASD map layers |
| `GET /api/asd/aircraft?lat=...&lon=...&range=...` | Tracks within the requested ASD viewport, in the same ready-to-paint form (plus HMI state and velocity vector) |

### Rendering Details

| Element | Specification |
|---|---|
| Track symbols | Exact geometry from the profile's Tracks.xml (paths/ellipses/rects, per-squawk SpecialCode variants), served via `/api/symbols`. StrokeWidth 0 → filled, otherwise stroked outline — same as vatSys geometry realisations |
| Ground symbol selection | Port of `PaintGroundTracks`, decided in the plugin: coupled or SSR/ADSB ground tracks get the wake-category symbol (L/M/H/J → GroundLight/Medium/Heavy/Super, else Ground; the category is withheld for Mode C-only returns and transponder-off primary returns, and falls back to the pilot's type, then engine type); airborne tracks get Primary (PSR/SMR) and/or ModeS (any SSR source); primary-only ground returns get the Ground symbol with no label; the track selected in vatSys gets the Selection symbol |
| Air symbol selection | Port of `PaintASDTrack`, decided in the plugin: ModeS → ModeA → ADSB by radar flags, Cancelled symbol for cancelled tracks, Coast symbols when coasting, Primary overlay on PSR returns, ident blink in the IdentFlash colour, Selection on the track selected in vatSys. Tracks are painted grouped by HMI state, each group's symbols before its labels |
| Projection | Port of `ConvertLLToScreen`: stereographic about the screen centre, zoomed so the range spans the window width (`SetZoom` / `CalculateZoom`); panning and zooming move the screen centre. Range limits as vatSys: 10–9999 NM (Air), 0.1–30 NM (Ground) |
| Velocity vectors | Port of `PaintASDTrack`: 1-minute segments (5-minute once the setting is above 5), each a great-circle step along the track heading, with VelocityMarker symbols at intermediate points |
| Track visibility | Air: tracks whose display track is not `IsVisibleASD` are omitted; Ground: tracks outside the window are not drawn |
| History dots | HistoryMarker symbol at past positions, capped at `MMI.HistoryDots` |
| Aircraft labels | Port of `PaintLabel`. The plugin walks the label definition vatSys parsed from `Labels.xml` and sends each label as rows of fixed-width character cells with text, colour, background and border; the page places them (`GetItemRectF`, including west-side and justified layouts) on vatSys's character cell (`MainFontWidth` × `MainFontHeight`). All built-in item types are ported, plus items supplied by other plugins. Empty fixed-width items still take their space, hidden/toggled lines, shared (timeshare) lines and the leader anchor row follow vatSys, and the alert box is drawn around the label |
| Label type and visibility | Ground: GroundDeparture / GroundArrival / GroundAir / GroundLimited as `PaintGroundTracks` chooses. Air (`PaintASDLabel`): Normal, Extended, Quicktag or Limited; non-jurisdiction traffic is labelled only when airborne, inside the level filters and not matched by an SSR code filter, unless quick-look or an alert applies |
| Label colours | Track colour, with non-jurisdiction labels in the NonJurisdictionLabel colour, per-item colours from `Labels.xml` (e.g. a new CFL highlighted), alert-priority colours, quick-look colours and plugin track colours. Handover-in and post-jurisdiction labels blink on vatSys's 600 ms flash cycle |
| Label position | The direction and leader length set on the track in vatSys; a label moved on the page keeps the page's position until reload |
| Map geometry | Served from `DisplayMaps.Maps` — the maps vatSys parsed itself — so airway lines (`FIX.AIRWAY.FIX`), navaid-suffixed and bearing/range points, nearest-duplicate fix resolution and great-circle segments match vatSys. The map lists are empty until vatSys has finished loading the profile; the pages retry |
| Map labels | Port of `PaintMap`: `MMI.ASDMapFont` size (default 16px) in the map layer's own colour; leader line and offset per `LabelOrientation`, `Alignment` / `VerticalAlignment` / `Border` honoured, labels that would overlap an earlier label of the same map are skipped |
| Map symbols | All 14 `Map.Symbol.Types`, same geometry as the vatSys symbol bitmaps (SolidSquare when no type is given) |
| Runways | 2.5px line between thresholds, 1px extended centrelines by bearing/range from the threshold, 12px tick marks |
| Leader line | Direction × 22.5°, length × 35px + 5 (matches `Track.LABEL_LEN_UNIT` and `CalculateLabelLocation`) |
| Track colours | Ports of `SelectGroundBrush` and `SelectASDBrush`: a plugin's custom track colour first; Ground: Departure / Arrival / Local / Unknown; Air: by HMI state, with the quick-look colours (Preactive renders NonJurisdiction, as vatSys does) |
| Map colours | Port of `SelectMapBrush`: Ground_RWY/TWY/APR/BLD/INF + custom colours; ASD System/System2/Flex/Filled/UserMap/PRDArea. Infill `Type="Background"` paints the window background colour |
| Layer priority | Descending sort — higher priority drawn first (behind lower priority) |
| Coordinate rotation | Map: `position.Rotation + MagneticVariation`; rotate-to-track symbols turn by heading + stereographic north correction + map rotation, as `PaintTrackGeometryRealisation` does (ground symbols use `NetworkPilot.Heading`; other symbols are screen-fixed) |

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

Keep vatSys running while using the display. From the landing page, choose **Ground Display** or **Air Display**, then select a position. The default map layers load automatically, aircraft update live, and additional layers can be toggled from the **Maps** menu.

The **Tools** menu mirrors the vatSys layout, but its listed operational tools are currently placeholders and are not interactive.

> The address can change when the PC reconnects to the network or the router restarts. If a previously saved address stops working, check `ipconfig` again.

### Connection Troubleshooting

- If `http://localhost:5300/` does not open on the vatSys PC, confirm vatSys is running, the correct profile is loaded, and the DLL is in that profile's plugin folder.
- If it works on the PC but not on another device, confirm both devices are on the same network and that the URL begins with `http://`, not `https://`.
- If Windows Firewall prompts for access, allow vatSys on **Private networks**. Public network access is not normally required.
- Guest Wi-Fi and some managed networks block communication between devices. Connect both devices to a normal private Wi-Fi or Ethernet network.
- Temporarily disconnecting a VPN can help if it is routing local traffic through a different adapter.
- Port `5300` must be available. Another application using that port can prevent the plugin server from starting.

### Touch Controls

| Gesture | Action |
|---|---|
| One-finger drag | Pan the map |
| Pinch | Zoom in/out (centered on pinch midpoint) |
| Scroll wheel | Zoom in/out (centered on cursor) |
| Double-tap / double-click | Reset pan and zoom to the position's defaults |
| Tap/click an aircraft symbol, then tap/click a new location | Reposition that aircraft's label in the browser display |

## Project Structure

```text
vatSysExternalWindows/
├── build-release.ps1          # Release rebuild and versioned ZIP packaging
├── README.md
└── VatSysExternalWindows/
    ├── ExternalWindowsPlugin.cs   # IPlugin entry point; starts/stops the server
    ├── EmbeddedWebServer.cs       # TCP HTTP server and JSON API routing
    ├── ProfileLocator.cs          # Resolves the currently loaded profile directory
    ├── MapSource.cs               # Map metadata/geometry from vatSys's loaded maps (shared by both windows)
    ├── TrackData.cs               # Ready-to-paint track data, colours, history, font cell sizes
    ├── TrackLabelBuilder.cs       # Port of vatSys PaintLabel: labels as character cells
    ├── VatSysInternals.cs         # Reflection access to non-public vatSys state (labels, alerts, plugin hooks)
    ├── GroundDataService.cs       # Ground positions, tracks, symbols, and runtime data
    ├── GroundMapData.cs           # Ground position, colour, and coordinate parsing
    ├── AsdDataService.cs          # ASD positions, colours, tracks, and dynamic layers
    ├── TrackSymbolData.cs         # Tracks.xml/default symbol geometry parsing
    ├── LandingPage.cs             # Ground/Air display chooser
    ├── WebUiPage.cs               # Embedded Ground display HTML/CSS/JavaScript
    ├── AsdUiPage.cs               # Embedded ASD display HTML/CSS/JavaScript
    └── VatSysExternalWindows.csproj
```
