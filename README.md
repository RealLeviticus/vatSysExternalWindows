# vatSys External Ground Windows

A vatSys plugin that serves a real-time A-SMGCS (Advanced Surface Movement Guidance and Control System) ground map to any browser on your local network — designed for iPad use alongside vatSys on your PC.

## Features

- **Pixel-accurate ground map rendering** — matches the native vatSys Ground window: same colours, symbols, fonts, labels, and map layers from the loaded profile
- **Live aircraft tracking** — departure, arrival, local, and unknown tracks with category-based colouring, wake-category-sized diamond symbols, and position history trail dots
- **Airborne symbol transition** — aircraft change from ground diamonds to ModeS circles when they leave the ground
- **Full profile support** — reads Positions.xml, Maps/, Colours.xml, and Tracks.xml definitions from whichever profile vatSys has loaded
- **On-demand map loading** — layers load individually when toggled, keeping the initial page load fast
- **Touch-optimised UI** — pan, pinch-zoom, scroll-zoom, double-tap reset; inline accordion menus for touch-friendly map/position selection
- **Self-contained** — single plugin DLL, no external dependencies beyond vatSys itself

## How It Works

The plugin starts a lightweight HTTP server on **port 5300** inside the vatSys process. It serves an HTML5 Canvas web application that:

1. Fetches ASMGCS positions and map metadata from the profile via `/api/airports` and `/api/groundmaps`
2. Loads map layer geometry on demand via `/api/maplayers`
3. Polls `/api/aircraft` every 2 seconds for live radar tracks near the selected airport
4. Renders everything client-side on a `<canvas>` element using the exact vatSys rendering rules

### API Endpoints

| Endpoint | Description |
|---|---|
| `GET /` | Landing page for choosing a window type |
| `GET /ground`, `GET /asd` | Ground / Air window web applications |
| `GET /api/airports` | All ASMGCS positions with name, airport, group, center, range, rotation, magnetic variation, and map references |
| `GET /api/groundmaps` | All Ground-category map metadata + colour scheme (for the Maps dropdown) |
| `GET /api/symbols` | Track symbol geometry parsed from the profile's Tracks.xml (DefaultTracks.xml backfill) + current label/map font sizes |
| `GET /api/maplayers?maps=Folder/Name,...` | Geometry (infills, lines, labels) for specific map layers |
| `GET /api/map?icao=XXXX` | Full map data for an airport (legacy endpoint) |
| `GET /api/aircraft?icao=XXXX` | Live aircraft near an airport with callsign, position, heading, groundspeed, radar flags, squawk, wake category, type, runway, on-ground state, category, and position history |
| `GET /api/visiblemaps` | Crossing/Release map layers currently visible in the vatSys Ground window |
| `GET /api/asd/positions`, `/api/asd/maps`, `/api/asd/maplayers`, `/api/asd/aircraft` | Air window equivalents |

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
| Map labels | `MMI.ASDMapFont` size (default 16px), drawn in the map layer's own colour |
| Leader line | Direction × 22.5°, length × 35px + 5 (matches `Track.LABEL_LEN_UNIT` and `CalculateLabelLocation`) |
| Track colours | Ground: Departure / Arrival / Local / Unknown; Air: HMI state via `MMI.FindTrack` → `Track.State` (Preactive renders NonJurisdiction, as vatSys does) |
| Map colours | Port of `SelectMapBrush`: Ground_RWY/TWY/APR/BLD/INF + custom colours; ASD System/System2/Flex/Filled/UserMap/PRDArea. Infill `Type="Background"` paints the window background colour |
| Layer priority | Descending sort — higher priority drawn first (behind lower priority) |
| Coordinate rotation | Map: `position.Rotation + MagneticVariation`; ground symbols additionally rotate to `NetworkPilot.Heading` (airborne symbols are screen-fixed) |

## Build Requirements

- .NET Framework 4.8.1 build tools (x86 target)
- vatSys installed locally (for the `vatSys.exe` assembly reference)

## Build

From the workspace root:

```powershell
dotnet build .\VatSysExternalWindows\VatSysExternalWindows.csproj -p:VatSysDir="C:\Program Files (x86)\vatSys"
```

Override output directory:

```powershell
dotnet build .\VatSysExternalWindows\VatSysExternalWindows.csproj ^
  -p:VatSysDir="C:\Program Files (x86)\vatSys" ^
  -p:VatSysProfilePluginsDir="C:\Path\To\Profiles\Australia\Plugins\VatSysExternalWindows\"
```

Default output: `%UserProfile%\Documents\vatSys Files\Profiles\Australia\Plugins\VatSysExternalWindows\`

## Usage

1. Build the plugin and ensure the DLL is in your profile's `Plugins\VatSysExternalWindows\` folder
2. Start vatSys — the plugin loads automatically and starts the web server on port 5300
3. On your iPad (or any device on the same network), open `http://<your-pc-ip>:5300/`
4. Select a position from the **Position** dropdown to center the map and load the default layers
5. Toggle map layers on/off from the **Maps** dropdown
6. Aircraft appear automatically when a position is selected

### Touch Controls

| Gesture | Action |
|---|---|
| One-finger drag | Pan the map |
| Pinch | Zoom in/out (centered on pinch midpoint) |
| Scroll wheel | Zoom in/out (centered on cursor) |
| Double-tap / double-click | Reset pan and zoom to the position's defaults |

## Project Structure

```
VatSysExternalWindows/
├── ExternalWindowsPlugin.cs   # IPlugin entry point, starts/stops the web server
├── EmbeddedWebServer.cs       # TCP HTTP server with JSON API routing
├── ProfileLocator.cs          # Resolves the loaded profile directory (DatasetPath)
├── GroundDataService.cs       # Reads vatSys runtime data (positions, tracks, aircraft)
├── GroundMapData.cs           # Parses profile XML files (maps, colours, coordinates)
├── AsdDataService.cs          # ASD (air window) positions, maps, colours, aircraft
├── LandingPage.cs             # Landing page for choosing a window type
├── WebUiPage.cs               # Ground window HTML/CSS/JS app as a C# string constant
├── AsdUiPage.cs               # Air window HTML/CSS/JS app as a C# string constant
└── VatSysExternalWindows.csproj
```
