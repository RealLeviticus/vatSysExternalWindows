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
| `GET /` | Serves the full HTML/CSS/JS web application |
| `GET /api/airports` | All ASMGCS positions with name, airport, group, center, range, rotation, magnetic variation, and map references |
| `GET /api/groundmaps` | All Ground-category map metadata + colour scheme (for the Maps dropdown) |
| `GET /api/maplayers?maps=Folder/Name,...` | Geometry (infills, lines, labels) for specific map layers |
| `GET /api/map?icao=XXXX` | Full map data for an airport (legacy endpoint) |
| `GET /api/aircraft?icao=XXXX` | Live aircraft near an airport with callsign, position, heading, groundspeed, wake category, type, runway, on-ground state, category, and position history |

### Rendering Details

| Element | Specification |
|---|---|
| Ground symbols | Wake-category diamonds from Tracks.xml — L/M/H/J sizes, all filled, Ground default also stroked (1px) |
| Airborne symbol | ModeS circle, 6.5px radius, stroked |
| History dots | 2px radius filled circles at past positions (up to 10) |
| Aircraft labels | 18px bold Terminus (`eurofont_sml` / `ASDMainFont`) |
| Map labels | 16px bold Terminus (`eurofont_xsml` / `ASDMapFont`) |
| Menu text | 16px bold Terminus (`eurofont_winsml`) |
| Leader line | Direction=1 (22.5°), Length=1 (40px), screen-fixed |
| Track colours | Departure / Arrival / Local / Unknown from Colours.xml |
| Map colours | GroundBackground / Runway / Taxiway / Apron / Building / Other from Colours.xml |
| Layer priority | Descending sort — higher priority drawn first (behind lower priority) |
| Coordinate rotation | `heading + position.Rotation + position.MagneticVariation` (clockwise from north) |

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
├── GroundDataService.cs       # Reads vatSys runtime data (positions, tracks, aircraft)
├── GroundMapData.cs           # Parses profile XML files (maps, colours, coordinates)
├── WebUiPage.cs               # Complete HTML/CSS/JS web application as a C# string constant
└── VatSysExternalWindows.csproj
```
- High FPS and high JPEG quality increase CPU and network usage.
