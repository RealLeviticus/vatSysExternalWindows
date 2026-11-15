# vatSys External Windows

A vatSys plugin that serves browser-based Ground (A-SMGCS) and Air (ASD) displays to devices on your local network. It is designed for an iPad or other secondary display used alongside vatSys on your PC.

## Features

- **Ground and Air displays** — choose an A-SMGCS Ground window or an ASD Air window from the browser landing page
- **Profile-driven rendering** — uses the loaded profile's colours, symbols, fonts, labels, positions, map layers, ranges, and rotations to closely reproduce the corresponding vatSys window
- **Live aircraft tracking** — refreshes tracks twice per second with ground/air symbol selection, category or jurisdiction colouring, velocity vectors, and history dots where applicable
- **Runtime map synchronisation** — Ground crossing/release maps follow vatSys visibility; ASD sector infill and active restricted areas refresh automatically
- **Full profile support** — reads `Positions.xml`, `Colours.xml`, `Tracks.xml`, and related settings from whichever profile vatSys has loaded, and takes map geometry straight from the maps vatSys itself loaded (airways, navaid and bearing/range points, labels, symbols and runways resolve exactly as they do in vatSys)
- **On-demand map loading** — layers load individually when toggled, keeping the initial page load fast
- **Touch-optimised UI** — pan, pinch-zoom, scroll-zoom, double-tap reset, movable labels, and touch-friendly map/position menus
- **Self-contained** — single plugin DLL, no external dependencies beyond vatSys itself

## How It Works

The plugin starts a lightweight HTTP server on **port 5300** inside the vatSys process. It listens on the PC's network interfaces so other devices on the same local network can connect. It serves HTML5 Canvas applications that:

1. Fetch Ground or ASD positions and map metadata from the currently loaded profile
2. Load selected map-layer geometry on demand
3. Poll live radar tracks every 500 milliseconds
4. Refresh runtime ASD sector/restricted-area maps every 5 seconds
5. Render the display client-side on a `<canvas>` using profile data and vatSys-style rendering rules

The server has no login or encryption and is intended only for a trusted local network. Do not expose port `5300` directly to the internet. It answers `GET`/`HEAD` only, sends no CORS headers (the pages are same-origin), drops requests that do not arrive within 10 seconds, and caps concurrent connections.

### API Endpoints

| Endpoint | Description |
|---|---|
| `GET /` | Landing page for choosing a window type |
| `GET /ground`, `GET /asd` | Ground / Air window web applications |
| `GET /api/airports` | All ASMGCS positions with name, airport, group, center, range, rotation, magnetic variation, and map references |
| `GET /api/groundmaps` | All Ground-category map metadata + colour scheme (for the Maps dropdown) |
| `GET /api/symbols` | Track symbol geometry parsed from the profile's Tracks.xml (DefaultTracks.xml backfill) + current label/map font sizes |
| `GET /api/maplayers?maps=Folder/Name,...` | Geometry (infills, lines, labels, symbols, runways) for specific map layers |
| `GET /api/aircraft?icao=XXXX&range=NM` | Live aircraft within `range` NM of the airport's position centre (a small default box when omitted) with callsign, position, heading, groundspeed, radar flags, squawk, wake category, type, runway, on-ground state, category, and position history |
| `GET /api/visiblemaps` | Crossing/Release map layers currently visible in the vatSys Ground window |
| `GET /api/asd/positions` | ASD positions from the loaded profile |
| `GET /api/asd/maps` | ASD map metadata, colours, velocity-vector setting, and transition altitude |
| `GET /api/asd/dynamicmaps` | Runtime controlled-sector infill and active restricted areas |
| `GET /api/asd/maplayers?maps=Folder/Name,...` | Geometry for selected ASD map layers |
| `GET /api/asd/aircraft?lat=...&lon=...&range=...` | Live aircraft within the requested ASD viewport |

### Rendering Details

