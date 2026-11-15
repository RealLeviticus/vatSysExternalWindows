using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using vatsys;

namespace VatSysExternalWindows;

/// <summary>
/// Provides ground positions, map geometry, and live aircraft for the web UI.
/// Reads from whichever profile is currently loaded.
/// </summary>
public sealed class GroundDataService
{
    // Half-extent (degrees) of the aircraft filter box when the client doesn't
    // say how far its view reaches.
    private const double DefaultBoxDegrees = 0.15;
    private const double MaxRangeNm = 200;

    private readonly object _lock = new();
    private string _profileDir = "";
    private string _profileName = "";
    private List<GroundMapData.GroundPosition>? _positions;
    private GroundMapData.ColourSet? _colours;
    private List<TrackSymbolData.SymbolDef>? _symbols;

    // Position history: use vatSys RadarTrack.PositionHistory directly
    // Dot count controlled by MMI.HistoryDots user setting (default 5, max 9)

    /// <summary>Returns all ASMGCS positions from the profile.</summary>
    public List<GroundMapData.GroundPosition> GetPositions()
    {
        lock (_lock)
        {
            EnsureProfileDir();
            return _positions ??= GroundMapData.LoadPositions(_profileDir);
        }
    }

    /// <summary>Returns the ground colour scheme.</summary>
    public GroundMapData.ColourSet GetColours()
    {
        lock (_lock)
        {
            EnsureProfileDir();
            return _colours ??= GroundMapData.LoadColours(_profileDir);
        }
    }

    /// <summary>Returns metadata for ALL Ground-category maps vatSys loaded (for the Maps menu).</summary>
    public List<MapMeta> GetGroundMaps()
    {
        return MapSource.ListMaps(DisplayMaps.MapCategories.Ground);
    }

    /// <summary>Returns the profile's Tracks.xml symbol definitions (with DefaultTracks.xml backfill).</summary>
    public List<TrackSymbolData.SymbolDef> GetTrackSymbols()
    {
        lock (_lock)
        {
            EnsureProfileDir();
            return _symbols ??= TrackSymbolData.Load(_profileDir);
        }
    }

    /// <summary>Geometry for specific Ground map layers identified by "Folder/Name" keys.</summary>
    public List<MapLayer> GetMapLayers(List<string> mapKeys)
    {
        return MapSource.GetLayers(DisplayMaps.MapCategories.Ground, mapKeys);
    }

    /// <summary>Returns the tracks for a Ground window showing <paramref name="icao"/>,
    /// each as vatSys's PaintGroundTracks would paint it. vatSys paints every track
    /// inside the window, so the client passes its screen centre and how far its
    /// view reaches; without them a small default box around the airport's
    /// position centre is used.</summary>
    public List<TrackDto> GetAircraft(string icao, double? viewLat = null, double? viewLon = null, double? rangeNm = null)
    {
        var result = new List<TrackDto>();

        double centreLat, centreLon;
        if (viewLat is double vLat && viewLon is double vLon)
        {
            centreLat = vLat;
            centreLon = vLon;
        }
        else
        {
            var pos = GetPositions().FirstOrDefault(p =>
                string.Equals(p.Airport, icao, StringComparison.OrdinalIgnoreCase));
            if (pos is null) return result;
            centreLat = pos.CenterLat;
            centreLon = pos.CenterLon;
        }

        var latDelta = DefaultBoxDegrees;
        var lonDelta = DefaultBoxDegrees;
        if (rangeNm is double range && range > 0)
        {
            range = Math.Min(range, MaxRangeNm);
            latDelta = range / 60.0;
            lonDelta = range / (60.0 * Math.Max(0.01, Math.Cos(centreLat * Math.PI / 180.0)));
        }

        IList<RDP.RadarTrack>? tracks = null;
        try { tracks = RDP.RadarTracks; } catch { }
        if (tracks is null) return result;

        foreach (var radarTrack in tracks)
        {
            try
            {
                var dto = BuildGroundTrack(radarTrack, icao, centreLat, centreLon, latDelta, lonDelta);
                if (dto is not null) result.Add(dto);
            }
            catch { }
        }

        return result;
    }

