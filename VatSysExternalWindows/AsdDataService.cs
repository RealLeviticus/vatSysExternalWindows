using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using vatsys;

namespace VatSysExternalWindows;

/// <summary>
/// Provides ASD (Air Situation Display) positions, map geometry, colours,
/// and live aircraft data for the ASD web UI.
/// </summary>
public sealed class AsdDataService
{
    private readonly object _lock = new();
    private string _profileDir = "";
    private string _profileName = "";
    private List<AsdPosition>? _positions;
    private AsdColourSet? _colours;

    public sealed class AsdPosition
    {
        public string Name { get; set; } = "";
        public string Group { get; set; } = "";
        public double CenterLat { get; set; }
        public double CenterLon { get; set; }
        public double DefaultRange { get; set; } = 200;
        public List<string> MapRefs { get; set; } = new();
    }

    public sealed class AsdColourSet
    {
        public int[] Background { get; set; } = { 127, 127, 127 };
        public int[] PrimaryMap { get; set; } = { 90, 90, 90 };
        public int[] SecondaryMap { get; set; } = { 145, 178, 177 };
        public int[] UserMap { get; set; } = { 100, 100, 100 };
        public int[] PRDArea { get; set; } = { 100, 100, 100 };
        public int[] Infill { get; set; } = { 133, 133, 133 };
        public int[] Jurisdiction { get; set; } = { 200, 255, 200 };
        public int[] GhostJurisdiction { get; set; } = { 200, 255, 200 };
        public int[] PostJurisdiction { get; set; } = { 200, 255, 200 };
        public int[] NonJurisdiction { get; set; } = { 0, 0, 0 };
        public int[] NonJurisdictionLabel { get; set; } = { 55, 55, 55 };
        public int[] Announced { get; set; } = { 0, 0, 188 };
        public int[] Preactive { get; set; } = { 255, 255, 255 };
        public int[] Handover { get; set; } = { 255, 205, 105 };
        public int[] Emergency { get; set; } = { 209, 46, 46 };
        public int[] Warning { get; set; } = { 255, 255, 0 };
        public int[] IdentFlash { get; set; } = { 255, 255, 255 };
        public int[] CFLHighlight { get; set; } = { 255, 255, 255 };
        public int[] StaticTools { get; set; } = { 255, 230, 180 };
        public int[] Route { get; set; } = { 196, 171, 196 };
        public int[] WindowBackground { get; set; } = { 160, 170, 170 };
        public int[] InteractiveText { get; set; } = { 0, 0, 96 };
        /// <summary>All Custom colours from Colours.xml by name — vatSys resolves any
        /// map CustomColourName via Colours.GetCustomColorDX.</summary>
        public Dictionary<string, int[]> CustomColours { get; set; } = new();
    }

    public List<AsdPosition> GetPositions()
    {
        lock (_lock)
        {
            EnsureProfileDir();
            return _positions ??= LoadAsdPositions(_profileDir);
        }
    }

    public AsdColourSet GetColours()
    {
        lock (_lock)
        {
            EnsureProfileDir();
            return _colours ??= LoadAsdColours(_profileDir);
        }
    }

    /// <summary>Metadata for all ASD-category maps vatSys loaded (for the Maps menu).</summary>
    public List<MapMeta> GetAsdMaps()
    {
        return MapSource.ListMaps(DisplayMaps.MapCategories.ASD);
    }

    /// <summary>Geometry for specific ASD map layers identified by "Folder/Name" keys.</summary>
    public List<MapLayer> GetMapLayers(List<string> mapKeys)
    {
        return MapSource.GetLayers(DisplayMaps.MapCategories.ASD, mapKeys);
    }

    public int GetVelocityVectors()
    {
        try { return MMI.VelocityVectors; } catch { return 0; }
    }

    /// <summary>Transition altitude in feet — vatSys Limited labels show levels
    /// above it as FLnnn and below it as 5-digit feet.</summary>
    public int GetTransitionAltitude()
    {
        try { return RDP.TRANSITION_ALTITUDE; } catch { return 10000; }
    }

    private volatile List<MapLayer> _dynamicMapsSnapshot = new();

    /// <summary>Runtime maps vatSys always paints on the ASD: the controlled-sector
    /// infill ("Dynamic Infill"), active restricted-area outlines ("Dynamic RMAPS",
    /// Type REST_NTZ_DAIW → PRDArea colour) and their infills. Built on the GUI
    /// thread, so snapshot there like the crossing/release maps.</summary>
    public List<MapLayer> GetDynamicMaps()
    {
        try
        {
            MMI.InvokeOnGUI(delegate { _dynamicMapsSnapshot = ComputeDynamicMaps(); });
        }
        catch { }
        return _dynamicMapsSnapshot;
    }

    private static List<MapLayer> ComputeDynamicMaps()
    {
        var result = new List<MapLayer>();
        try { AddDynamicMap(result, DisplayMaps.DynamicInfill); } catch { }
        try { AddDynamicMap(result, DisplayMaps.DynamicRestrictedAreaInfills); } catch { }
        try { AddDynamicMap(result, DisplayMaps.DynamicRestrictedAreas); } catch { }
        return result;
    }

