# vatSys External Windows

A vatSys plugin that serves browser-based Ground (A-SMGCS) and Air (ASD) displays, and the OzStrips strip board, to devices on your local network. It is designed for an iPad or other secondary display used alongside vatSys on your PC.

## Features

- **Ground and Air windows** — the vatSys A-SMGCS Ground window and a secondary ASD window, reproduced in the browser: the same window frame, menus, maps, tracks, labels and tool graphics
- **One renderer, ported from vatSys** — vatSys draws both window types with one control (`ASDControlDX`); the page is a port of that control's paint code (projection, map painting, track symbols, label layout, tools), not an approximation of how it looks
- **Decisions made inside vatSys** — the plugin decides each track's symbols, colour and label from vatSys's own state, exactly as vatSys's paint code does, and sends the result ready to draw
- **vatSys's live state, not the profile files** — positions, maps, colours (with your brightness settings applied), fonts, label definitions, display settings and runtime maps are read from the running vatSys, so the page follows whatever vatSys is showing
- **Tracks and labels you can act on** — each mouse button does on a track or label item what it does in vatSys: select a track, open the aircraft menu, set the cleared level, runway, label data or a handoff, acknowledge an alert, toggle label lines. The choice is carried out by vatSys itself, as if made in its own window
- **OzStrips in the browser** — when the OzStrips plugin is loaded, `/ozstrips` shows the strip board and lets you work the strips. Its window does not have to be open on the PC: the page starts OzStrips minimised
- **On-demand map loading** — layers load individually when toggled, keeping the initial page load fast
- **Works on touch devices** — tap for a left click, hold for what the other mouse buttons do, drag to pan, pinch to zoom
- **Self-contained** — a single plugin DLL (the page is embedded in it), no dependencies beyond vatSys itself

## How It Works

The plugin starts a lightweight HTTP server on **port 5300** inside the vatSys process. It listens on the PC's network interfaces so other devices on the same local network can connect. It serves one HTML5 Canvas page for both window types (`/ground` and `/asd`), which:

1. Loads the window's positions, its Maps menu and the track symbol geometry
2. Loads map-layer geometry on demand
3. Polls live tracks every 500 milliseconds
4. Polls colours, fonts, display settings, runtime maps and tool graphics every 2 seconds
5. Paints the window on a `<canvas>` in vatSys's own coordinate frame, in vatSys's paint order

The server has no login or encryption and is intended only for a trusted local network. Do not expose port `5300` directly to the internet. **Anyone who can reach the port can act on flights and strips as you**: the pages are no longer read-only. Reading is `GET`/`HEAD`; the two requests that change something (`/api/action` and `/api/ozstrips/command`) are `POST`s of JSON, accepted only from the plugin's own pages (the `Origin` must be the server's own address, and no CORS headers are sent, so another website open in the same browser cannot issue them). The server drops requests that do not arrive within 10 seconds and caps concurrent connections.

### API Endpoints

`type` is `ground` or `asd` (the default).