    /// <summary>Port of ASDControlDX.PaintGroundTracks for one radar track.</summary>
    private static TrackDto? BuildGroundTrack(RDP.RadarTrack radarTrack, string icao,
        double centreLat, double centreLon, double latDelta, double lonDelta)
    {
        // vatSys skips cancelled tracks and tracks without a network pilot
        var pilot = radarTrack.ActualAircraft;
        if (radarTrack.Cancelled || pilot is null) return null;

        var coord = radarTrack.LatLong;
        if (coord is null) return null;
        var lat = coord.Latitude;
        var lon = coord.Longitude;
        if (double.IsNaN(lat) || double.IsNaN(lon)) return null;
        if (Math.Abs(lat - centreLat) > latDelta || Math.Abs(lon - centreLon) > lonDelta) return null;

        Track? tl = null;
        try { tl = MMI.FindTrack(radarTrack); } catch { }

        // ASMGCSCoupledFDR takes precedence over CoupledFDR on the ground display
        var fdr = radarTrack.ASMGCSCoupledFDR ?? radarTrack.CoupledFDR;
        var colour = TrackPainter.GroundColour(tl, fdr, icao);

        var types = radarTrack.RadarTypes;
        var ssrAny = (types & (RDP.RadarTypes.SSR_ModeC | RDP.RadarTypes.SSR_ModeS | RDP.RadarTypes.ADSB | RDP.RadarTypes.SMR_ADSB)) != 0;
        var code = pilot.TransponderCode;
        // Ground symbols turn to the pilot's reported heading (nose direction);
        // airborne symbols use the radar track's heading
        var aircraftHeading = pilot.Heading;

        var dto = new TrackDto
        {
            Id = pilot.Callsign ?? "",
            Lat = Math.Round(lat, 7),
            Lon = Math.Round(lon, 7),
            Col = TrackLabelBuilder.Rgb(colour),
            Clip = true,
            History = TrackPainter.History(radarTrack),
            HistSsr = -1,
        };

        bool hasLabel;
        if (!radarTrack.OnGround)
        {
            if (types.HasFlag(RDP.RadarTypes.PRI) || types.HasFlag(RDP.RadarTypes.SMR))
                dto.Syms.Add(TrackPainter.Symbol("Primary", -1, radarTrack.Heading));
            hasLabel = ssrAny;
            if (ssrAny) dto.Syms.Add(TrackPainter.Symbol("ModeS", code, radarTrack.Heading));
        }
        else if (fdr != null || ssrAny)
        {
            hasLabel = true;

            // Wake category for the symbol: withheld when only Mode C is received or
            // the transponder is off on a primary/SMR return
            var modeCOnly = types.HasFlag(RDP.RadarTypes.SSR_ModeC) &&
                            (types & (RDP.RadarTypes.SSR_ModeS | RDP.RadarTypes.ADSB | RDP.RadarTypes.SMR_ADSB)) == 0;
            var transponderOff = !pilot.TransponderModeC &&
                                 (types & (RDP.RadarTypes.PRI | RDP.RadarTypes.SMR | RDP.RadarTypes.SMR_ADSB)) != 0;
            var wake = "";
            if (fdr != null && !transponderOff)
            {
                wake = fdr.AircraftWake ?? "";
            }
            else if (!modeCOnly && !transponderOff && !string.IsNullOrEmpty(pilot.AircraftType))
            {
                wake = Performance.GetAircraftFromType(pilot.AircraftType)?.WakeCategory ?? "";
            }
            else if (!modeCOnly && !transponderOff)
            {
                switch (VatSysInternals.EngineTypeName(pilot))
                {
                    case "Piston": wake = "L"; break;
                    case "Jet": wake = "M"; break;
                }
            }

            switch (wake)
            {
                case "L": dto.Syms.Add(TrackPainter.Symbol("GroundLight", code, aircraftHeading)); break;
                case "M": dto.Syms.Add(TrackPainter.Symbol("GroundMedium", code, aircraftHeading)); break;
                case "H": dto.Syms.Add(TrackPainter.Symbol("GroundHeavy", code, aircraftHeading)); break;
                case "J": dto.Syms.Add(TrackPainter.Symbol("GroundSuper", code, aircraftHeading)); break;
                default: dto.Syms.Add(TrackPainter.Symbol("Ground", -1, aircraftHeading)); break;
            }
        }
        else
        {
            // Primary-only ground return: default symbol, no label
            hasLabel = false;
            dto.Syms.Add(TrackPainter.Symbol("Ground", -1, aircraftHeading));
        }

        if (tl is null) return dto;

        try
        {
            if (ReferenceEquals(MMI.SelectedGroundTrack, tl))
                dto.Syms.Add(TrackPainter.Symbol("Selection", -1, aircraftHeading));
        }
        catch { }

        if (hasLabel)
        {
            var labelType = "GroundLimited";
            if (fdr != null && !string.IsNullOrEmpty(icao))
            {
                if (radarTrack.OnGround && string.Equals(fdr.DepAirport, icao, StringComparison.OrdinalIgnoreCase))
                    labelType = "GroundDeparture";
                else if (radarTrack.OnGround && string.Equals(fdr.DesAirport, icao, StringComparison.OrdinalIgnoreCase))
                    labelType = "GroundArrival";
                else
                    labelType = "GroundAir";
            }

            try
            {
                dto.Label = TrackLabelBuilder.Build(true, tl, fdr, tl.GetQuickTag(), radarTrack, pilot, labelType, colour);
            }
            catch { }
        }

        return dto;
    }