| Element | Specification |
|---|---|
| Track symbols | Exact geometry from the profile's Tracks.xml (paths/ellipses/rects, per-squawk SpecialCode variants), served via `/api/symbols`. StrokeWidth 0 → filled, otherwise stroked outline — same as vatSys geometry realisations |
| Ground symbol selection | Port of `PaintGroundTracks`: coupled or SSR/ADSB ground tracks get the wake-category symbol (L/M/H/J → GroundLight/Medium/Heavy/Super, else Ground); airborne tracks get Primary (PSR/SMR) and/or ModeS (any SSR source); primary-only ground returns get the Ground symbol with no label |
| Air symbol selection | Port of `PaintASDTrack`: ModeS → ModeA → ADSB by radar flags, Coast crosshair/cross when coasting, Primary cross overlay on PSR returns, ident blink in IdentFlash colour |
| Velocity vectors | Exact algorithm: segments of `min(5, VV)` minutes along track heading with VelocityMarker symbols at intermediate points |
| History dots | HistoryMarker symbol at past positions, capped at `MMI.HistoryDots` |
| Aircraft labels | `MMI.ASDMainFont` size (default 18px bold Terminus), Labels.xml formats: GroundAir / GroundDeparture / GroundArrival / GroundLimited (ground), Normal / Limited (air) |
| Label item formats | Exact `PaintLabel` ports — ACID: FDR callsign, or squawk (4-digit octal) for SSR-only uncoupled tracks (ADS-B broadcasts callsign); LEVEL: alt/100 as D3; RWY: departure runway until ATD set, then arrival runway; vertical indicator: ® / ¬ / > Terminus glyphs at ±300fpm, hidden while coasting; CFL: D3 with VSA sentinel; GS: round(gs/10) 2-padded, ++ outside 0–995; Limited level: FLnnn above transition altitude, 5-digit feet below |
| Map geometry | Served from `DisplayMaps.Maps` — the maps vatSys parsed itself — so airway lines (`FIX.AIRWAY.FIX`), navaid-suffixed and bearing/range points, nearest-duplicate fix resolution and great-circle segments match vatSys. The map lists are empty until vatSys has finished loading the profile; the pages retry |
| Map labels | Port of `PaintMap`: `MMI.ASDMapFont` size (default 16px) in the map layer's own colour; leader line and offset per `LabelOrientation`, `Alignment` / `VerticalAlignment` / `Border` honoured, labels that would overlap an earlier label of the same map are skipped |
| Map symbols | All 14 `Map.Symbol.Types`, same geometry as the vatSys symbol bitmaps (SolidSquare when no type is given) |
| Runways | 2.5px line between thresholds, 1px extended centrelines by bearing/range from the threshold, 12px tick marks |
| Leader line | Direction × 22.5°, length × 35px + 5 (matches `Track.LABEL_LEN_UNIT` and `CalculateLabelLocation`) |
| Track colours | Ground: Departure / Arrival / Local / Unknown; Air: HMI state via `MMI.FindTrack` → `Track.State` (Preactive renders NonJurisdiction, as vatSys does) |
| Map colours | Port of `SelectMapBrush`: Ground_RWY/TWY/APR/BLD/INF + custom colours; ASD System/System2/Flex/Filled/UserMap/PRDArea. Infill `Type="Background"` paints the window background colour |
| Layer priority | Descending sort — higher priority drawn first (behind lower priority) |
| Coordinate rotation | Map: `position.Rotation + MagneticVariation`; ground symbols additionally rotate to `NetworkPilot.Heading` (airborne symbols are screen-fixed) |

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
    ├── GroundDataService.cs       # Ground positions, tracks, symbols, and runtime data
    ├── GroundMapData.cs           # Ground position, colour, and coordinate parsing
    ├── AsdDataService.cs          # ASD positions, colours, tracks, and dynamic layers
    ├── TrackSymbolData.cs         # Tracks.xml/default symbol geometry parsing
    ├── LandingPage.cs             # Ground/Air display chooser
    ├── WebUiPage.cs               # Embedded Ground display HTML/CSS/JavaScript
    ├── AsdUiPage.cs               # Embedded ASD display HTML/CSS/JavaScript
    └── VatSysExternalWindows.csproj
```