| Endpoint | Description |
|---|---|
| `GET /` | Landing page for choosing a window type |
| `GET /ground`, `GET /asd` | The window page (the same page; it takes its type from the path) |
| `GET /ozstrips` | The OzStrips strip board page |
| `GET /api/static?type=` | The window's positions (from `LogicalPositions.Positions`: name, group, airport, default centre and range, rotation, magnetic variation, maps), the maps for its Maps menu, and the track symbol geometry from Tracks.xml (DefaultTracks.xml backfill) |
| `GET /api/state?type=` | What changes while vatSys runs: the colour table as the renderer and the window chrome see it, font sizes and character cells, display settings, the runtime maps in paint order (`Below` / `Above` the file maps), the tool graphics (ATC list, visibility centres, conflict areas, closest approach, graphic routes) and, for Ground, the Crossing/Release maps the vatSys Ground window shows |
| `GET /api/maplayers?maps=Folder/Name,...` | Geometry (infills, lines, labels, symbols, runways) and paint order for specific maps |
| `GET /api/aircraft?type=&lat=&lon=&range=&icao=` | Every track the window paints, in paint order and ready to draw: position, colour, symbols, history, velocity vector, short route probe, and the label as rows of character cells, each cell with the clickspot vatSys gives that label item (what each mouse button does). `lat`/`lon` is the window's screen centre and `range` how far its view reaches; `icao` is the Ground window's airport |
| `POST /api/action` | A click on a track or label item, or a choice made in a menu or popup. JSON: `Type` (`ground`/`asd`), `Id` (the track), `Action`, `Arg`, `Arg2`, `Ctx`, `Button`, `Toggles`. Answers `Ok` / `Error`, a `Note` when a vatSys window was opened on the PC, and a `Ui` (menu, list popup, handoff window, text field or confirmation) for the page to show next |
| `GET /api/ozstrips/state` | OzStrips' state: aerodrome, ATIS, METAR, connection, the bays with their strips and bars in display order. `Available` is false, with a `Reason`, when OzStrips is not loaded or not running; `CanStart` then says the `start` command can start it |
| `POST /api/ozstrips/command` | One strip-board command. JSON: `Action`, `Callsign`, `Bay`, `Value`, `Style`. `start` starts OzStrips minimised |

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
| Tool graphics | Compass rose, scale marker, ATC list, visibility centres, conflict areas (LATC), closest approach (TOP) and graphic routes, when they are on in vatSys. Bearing range lines can be drawn on the page from the selected track |
| Clickspots | The loop in `ASD_MouseDown`: the track's clickspot (select, quick look, move label) and every label item's, with the click types the profile's Labels.xml gives each mouse button, walked in paint order. Toggles (C prompt, extended label, CFL highlight, label lines, CPDLC message) and alert acknowledgement act at once; another plugin's label item gets the click through its own handler |
| Menus and popups | Drawn on the page in vatSys's window colours, opened where you clicked: the aircraft menu (the items come from vatSys's own `AircraftContextMenu`, configured for the track), Direct, Runway and CPDLC request menus, `CFLMenu` (levels with the position's extra levels and quick level, VISUAL, BLK for a block, CPDLC), `LevelMenu`, `DiversionMenu`, `TimeMenu` (departure and SAR time), `HandoffWindow` (next sector, who is online, None, Other) and the label data field |

### What is not reproduced

- **vatSys's larger windows.** The flight plan window, CPDLC editor, SELCAL window and quick tag window are not drawn on the page: choosing them (aircraft menu → Flightplan, CPDLC, SELCAL, Quicktag / Edit, or a label's CPDLC editor click) opens the real window in vatSys on the PC, and the page says so. Reroute starts vatSys's graphic reroute, which is drawn on the vatSys ASD.
- **Tools menu entries that act on flights.** Quick Tag, the conflict tools (Conflict Area, Closest Approach), Add Text Area and Contact Me / Decouple are shown disabled. While a conflict-tool selection is in progress in vatSys, selecting a track from the page is refused.
- **The supervisor's INF and Kill** menu items are left to vatSys.
- **`Label_Field18_Popup` and `Label_PRW_Popup`** do nothing, as in vatSys, which has no handler for them.
- **Text areas, lat/long points and terrain heights** are not drawn.
- **Edge anti-aliasing of infills on Air-window file maps.** vatSys draws those without anti-aliasing; a browser canvas cannot switch it off for shapes.
- **Map labels wider than 200 px** are cut off at that width; vatSys wraps them inside the same box.
- The small "visual" flight-rules glyphs are not in the Terminus font, so vatSys and the browser each fall back to a font of their own.

### OzStrips