    private static void AddDynamicMap(List<MapLayer> list, DisplayMaps.Map? map)
    {
        var layer = MapSource.ToLayer(map);
        if (layer is not null) list.Add(layer);
    }

    /// <summary>Returns all radar tracks visible within the given bounds.</summary>
    public List<AsdAircraftDto> GetAircraft(double centerLat, double centerLon, double rangeNm)
    {
        var result = new List<AsdAircraftDto>();

        IList<RDP.RadarTrack>? tracks = null;
        try { tracks = RDP.RadarTracks; } catch { }
        if (tracks is null) return result;

        // Convert range to approximate lat/lon delta for filtering
        var latDelta = rangeNm / 60.0;
        var lonDelta = rangeNm / (60.0 * Math.Max(0.01, Math.Cos(centerLat * Math.PI / 180.0)));

        foreach (var track in tracks)
        {
            try
            {
                if (track.Cancelled) continue;
                var coord = track.LatLong;
                if (coord is null) continue;

                var lat = coord.Latitude;
                var lon = coord.Longitude;
                if (double.IsNaN(lat) || double.IsNaN(lon)) continue;

                if (Math.Abs(lat - centerLat) > latDelta || Math.Abs(lon - centerLon) > lonDelta)
                    continue;

                var callsign = "";
                var heading = 0.0;
                var groundSpeed = 0.0;
                var onGround = false;

                try { onGround = track.OnGround; } catch { }
                try { heading = track.Heading; } catch { }
                try { groundSpeed = track.GroundSpeed; } catch { }

                var verticalSpeed = 0.0;
                try { verticalSpeed = track.VerticalSpeed; } catch { }

                // Symbol selection inputs (PaintASDTrack): radar source flags, coast and
                // ident state, and squawk for Tracks.xml SpecialCode symbol variants
                var radarFlags = 0;
                try { radarFlags = (int)track.RadarTypes; } catch { }
                var coasting = false;
                try { coasting = track.Coasting; } catch { }
                var identing = false;
                try { identing = track.Identing; } catch { }
                var ssr = -1;
                try { ssr = track.ActualAircraft?.TransponderCode ?? -1; } catch { }

                var state = "NonJurisdiction";
                try
                {
                    state = GetTrackState(track);
                }
                catch { }

                try
                {
                    var fdr = track.CoupledFDR;
                    if (fdr is not null) callsign = fdr.Callsign ?? "";
                }
                catch { }

                if (string.IsNullOrEmpty(callsign))
                {
                    try
                    {
                        var pilot = track.ActualAircraft;
                        if (pilot is not null) callsign = pilot.Callsign ?? "";
                    }
                    catch { }
                }

                var coupled = false;
                var wakeCat = "";
                var aircraftType = "";
                var runway = "";
                var cfl = -1;
                try
                {
                    var fdr = track.CoupledFDR;
                    if (fdr is not null)
                    {
                        coupled = true;
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
                        try { cfl = fdr.CFLUpper; } catch { }
                    }
                }
                catch { }

                var altitude = -1;
                try { altitude = track.CorrectedAltitude; } catch { }

                // Position history
                double[][]? history = null;
                var maxDots = MMI.HistoryDots;
                try
                {
                    if (maxDots > 0)
                    {
                        var posHist = track.PositionHistory?.ToArray();
                        if (posHist is not null && posHist.Length > 0)
                        {
                            var dots = new List<double[]>();
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

                result.Add(new AsdAircraftDto
                {
                    Callsign = callsign,
                    Lat = Math.Round(lat, 7),
                    Lon = Math.Round(lon, 7),
                    Hdg = Json.Number(heading, 1),
                    Gs = Json.Number(groundSpeed, 0),
                    Alt = altitude,
                    CFL = cfl,
                    VSpd = Json.Number(verticalSpeed, 0),
                    State = state,
                    OnGround = onGround,
                    Coupled = coupled,
                    RadarFlags = radarFlags,
                    Coasting = coasting,
                    Ident = identing,
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

    /// <summary>Gets the HMI state for a radar track. vatSys resolves the display Track
    /// wrapper via MMI.FindTrack (keyed by the radar track or its coupled FDR) and the
    /// wrapper holds the HMI state in its public State field.</summary>
    private static string GetTrackState(RDP.RadarTrack radarTrack)
    {
        try
        {
            var track = MMI.FindTrack(radarTrack);
            if (track is not null) return track.State.ToString();
        }
        catch { }

        return "NonJurisdiction";
    }

    // ---- Position loading ----

    private static List<AsdPosition> LoadAsdPositions(string profileDir)
    {
        var result = new List<AsdPosition>();
        var path = Path.Combine(profileDir, "Positions.xml");
        if (!File.Exists(path)) return result;

        try
        {
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root is null) return result;

            foreach (var elem in root.Elements("Position"))
                TryAddAsdPosition(elem, "", result);

            foreach (var group in root.Elements("Group"))
            {
                var groupName = group.Attribute("Name")?.Value ?? "";
                foreach (var elem in group.Elements("Position"))
                    TryAddAsdPosition(elem, groupName, result);
            }
        }
        catch { }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static void TryAddAsdPosition(XElement elem, string group, List<AsdPosition> result)
    {
        var type = elem.Attribute("Type")?.Value ?? "";
        if (!string.Equals(type, "ASD", StringComparison.OrdinalIgnoreCase))
            return;

        var name = elem.Attribute("Name")?.Value ?? "";
        var centerStr = elem.Attribute("DefaultCenter")?.Value ?? "";
        var rangeStr = elem.Attribute("DefaultRange")?.Value ?? "200";

        var (lat, lon) = GroundMapData.ParseCoordinate(centerStr);
        double.TryParse(rangeStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var range);
        if (range <= 0) range = 200;

        var mapRefs = new List<string>();
        var mapsElem = elem.Element("Maps");
        if (mapsElem is not null)
        {
            foreach (var m in mapsElem.Elements("Map"))
            {
                var mapName = m.Attribute("Name")?.Value ?? "";
                if (!string.IsNullOrWhiteSpace(mapName))
                    mapRefs.Add(mapName);
            }
        }

        result.Add(new AsdPosition
        {
            Name = name,
            Group = group,
            CenterLat = lat,
            CenterLon = lon,
            DefaultRange = range,
            MapRefs = mapRefs,
        });
    }

    // ---- Colour loading ----

    private static AsdColourSet LoadAsdColours(string profileDir)
    {
        var colours = new AsdColourSet();
        var path = Path.Combine(profileDir, "Colours.xml");
        if (!File.Exists(path)) return colours;

        try
        {
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root is null) return colours;

            foreach (var elem in root.Elements("Colour"))
            {
                var id = elem.Attribute("id")?.Value ?? "";
                var rgb = GroundMapData.ReadRgb(elem);
                if (rgb is null) continue;

                switch (id)
                {
                    case "ASDBackground": colours.Background = rgb; break;
                    case "PrimaryMap": colours.PrimaryMap = rgb; break;
                    case "SecondaryMap": colours.SecondaryMap = rgb; break;
                    case "UserMap": colours.UserMap = rgb; break;
                    case "PRDArea": colours.PRDArea = rgb; break;
                    case "Infill": colours.Infill = rgb; break;
                    case "Jurisdiction": colours.Jurisdiction = rgb; break;
                    case "GhostJurisdiction": colours.GhostJurisdiction = rgb; break;
                    case "PostJurisdiction": colours.PostJurisdiction = rgb; break;
                    case "NonJurisdiction": colours.NonJurisdiction = rgb; break;
                    case "NonJurisdictionLabel": colours.NonJurisdictionLabel = rgb; break;
                    case "Announced": colours.Announced = rgb; break;
                    case "Preactive": colours.Preactive = rgb; break;
                    case "Handover": colours.Handover = rgb; break;
                    case "Emergency": colours.Emergency = rgb; break;
                    case "Warning": colours.Warning = rgb; break;
                    case "IdentFlash": colours.IdentFlash = rgb; break;
                    case "CFLHighlight": colours.CFLHighlight = rgb; break;
                    case "StaticTools": colours.StaticTools = rgb; break;
                    case "Route": colours.Route = rgb; break;
                    case "WindowBackground":
                    case "WindowBackgound": colours.WindowBackground = rgb; break;
                    case "InteractiveText": colours.InteractiveText = rgb; break;
                    case "Custom":
                        var name = elem.Element("Name")?.Value ?? "";
                        if (!string.IsNullOrWhiteSpace(name)) colours.CustomColours[name] = rgb;
                        break;
                }
            }
        }
        catch { }

        return colours;
    }

    // ---- Profile resolution (same logic as GroundDataService) ----

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

            _profileDir = ProfileLocator.Resolve();
            _profileName = currentName;
        }
    }
}

public sealed class AsdAircraftDto
{
    public string Callsign { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double Hdg { get; set; }
    public double Gs { get; set; }
    public int Alt { get; set; } = -1;
    public int CFL { get; set; } = -1;
    public double VSpd { get; set; }
    public string State { get; set; } = "NonJurisdiction";
    public bool OnGround { get; set; }
    /// <summary>True when an FDR is coupled to the radar track.</summary>
    public bool Coupled { get; set; }
    /// <summary>RDP.RadarTypes flags: 1=PRI 2=ModeC 4=ModeS 8=ADSB 16=SMR 32=SMR_ADSB.</summary>
    public int RadarFlags { get; set; }
    public bool Coasting { get; set; }
    public bool Ident { get; set; }
    /// <summary>Transponder code for Tracks.xml SpecialCode symbol variants (-1 unknown).</summary>
    public int Ssr { get; set; } = -1;
    public string WakeCat { get; set; } = "";
    public string AircraftType { get; set; } = "";
    public string Runway { get; set; } = "";
    public double[][]? History { get; set; }
}
