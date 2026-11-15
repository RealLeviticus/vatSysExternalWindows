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

    /// <summary>Current ASD label font size in pixels (MMI.ASDMainFont honours the user's setting).</summary>
    public double GetLabelFontSize()
    {
        try { return MMI.ASDMainFont.Size; } catch { return 18; }
    }

    /// <summary>Current ASD map font size in pixels.</summary>
    public double GetMapFontSize()
    {
        try { return MMI.ASDMapFont.Size; } catch { return 16; }
    }

    /// <summary>Geometry for specific Ground map layers identified by "Folder/Name" keys.</summary>
    public List<MapLayer> GetMapLayers(List<string> mapKeys)
    {
        return MapSource.GetLayers(DisplayMaps.MapCategories.Ground, mapKeys);
    }

    /// <summary>Returns aircraft around the given airport. vatSys paints every track
    /// inside the Ground window, so the client passes how far its view reaches
    /// (<paramref name="rangeNm"/> from the position centre); without it a small
    /// default box around the airport is used.</summary>
    public List<AircraftDto> GetAircraft(string icao, double? rangeNm = null)
    {
        var result = new List<AircraftDto>();
        var pos = GetPositions().FirstOrDefault(p =>
            string.Equals(p.Airport, icao, StringComparison.OrdinalIgnoreCase));
        if (pos is null) return result;

        var latDelta = DefaultBoxDegrees;
        var lonDelta = DefaultBoxDegrees;
        if (rangeNm is double range && range > 0 && !double.IsNaN(range))
        {
            range = Math.Min(range, MaxRangeNm);
            latDelta = range / 60.0;
            lonDelta = range / (60.0 * Math.Max(0.01, Math.Cos(pos.CenterLat * Math.PI / 180.0)));
        }

        IList<RDP.RadarTrack>? tracks = null;
        try { tracks = RDP.RadarTracks; } catch { }
        if (tracks is null) return result;

        foreach (var track in tracks)
        {
            try
            {
                if (track.Cancelled) continue;

                // vatSys PaintGroundTracks skips tracks without a network pilot
                var pilot = track.ActualAircraft;
                if (pilot is null) continue;

                var coord = track.LatLong;
                if (coord is null) continue;

                var lat = coord.Latitude;
                var lon = coord.Longitude;
                if (double.IsNaN(lat) || double.IsNaN(lon)) continue;

                if (Math.Abs(lat - pos.CenterLat) > latDelta || Math.Abs(lon - pos.CenterLon) > lonDelta)
                    continue;

                var onGround = false;
                try { onGround = track.OnGround; } catch { }

                // vatSys rotates ground symbols to the pilot's reported heading (nose
                // direction — differs from track during pushback) and paints airborne
                // symbols with the calculated radar track heading.
                var heading = 0.0;
                try { heading = onGround ? pilot.Heading : track.Heading; } catch { }

                var groundSpeed = 0.0;
                try { groundSpeed = track.GroundSpeed; } catch { }

                // Symbol selection inputs (PaintGroundTracks): radar source flags and
                // squawk code for Tracks.xml SpecialCode symbol variants
                var radarFlags = 0;
                try { radarFlags = (int)track.RadarTypes; } catch { }
                var ssr = -1;
                try { ssr = pilot.TransponderCode; } catch { }

                // vatSys: ASMGCSCoupledFDR takes precedence over CoupledFDR for ground display
                FDP2.FDR? fdr = null;
                try { fdr = track.ASMGCSCoupledFDR ?? track.CoupledFDR; } catch { }

                var callsign = "";
                try { callsign = fdr?.Callsign ?? ""; } catch { }
                if (string.IsNullOrEmpty(callsign)) callsign = pilot.Callsign ?? "";

                var category = "unknown";
                var wakeCat = "";
                var aircraftType = "";
                var runway = "";
                if (fdr is not null)
                {
                    try
                    {
                        var dep = fdr.DepAirport ?? "";
                        var arr = fdr.DesAirport ?? "";
                        if (dep.Equals(icao, StringComparison.OrdinalIgnoreCase))
                            category = "departure";
                        else if (arr.Equals(icao, StringComparison.OrdinalIgnoreCase))
                            category = "arrival";
                        else
                            category = "local";

                        try { wakeCat = fdr.AircraftWake ?? ""; } catch { }
                        try { aircraftType = fdr.AircraftType ?? ""; } catch { }
                        // vatSys LABEL_ITEM_RWY: departure runway until ATD is set,
                        // arrival runway once the aircraft has departed
                        try
                        {
                            runway = fdr.ATD != DateTime.MaxValue
                                ? fdr.ArrivalRunway?.Name ?? ""
                                : fdr.DepartureRunway?.Name ?? "";
                        }
                        catch { }
                    }
                    catch { }
                }

                // vatSys wake fallback for uncoupled/blank wake: performance data for the
                // pilot's reported type, then engine type (piston → L, turboprop → M).
                if (string.IsNullOrEmpty(wakeCat))
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(pilot.AircraftType))
                            wakeCat = Performance.GetAircraftFromType(pilot.AircraftType)?.WakeCategory ?? "";
                    }
                    catch { }
                }
                if (string.IsNullOrEmpty(wakeCat))
                {
                    try
                    {
                        // NetworkPilot.EngineType is from the unreferenced vatSys network
                        // assembly — read it reflectively.
                        var engineField = pilot.GetType().GetField("EngineType");
                        if (engineField is not null)
                        {
                            var engine = Convert.ToInt32(engineField.GetValue(pilot));
                            if (engine == 0) wakeCat = "L";
                            else if (engine == 1) wakeCat = "M";
                        }
                    }
                    catch { }
                }

                // Get position history trail dots from vatSys RadarTrack.PositionHistory
                double[][]? history = null;
                var maxDots = MMI.HistoryDots; // user setting (0=off, 1-9)
                try
                {
                    if (maxDots > 0)
                    {
                        var posHist = track.PositionHistory?.ToArray();
                        if (posHist is not null && posHist.Length > 0)
                        {
                            var dots = new List<double[]>();
                            // Newest first (iterate backwards), limit to user setting
                            for (int i = posHist.Length - 1; i >= 0 && dots.Count < maxDots; i--)
                            {
                                var h = posHist[i];
                                if (h?.LatLong is null) continue;
                                dots.Add(new[] { Math.Round(h.LatLong.Latitude, 7), Math.Round(h.LatLong.Longitude, 7) });
                            }
                            if (dots.Count > 0) history = dots.ToArray();
                        }
                    }
                }
                catch { }

                // Get altitude for GroundAir label
                var altitude = -1;
                try { altitude = track.CorrectedAltitude; } catch { }

                result.Add(new AircraftDto
                {
                    Callsign = callsign,
                    Lat = Math.Round(lat, 7),
                    Lon = Math.Round(lon, 7),
                    Hdg = Json.Number(heading, 1),
                    Gs = Json.Number(groundSpeed, 0),
                    Alt = altitude,
                    OnGround = onGround,
                    Category = category,
                    Coupled = fdr is not null,
                    RadarFlags = radarFlags,
                    Ssr = ssr,
                    WakeCat = wakeCat,
                    AircraftType = aircraftType,
                    Runway = runway,
                    History = history,
                });
            }
            catch { }
        }

        return result;
    }

    private volatile List<string> _visibleMapsSnapshot = new();

    /// <summary>Returns the currently visible crossing/released map layer keys from vatSys Ground windows.</summary>
    public List<string> GetVisibleCrossingReleasedMaps()
    {
        // Application.OpenForms and the ASD control state belong to the GUI thread —
        // queue a snapshot refresh there and serve the latest completed one. The
        // endpoint is polled continuously, so at most one poll of staleness.
        try
        {
            MMI.InvokeOnGUI(delegate { _visibleMapsSnapshot = ComputeVisibleCrossingReleasedMaps(); });
        }
        catch { }
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

public sealed class AircraftDto
{
    public string Callsign { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double Hdg { get; set; }
    public double Gs { get; set; }
    public int Alt { get; set; } = -1;
    public bool OnGround { get; set; }
    public string Category { get; set; } = "unknown";
    /// <summary>True when an FDR is coupled (ASMGCS or radar coupling).</summary>
    public bool Coupled { get; set; }
    /// <summary>RDP.RadarTypes flags: 1=PRI 2=ModeC 4=ModeS 8=ADSB 16=SMR 32=SMR_ADSB.</summary>
    public int RadarFlags { get; set; }
    /// <summary>Transponder code for Tracks.xml SpecialCode symbol variants (-1 unknown).</summary>
    public int Ssr { get; set; } = -1;
    public string WakeCat { get; set; } = "";
    public string AircraftType { get; set; } = "";
    public string Runway { get; set; } = "";
    public double[][]? History { get; set; }
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