`/ozstrips` shows the strip board of the [OzStrips](https://github.com/maxrumsey/OzStrips) plugin running in the same vatSys. It needs OzStrips installed; when it is not, or cannot be read, the page says so.

You do not need the OzStrips window open. If OzStrips is not running when the page is opened, the page starts it **minimised**: it appears on the Windows taskbar and takes no screen space. It cannot be hidden altogether, because OzStrips keeps everything it knows in its window and stops listening to its server while that window is not visible; a minimised window still counts as visible. Click it on the taskbar, or choose OzStrips in vatSys's menu, to bring the real window up at any time. If you close the OzStrips window while a page is still open somewhere, the page starts it minimised again within a few seconds (its strips reload from the OzStrips server); close the page as well to stop OzStrips.

The page is its own rendering of the board, built for touch, not a copy of the OzStrips window:

- **Shown:** the bays of the current view mode in their columns, strips with their fields, alerts, cock level, PDC and CDM markers, queue bars and bars, the ATIS letter, METAR, connection state and connected clients.
- **Done from the page:** move a strip to a bay, up or down, or to the next bar; cock, ready, SID trigger, take-off timer, flip, crossing, SSR assignment; acknowledge strip alerts; inhibit; edit CLX, stand, remark, departure frequency, CFL, runway, SID and global ops; queue up, queue bars, add and delete bars, crossing and release bars; change aerodrome, view mode and smart resize; the circuit, coordinator and CDM toggles.
- **Opened in vatSys on the PC, not on the page:** the flight plan, PDC, CDM, reroute and private message windows, and OzStrips' settings, key settings, About and message log.
- **Not available:** CDM rate entry, quick search, dropping a strip below another, and a custom `Strip.xml` layout (the page draws OzStrips' default strip).

The plugin does not reference OzStrips: it finds it in the vatSys process and uses it by reflection, so it loads with or without it. Most of what it uses is public, but a few members are not (among them `MainFormController`'s `_bayManager`, `_socketConn`, `_metar` and `_layoutName`, OzStrips' settings class, and for starting it minimised the plugin's `_gui`, `_readyForConnection` and `_aerodromeManager` and vatSys's own plugin list), so an OzStrips update can break the page until the plugin is updated. It was written against the OzStrips build current in November 2026.

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

The landing page provides buttons for the Ground and Air displays and for OzStrips. You can also open them directly at `http://localhost:5300/ground`, `http://localhost:5300/asd` and `http://localhost:5300/ozstrips`.

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

Keep vatSys running while using the display. From the landing page, choose **Ground Window** or **Air Window**, then pick a position from the **Position** menu. The position's maps load, tracks update live, and other maps can be toggled from the **Maps** menu. Choose **OzStrips** for the strip board; it starts OzStrips minimised if it is not already running.

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
| Left-click a track | Select or deselect it (in vatSys too) |
| Middle-click a track (Air window) | Quick look on or off |
| Click a label item | What the profile's Labels.xml gives that item and button: for the Australia profile, left-click the callsign for the aircraft menu, the cleared level for the level menu, the runway, the label data; middle-click an alert to acknowledge it, the callsign for the extended label; right-click the callsign for the SAR time |
| Click away from an open menu or popup, or press Esc | Close it without choosing (a popup also closes from its title bar: the close button or a middle-click) |
| Right-click a track (or a label's level), move, right-click | Move that track's label (in this window only) |
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
| Tap a track or label item | A left click on it. A finger that just misses takes the nearest one |
| Hold a finger on a track or label item | A menu of what the middle and right buttons do there (Quick Look, Move Label, Acknowledge Alert, Extended Label, SAR Time, …) |
| Hold a track, Move Label, then drag or tap | Move that track's label |

Menus and popups opened by a finger are drawn half as large again.

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
    │   ├── display-ui.js          # Menus, pointer handling (clickspots, touch), data polling
    │   ├── display-popups.js      # The menus and popups a click opens; sends the choice back
    │   └── ozstrips.html/.css/.js # The OzStrips strip board page
    ├── MapSource.cs               # Maps, positions and runtime maps from vatSys's loaded state
    ├── TrackBuilder.cs            # Ports of PaintGroundTracks / PaintASDTrack / PaintASDLabel
    ├── TrackLabelBuilder.cs       # Port of PaintLabel: labels as character cells, with their clickspots
    ├── TrackActions.cs            # Port of ASD_MouseDown's click handling and the menus it opens
    ├── OzStripsBridge.cs          # OzStrips state and commands, by reflection into the OzStrips plugin
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