    private volatile List<string> _visibleMapsSnapshot = new();

    /// <summary>Returns the currently visible crossing/released map layer keys from vatSys Ground windows.</summary>
    public List<string> GetVisibleCrossingReleasedMaps()
    {
        // Application.OpenForms and the ASD control state belong to the GUI thread:
        // compute there and wait briefly, falling back to the last result if the
        // GUI is busy.
        GuiThread.Run(() => _visibleMapsSnapshot = ComputeVisibleCrossingReleasedMaps());
        return _visibleMapsSnapshot;
    }

    private static List<string> ComputeVisibleCrossingReleasedMaps()
    {
        // Prefer the live state of an open vatSys Ground window. When none is open,
        // derive the state from OzStrips' runway bay directly — OzStrips can only
        // push its crossing/release toggles into Ground windows that exist.
        return ComputeFromGroundWindow() ?? ComputeFromOzStrips() ?? new List<string>();
    }

    private static List<string>? ComputeFromGroundWindow()
    {
        try
        {
            // Find ASMGCS Ground windows via Application.OpenForms
            foreach (Form form in Application.OpenForms)
            {
                if (form.GetType().Name != "ASMGCSWindow") continue;

                // Get the ASDControlDX field via reflection (it's a private field)
                var field = form.GetType().GetField("asmgcsControlDX1",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (field is null) continue;

                var asdControl = field.GetValue(form);
                if (asdControl is null) continue;

                // Call public IsMapVisible for each crossing/released map
                var isVisibleMethod = asdControl.GetType().GetMethod("IsMapVisible");
                if (isVisibleMethod is null) continue;

                var result = new List<string>();
                foreach (var map in DisplayMaps.Maps)
                {
                    if (map.Category != DisplayMaps.MapCategories.Ground) continue;
                    if (string.IsNullOrEmpty(map.CustomColourName)) continue;
                    if (!map.CustomColourName.Equals("Release", StringComparison.OrdinalIgnoreCase) &&
                        !map.CustomColourName.Equals("Crossing", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var visible = (bool)(isVisibleMethod.Invoke(asdControl, new object[] { map }) ?? false);
                    if (visible)
                    {
                        var key = string.IsNullOrEmpty(map.Folder) ? map.Name : map.Folder + "/" + map.Name;
                        result.Add(key);
                    }
                }

                return result; // Only need one Ground window
            }
        }
        catch { }
        return null;
    }

    /// <summary>Replicates OzStrips' MapManager.SetApplicableMaps decision by reading its
    /// runway-bay bar strips via reflection (MainFormController.Instance is public static).
    /// Returns null when OzStrips isn't running or its state can't be read.</summary>
    private static List<string>? ComputeFromOzStrips()
    {
        try
        {
            Type? ctrlType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { ctrlType = asm.GetType("MaxRumsey.OzStripsPlugin.GUI.MainFormController"); }
                catch { }
                if (ctrlType is not null) break;
            }
            if (ctrlType is null) return null;

            var controller = ctrlType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (controller is null) return null;

            // OzStrips skips map updates entirely while "Inhibit ground maps" is checked
            var inhibit = GetMember(controller, "InhibitGroundMaps");
            if (inhibit is not null &&
                GetMember(inhibit, "Checked") is bool inhibited && inhibited)
                return null;

            var mainForm = GetMember(controller, "_mainForm");
            var bayManager = GetMember(controller, "_bayManager");
            if (mainForm is null || bayManager is null) return null;

            var icao = GetMember(bayManager, "AerodromeName") as string ?? "";
            if (string.IsNullOrEmpty(icao)) return null;

            // Aerodrome full name = the Maps folder (AutoMapAerodromes ICAO→FullName)
            string? fullName = null;
            var settings = GetMember(GetMember(mainForm, "AerodromeManager"), "Settings");
            if (GetMember(settings, "AutoMapAerodromes") is System.Collections.IEnumerable autoMaps)
            {
                foreach (var am in autoMaps)
                {
                    if (string.Equals(GetMember(am, "ICAOCode") as string, icao, StringComparison.OrdinalIgnoreCase))
                    {
                        fullName = GetMember(am, "FullName") as string;
                        break;
                    }
                }
            }
            if (string.IsNullOrEmpty(fullName)) return null;

            // Bar texts from the first runway bay (OzStrips uses BAY_RUNWAY)
            var barNames = new List<string>();
            var foundBay = false;
            if (GetMember(GetMember(bayManager, "BayRepository"), "Bays") is System.Collections.IEnumerable bays)
            {
                foreach (var bay in bays)
                {
                    var isRunway = false;
                    if (GetMember(bay, "BayTypes") is System.Collections.IEnumerable types)
                    {
                        foreach (var t in types)
                        {
                            if (t?.ToString() == "BAY_RUNWAY") { isRunway = true; break; }
                        }
                    }
                    if (!isRunway) continue;

                    foundBay = true;
                    if (GetMember(bay, "Strips") is System.Collections.IEnumerable strips)
                    {
                        foreach (var s in strips)
                        {
                            if (GetMember(s, "Type")?.ToString() == "BAR")
                                barNames.Add(GetMember(s, "BarText") as string ?? "");
                        }
                    }
                    break;
                }
            }
            if (!foundBay) return null;

            // Same decision as OzStrips MapManager: map "Crossing_16L34R" is visible
            // when a bar "XXX CROSSING RWY 16L/34R…" exists; "Released_16L34R" when
            // a bar "Runway 16L/34R Released…" exists.
            var result = new List<string>();
            foreach (var map in DisplayMaps.Maps)
            {
                if (map.Category != DisplayMaps.MapCategories.Ground) continue;
                if (!string.Equals(map.Folder, fullName, StringComparison.OrdinalIgnoreCase)) continue;

                var crossing = map.Name.StartsWith("Crossing", StringComparison.OrdinalIgnoreCase);
                var released = map.Name.StartsWith("Released", StringComparison.OrdinalIgnoreCase);
                if (!crossing && !released) continue;

                var parts = map.Name.Split('_');
                if (parts.Length < 2 || parts[1].Length % 2 != 0) continue;
                var pair = parts[1];
                var r0 = pair.Substring(0, pair.Length / 2);
                var r1 = pair.Substring(pair.Length / 2);

                var wanted = crossing ? "XXX CROSSING RWY " + r0 + "/" + r1
                                      : "Runway " + r0 + "/" + r1 + " Released";
                var on = barNames.Any(b => b.StartsWith(wanted, StringComparison.Ordinal));
                if (on)
                {
                    var key = string.IsNullOrEmpty(map.Folder) ? map.Name : map.Folder + "/" + map.Name;
                    result.Add(key);
                }
            }
            return result;
        }
        catch { return null; }
    }

    private static object? GetMember(object? obj, string name)
    {
        if (obj is null) return null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var t = obj.GetType();
        var p = t.GetProperty(name, flags);
        if (p is not null) return p.GetValue(obj);
        return t.GetField(name, flags)?.GetValue(obj);
    }

    private void EnsureProfileDir()
    {
        lock (_lock)
        {
            string currentName;
            try { currentName = Profile.Name ?? ""; } catch { currentName = ""; }

            if (!string.IsNullOrEmpty(_profileDir) && currentName == _profileName)
                return;

            // First resolution or profile changed — clear caches and re-resolve
            _positions = null;
            _colours = null;
            _symbols = null;

            _profileDir = ProfileLocator.Resolve();
            _profileName = currentName;
        }
    }
}

internal static class Json
{
    /// <summary>Rounds a value for the JSON payload. NaN and infinities would
    /// serialise as invalid JSON and lose the whole response, so they become 0.</summary>
    public static double Number(double value, int digits)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Round(value, digits);
    }
}
