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

    private volatile DynamicMaps _dynamicMapsSnapshot = new();

    /// <summary>Runtime maps vatSys paints on its windows, in vatSys's paint order.</summary>
    public sealed class DynamicMaps
    {
        /// <summary>Painted before the file maps (ASD only): the controlled-sector
        /// infill, when the user's Dynamic Infill setting is on, then the active
        /// restricted-area infills.</summary>
        public List<MapLayer> Below { get; set; } = new();
        /// <summary>Painted after the file maps (ASD and Ground): active
        /// restricted-area outlines (Type REST_NTZ_DAIW → PRDArea colour).</summary>
        public List<MapLayer> Above { get; set; } = new();
    }

    /// <summary>The dynamic maps as vatSys currently has them. They belong to the
    /// GUI thread, so they are read there; if the GUI is busy the previous
    /// snapshot is served.</summary>
    public DynamicMaps GetDynamicMaps()
    {
        GuiThread.Run(() => _dynamicMapsSnapshot = ComputeDynamicMaps());
        return _dynamicMapsSnapshot;
    }

    private static DynamicMaps ComputeDynamicMaps()
    {
        var result = new DynamicMaps();
        var infillOn = true;
        try { infillOn = MMI.DynamicInfill; } catch { }
        if (infillOn)
        {
            try { AddDynamicMap(result.Below, DisplayMaps.DynamicInfill); } catch { }
        }
        try { AddDynamicMap(result.Below, DisplayMaps.DynamicRestrictedAreaInfills); } catch { }
        try { AddDynamicMap(result.Above, DisplayMaps.DynamicRestrictedAreas); } catch { }
        return result;
    }

    private static void AddDynamicMap(List<MapLayer> list, DisplayMaps.Map? map)
    {
        var layer = MapSource.ToLayer(map);
        if (layer is not null) list.Add(layer);
    }

    /// <summary>Returns the radar tracks within the given bounds, each as vatSys's
    /// PaintASDTrack / PaintASDLabel would paint it.</summary>
    public List<TrackDto> GetAircraft(double centerLat, double centerLon, double rangeNm)
    {
        var result = new List<TrackDto>();

        IList<RDP.RadarTrack>? tracks = null;
        try { tracks = RDP.RadarTracks; } catch { }
        if (tracks is null) return result;

        // Convert range to approximate lat/lon delta for filtering
        var latDelta = rangeNm / 60.0;
        var lonDelta = rangeNm / (60.0 * Math.Max(0.01, Math.Cos(centerLat * Math.PI / 180.0)));

        foreach (var radarTrack in tracks)
        {
            try
            {
                var dto = BuildAsdTrack(radarTrack, centerLat, centerLon, latDelta, lonDelta);
                if (dto is not null) result.Add(dto);
            }
            catch { }
        }

        return result;
    }

    /// <summary>Port of ASDControlDX.PaintASDTrack and PaintASDLabel for one radar track.</summary>
    private static TrackDto? BuildAsdTrack(RDP.RadarTrack radarTrack, double centerLat, double centerLon,
        double latDelta, double lonDelta)
    {
        var pilot = radarTrack.ActualAircraft;
        if (pilot is null) return null;

        var coord = radarTrack.LatLong;
        if (coord is null) return null;
        var lat = coord.Latitude;
        var lon = coord.Longitude;
        if (double.IsNaN(lat) || double.IsNaN(lon)) return null;
        if (Math.Abs(lat - centerLat) > latDelta || Math.Abs(lon - centerLon) > lonDelta) return null;

        // vatSys paints display tracks: one in HMI state None, or not visible on the
        // ASD (SMR-only returns, inhibited or pre-coordination FDRs), is skipped.
        // A radar track with no display Track is still served, without a label.
        Track? tl = null;
        try { tl = MMI.FindTrack(radarTrack); } catch { }
        if (tl != null && (tl.State == MMI.HMIStates.None || !tl.IsVisibleASD)) return null;

        var colour = TrackPainter.AsdColour(tl, out var isNonJurisdictionBrush);
        var types = radarTrack.RadarTypes;
        var code = pilot.TransponderCode;
        var heading = radarTrack.Heading;

        var dto = new TrackDto
        {
            Id = pilot.Callsign ?? "",
            Lat = Math.Round(lat, 7),
            Lon = Math.Round(lon, 7),
            State = (tl?.State ?? MMI.HMIStates.NonJurisdiction).ToString(),
            Col = TrackLabelBuilder.Rgb(colour),
            History = TrackPainter.History(radarTrack),
            HistSsr = code,
        };

        var velocityVectors = 0;
        try { velocityVectors = MMI.VelocityVectors; } catch { }
        if (velocityVectors > 0 && radarTrack.GroundSpeed > 0.0 && !radarTrack.Cancelled && heading != -1.0)
        {
            dto.Vv = velocityVectors;
            dto.Gs = Json.Number(radarTrack.GroundSpeed, 1);
            dto.Trk = Json.Number(heading, 1);
        }

        // Symbol: Mode S, else Mode A for Mode C returns, else ADS-B, else primary
        var symbol = "Primary";
        if (types.HasFlag(RDP.RadarTypes.SSR_ModeS)) symbol = "ModeS";
        else if (types.HasFlag(RDP.RadarTypes.SSR_ModeC)) symbol = "ModeA";
        else if (types.HasFlag(RDP.RadarTypes.ADSB)) symbol = "ADSB";

        var selectable = true;
        if (radarTrack.Cancelled)
        {
            dto.Syms.Add(TrackPainter.Symbol("Cancelled", code, heading));
            selectable = false;
        }
        else if (radarTrack.Coasting)
        {
            dto.Syms.Add(TrackPainter.Symbol(symbol == "ADSB" ? "CoastADSB" : "CoastRadar", code, heading));
        }
        else if (symbol != "Primary")
        {
            // An identing track blinks in the ident colour
            int[]? identColour = radarTrack.Identing ? TrackLabelBuilder.Rgb(TrackLabelBuilder.Colour(Colours.Identities.IdentFlash)) : null;
            var main = TrackPainter.Symbol(symbol, code, heading);
            main.Ident = radarTrack.Identing;
            main.Col = identColour;
            dto.Syms.Add(main);
            if (types.HasFlag(RDP.RadarTypes.PRI))
            {
                var primary = TrackPainter.Symbol("Primary", code, heading);
                primary.Ident = radarTrack.Identing;
                primary.Col = identColour;
                dto.Syms.Add(primary);
            }
        }
        else if (types.HasFlag(RDP.RadarTypes.PRI))
        {
            dto.Syms.Add(TrackPainter.Symbol("Primary", -1, heading));
        }

        if (tl is null) return dto;

        try
        {
            if (selectable && ReferenceEquals(MMI.SelectedTrack, tl))
                dto.Syms.Add(TrackPainter.Symbol("Selection", -1, -1));
        }
        catch { }

        try { dto.Label = BuildAsdLabel(tl, radarTrack, pilot, colour, isNonJurisdictionBrush); } catch { }
        return dto;
    }

    /// <summary>PaintASDLabel: whether the track gets a label, which type, and in
    /// what colour.</summary>
    private static LabelDto? BuildAsdLabel(Track tl, RDP.RadarTrack radarTrack, NetworkPilot pilot,
        System.Drawing.Color trackColour, bool isNonJurisdictionBrush)
    {
        var fdr = radarTrack.CoupledFDR;
        var state = tl.State;

        // No label for cancelled tracks, or for primary-only returns with nothing coupled
        var types = radarTrack.RadarTypes & ~(RDP.RadarTypes.SMR | RDP.RadarTypes.SMR_ADSB);
        if (radarTrack.Cancelled) return null;
        if (types == RDP.RadarTypes.PRI && fdr is null && radarTrack.QuickTag is null) return null;

        // Tracks of interest always get one; other traffic only when airborne, inside
        // the level filters and not matched by an SSR code filter
        var show = MMI.QuicklookAll || tl.IQL || state != MMI.HMIStates.NonJurisdiction ||
                   VatSysInternals.GetAlerts(tl).Count != 0;
        if (!show)
        {
            var altitude = radarTrack.CorrectedAltitude;
            if (altitude < MMI.UpperLevelFilter && altitude > MMI.LowerLevelFilter && !radarTrack.OnGround)
            {
                var filter1 = MMI.SSRCodeFilter1;
                var filter2 = MMI.SSRCodeFilter2;
                var code = pilot.TransponderCode;
                show = !VatSysInternals.SsrFilterMatches(filter1, code) && !VatSysInternals.SsrFilterMatches(filter2, code);
            }
        }
        if (!show) return null;

        string labelType;
        if (fdr is null) labelType = radarTrack.QuickTag is null ? "Limited" : "Quicktag";
        else labelType = tl.ManualExtend || (MMI.ExtendedLabels && state > MMI.HMIStates.PostJurisdiction) ? "Extended" : "Normal";

        // Non-jurisdiction labels use their own colour; handover-in and
        // post-jurisdiction labels blink
        var blink = state == MMI.HMIStates.HandoverIn || (!tl.IQL && state == MMI.HMIStates.PostJurisdiction);
        var labelColour = trackColour;
        if (!blink && !tl.IQL && state == MMI.HMIStates.NonJurisdiction && isNonJurisdictionBrush)
            labelColour = TrackLabelBuilder.Colour(Colours.Identities.NonJurisdictionLabel);

        var label = TrackLabelBuilder.Build(false, tl, fdr, tl.GetQuickTag(), radarTrack, pilot, labelType, labelColour);
        if (label != null) label.Blink = blink;
        return label;
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
