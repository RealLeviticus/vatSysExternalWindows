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
    private List<AsdMapMeta>? _asdMaps;

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
        public int[] Infill { get; set; } = { 133, 133, 133 };
        public int[] Jurisdiction { get; set; } = { 200, 255, 200 };
        public int[] NonJurisdiction { get; set; } = { 0, 0, 0 };
        public int[] NonJurisdictionLabel { get; set; } = { 55, 55, 55 };
        public int[] Announced { get; set; } = { 0, 0, 188 };
        public int[] Preactive { get; set; } = { 255, 255, 255 };
        public int[] Handover { get; set; } = { 255, 205, 105 };
        public int[] Emergency { get; set; } = { 209, 46, 46 };
        public int[] Warning { get; set; } = { 255, 255, 0 };
        public int[] CFLHighlight { get; set; } = { 255, 255, 255 };
        public int[] StaticTools { get; set; } = { 255, 230, 180 };
        public int[] Route { get; set; } = { 196, 171, 196 };
        public int[] WindowBackground { get; set; } = { 160, 170, 170 };
        public int[] InteractiveText { get; set; } = { 0, 0, 96 };
    }

    public sealed class AsdMapMeta
    {
        public string Folder { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public double CenterLat { get; set; }
        public double CenterLon { get; set; }
    }

    public List<AsdPosition> GetPositions()
    {
        EnsureProfileDir();
        _positions ??= LoadAsdPositions(_profileDir);
        return _positions;
    }

    public AsdColourSet GetColours()
    {
        EnsureProfileDir();
        _colours ??= LoadAsdColours(_profileDir);
        return _colours;
    }

    public List<AsdMapMeta> GetAsdMaps()
    {
        EnsureProfileDir();
        _asdMaps ??= ScanAllAsdMaps(_profileDir);
        return _asdMaps;
    }

    public List<GroundMapData.MapLayer> GetMapLayers(List<string> mapKeys)
    {
        EnsureProfileDir();
        return LoadAsdLayers(_profileDir, mapKeys);
    }

    public int GetVelocityVectors()
    {
        try { return MMI.VelocityVectors; } catch { return 0; }
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
        var lonDelta = rangeNm / (60.0 * Math.Cos(centerLat * Math.PI / 180.0));

        foreach (var track in tracks)
        {
            try
            {
                if (track.Cancelled) continue;
                var coord = track.LatLong;
                if (coord is null) continue;

                var lat = coord.Latitude;
                var lon = coord.Longitude;

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

                var wakeCat = "";
                var aircraftType = "";
                var runway = "";
                var destination = "";
                var cfl = -1;
                try
                {
                    var fdr = track.CoupledFDR;
                    if (fdr is not null)
                    {
                        try { wakeCat = fdr.AircraftWake ?? ""; } catch { }
                        try { aircraftType = fdr.AircraftType ?? ""; } catch { }
                        try { runway = fdr.DepartureRunway?.Name ?? ""; } catch { }
                        try { destination = fdr.DesAirport ?? ""; } catch { }
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
                    Hdg = Math.Round(heading, 1),
                    Gs = Math.Round(groundSpeed, 0),
                    Alt = altitude,
                    CFL = cfl,
                    VSpd = Math.Round(verticalSpeed, 0),
                    State = state,
                    OnGround = onGround,
                    WakeCat = wakeCat,
                    AircraftType = aircraftType,
                    Runway = runway,
                    Destination = destination,
                    History = history,
                });
            }
            catch { }
        }

        return result;
    }

    /// <summary>Gets the HMI state string for a radar track via reflection on the internal Track wrapper.</summary>
    private static string GetTrackState(RDP.RadarTrack radarTrack)
    {
        try
        {
            // The Track class wraps RadarTrack and holds the State field
            var fdr = radarTrack.CoupledFDR;
            if (fdr is null) return "NonJurisdiction";

            // FDR.CoupledTrack points back to the RadarTrack — but the Track object
            // is the one with State. Use reflection to find it from the FDR.
            var fdrType = fdr.GetType();
            // Look for a property/field that returns a Track object with a State field
            foreach (var prop in fdrType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (prop.PropertyType.Name != "Track") continue;
                var trackObj = prop.GetValue(fdr);
                if (trackObj is null) continue;
                var stateProp = trackObj.GetType().GetField("State", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (stateProp is not null) return stateProp.GetValue(trackObj)?.ToString() ?? "NonJurisdiction";
            }
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
                var rgb = ReadRgb(elem);
                if (rgb is null) continue;

                switch (id)
                {
                    case "ASDBackground": colours.Background = rgb; break;
                    case "PrimaryMap": colours.PrimaryMap = rgb; break;
                    case "SecondaryMap": colours.SecondaryMap = rgb; break;
                    case "Infill": colours.Infill = rgb; break;
                    case "Jurisdiction": colours.Jurisdiction = rgb; break;
                    case "NonJurisdiction": colours.NonJurisdiction = rgb; break;
                    case "NonJurisdictionLabel": colours.NonJurisdictionLabel = rgb; break;
                    case "Announced": colours.Announced = rgb; break;
                    case "Preactive": colours.Preactive = rgb; break;
                    case "Handover": colours.Handover = rgb; break;
                    case "Emergency": colours.Emergency = rgb; break;
                    case "Warning": colours.Warning = rgb; break;
                    case "CFLHighlight": colours.CFLHighlight = rgb; break;
                    case "StaticTools": colours.StaticTools = rgb; break;
                    case "Route": colours.Route = rgb; break;
                    case "WindowBackground": colours.WindowBackground = rgb; break;
                    case "InteractiveText": colours.InteractiveText = rgb; break;
                }
            }
        }
        catch { }

        return colours;
    }

    private static int[]? ReadRgb(XElement elem)
    {
        if (!int.TryParse(elem.Element("R")?.Value, out var r)) return null;
        if (!int.TryParse(elem.Element("G")?.Value, out var g)) return null;
        if (!int.TryParse(elem.Element("B")?.Value, out var b)) return null;
        return new[] { r, g, b };
    }

    // ---- ASD Map scanning ----

    private static List<AsdMapMeta> ScanAllAsdMaps(string profileDir)
    {
        var result = new List<AsdMapMeta>();
        var mapsDir = Path.Combine(profileDir, "Maps");
        if (!Directory.Exists(mapsDir)) return result;

        try
        {
            var files = Directory.GetFiles(mapsDir, "*.xml", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                try
                {
                    var dirName = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
                    var folder = string.Equals(dirName, "Maps", StringComparison.OrdinalIgnoreCase) ? "" : dirName;

                    var doc = XDocument.Load(file);
                    var root = doc.Root;
                    if (root is null) continue;

                    foreach (var mapElem in root.Elements("Map"))
                    {
                        var type = mapElem.Attribute("Type")?.Value ?? "";
                        // ASD-category: Type does NOT start with "Ground_"
                        if (type.StartsWith("Ground_", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.IsNullOrWhiteSpace(type)) continue;

                        var name = mapElem.Attribute("Name")?.Value ?? "";
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        var centerStr = mapElem.Attribute("Center")?.Value ?? "";
                        var (clat, clon) = string.IsNullOrWhiteSpace(centerStr)
                            ? (0.0, 0.0)
                            : GroundMapData.ParseCoordinate(centerStr);

                        result.Add(new AsdMapMeta { Folder = folder, Name = name, Type = type, CenterLat = clat, CenterLon = clon });
                    }
                }
                catch { }
            }
        }
        catch { }

        result.Sort((a, b) =>
        {
            var fc = string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase);
            return fc != 0 ? fc : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    /// <summary>Loads geometry for specific ASD map layers.</summary>
    private static List<GroundMapData.MapLayer> LoadAsdLayers(string profileDir, List<string> mapKeys)
    {
        var result = new List<GroundMapData.MapLayer>();
        if (mapKeys.Count == 0) return result;

        var mapsDir = Path.Combine(profileDir, "Maps");
        if (!Directory.Exists(mapsDir)) return result;

        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in mapKeys) needed.Add(key);

        try
        {
            var files = Directory.GetFiles(mapsDir, "*.xml", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                if (needed.Count == 0) break;
                try
                {
                    var dirName = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
                    var folder = string.Equals(dirName, "Maps", StringComparison.OrdinalIgnoreCase) ? "" : dirName;

                    var doc = XDocument.Load(file);
                    var root = doc.Root;
                    if (root is null) continue;

                    foreach (var mapElem in root.Elements("Map"))
                    {
                        var type = mapElem.Attribute("Type")?.Value ?? "";
                        if (type.StartsWith("Ground_", StringComparison.OrdinalIgnoreCase)) continue;

                        var name = mapElem.Attribute("Name")?.Value ?? "";
                        var key = string.IsNullOrEmpty(folder) ? name : folder + "/" + name;
                        if (!needed.Contains(key)) continue;

                        needed.Remove(key);
                        var priority = int.TryParse(mapElem.Attribute("Priority")?.Value, out var p) ? p : 99;
                        var layer = new GroundMapData.MapLayer { Type = type, Name = name, Folder = folder, Priority = priority };

                        // Reuse the shared map geometry parser via reflection or parse inline
                        ParseAsdMapGeometry(mapElem, layer);

                        if (layer.Infills.Count > 0 || layer.Lines.Count > 0 || layer.Labels.Count > 0 || layer.Symbols.Count > 0)
                            result.Add(layer);
                    }
                }
                catch { }
            }
        }
        catch { }

        result.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        return result;
    }

    private static void ParseAsdMapGeometry(XElement mapElem, GroundMapData.MapLayer layer)
    {
        // Infills
        foreach (var infillElem in mapElem.Elements("Infill"))
        {
            var infillName = infillElem.Attribute("Name")?.Value ?? "";
            var rawText = infillElem.Element("Point")?.Value ?? "";
            if (string.IsNullOrWhiteSpace(rawText))
                rawText = infillElem.Value;

            var poly = new GroundMapData.InfillPolygon { Name = infillName };
            foreach (var part in rawText.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (trimmed.Length == 0) continue;
                var (lat, lon) = ResolvePoint(trimmed);
                if (lat != 0 || lon != 0)
                    poly.Points.Add(new[] { Math.Round(lat, 8), Math.Round(lon, 8) });
            }
            if (poly.Points.Count >= 3)
                layer.Infills.Add(poly);
        }

        // Lines
        foreach (var lineElem in mapElem.Elements("Line"))
        {
            var lineName = lineElem.Attribute("Name")?.Value ?? "";
            var linePattern = lineElem.Attribute("Pattern")?.Value ?? "";
            var lineWidth = double.TryParse(lineElem.Attribute("Width")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 1.0;

            // Collect all coordinate text from the Line element
            string rawText;
            var pointElem = lineElem.Element("Point");
            if (pointElem is not null)
            {
                // Single <Point> child wrapping slash-separated coords
                rawText = pointElem.Value;
            }
            else
            {
                // Direct text content in <Line> element
                rawText = lineElem.Value;
            }

            if (!string.IsNullOrWhiteSpace(rawText))
            {
                var line = new GroundMapData.MapLine { Name = lineName, Pattern = linePattern, Width = lineWidth };
                // Route format: "CS VOR.A216.HOOVR" — dots but NO slashes
                // Coordinate format: "-344727.000+1382426.000/..." — has slashes
                // Waypoint format: "MAKKA/AKMIR/WELSH" — slashes, no dots
                bool isRoute = rawText.Contains('.') && !rawText.Contains('/');
                if (isRoute)
                {
                    // Route: split by dots and resolve each named fix
                    foreach (var fixName in rawText.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = fixName.Trim();
                        if (trimmed.Length == 0) continue;
                        var (lat, lon) = ResolveNamedFix(trimmed);
                        if (lat != 0 || lon != 0)
                            line.Points.Add(new[] { Math.Round(lat, 8), Math.Round(lon, 8) });
                    }
                }
                else
                {
                    // Slash-separated: coordinates or waypoint names
                    foreach (var part in rawText.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = part.Trim();
                        if (trimmed.Length == 0) continue;
                        var (lat, lon) = ResolvePoint(trimmed);
                        if (lat != 0 || lon != 0)
                            line.Points.Add(new[] { Math.Round(lat, 8), Math.Round(lon, 8) });
                    }
                }
                if (line.Points.Count >= 2)
                    layer.Lines.Add(line);
            }
        }

        // Symbols
        foreach (var symElem in mapElem.Elements("Symbol"))
        {
            var symType = symElem.Attribute("Type")?.Value ?? "DotFillCircle";
            foreach (var ptElem in symElem.Elements("Point"))
            {
                var ptText = ptElem.Value.Trim();
                if (ptText.Length == 0) continue;
                var (lat, lon) = ResolvePoint(ptText);
                if (lat != 0 || lon != 0)
                    layer.Symbols.Add(new GroundMapData.MapSymbol { SymbolType = symType, Lat = lat, Lon = lon });
            }
        }

        // Labels
        foreach (var labelElem in mapElem.Elements("Label"))
        {
            foreach (var ptElem in labelElem.Elements("Point"))
            {
                var text = ptElem.Attribute("Name")?.Value ?? "";
                var ptText = ptElem.Value.Trim();
                if (ptText.Length == 0) continue;

                // If no Name attribute, the text content IS the waypoint name (used as both label and lookup)
                if (string.IsNullOrWhiteSpace(text))
                    text = ptText;

                var (lat, lon) = ResolvePoint(ptText);
                if (lat != 0 || lon != 0)
                    layer.Labels.Add(new GroundMapData.MapLabel { Text = text, Lat = lat, Lon = lon });
            }
        }

        // Runways — draw as lines with extended centerlines
        foreach (var rwyElem in mapElem.Elements("Runway"))
        {
            var thresholds = rwyElem.Elements("Threshold").ToList();
            if (thresholds.Count < 2) continue;

            var pos1Str = thresholds[0].Attribute("Position")?.Value ?? "";
            var pos2Str = thresholds[1].Attribute("Position")?.Value ?? "";
            var (lat1, lon1) = GroundMapData.ParseCoordinate(pos1Str);
            var (lat2, lon2) = GroundMapData.ParseCoordinate(pos2Str);
            if (lat1 == 0 && lon1 == 0) continue;
            if (lat2 == 0 && lon2 == 0) continue;

            // Runway outline
            layer.Lines.Add(new GroundMapData.MapLine
            {
                Name = rwyElem.Attribute("Name")?.Value ?? "",
                Points = { new[] { Math.Round(lat1, 8), Math.Round(lon1, 8) }, new[] { Math.Round(lat2, 8), Math.Round(lon2, 8) } }
            });

            // Extended centerlines for each threshold
            foreach (var thr in thresholds)
            {
                var posStr = thr.Attribute("Position")?.Value ?? "";
                var trackStr = thr.Attribute("ExtendedCentrelineTrack")?.Value ?? "";
                var lenStr = thr.Attribute("ExtendedCentrelineLength")?.Value ?? "";
                var tickStr = thr.Attribute("ExtendedCentrelineTickInterval")?.Value ?? "";

                if (string.IsNullOrEmpty(trackStr) || string.IsNullOrEmpty(lenStr)) continue;

                var (tLat, tLon) = GroundMapData.ParseCoordinate(posStr);
                if (tLat == 0 && tLon == 0) continue;
                if (!double.TryParse(trackStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var track)) continue;
                if (!double.TryParse(lenStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lenNm)) continue;

                var trackRad = track * Math.PI / 180.0;
                var endLat = tLat + Math.Cos(trackRad) * lenNm / 60.0;
                var endLon = tLon + Math.Sin(trackRad) * lenNm / (60.0 * Math.Cos(tLat * Math.PI / 180.0));

                layer.Lines.Add(new GroundMapData.MapLine
                {
                    Name = thr.Attribute("Name")?.Value ?? "",
                    Pattern = "Dashed",
                    Points = { new[] { Math.Round(tLat, 8), Math.Round(tLon, 8) }, new[] { Math.Round(endLat, 8), Math.Round(endLon, 8) } }
                });

                // Tick marks along the extended centerline
                if (double.TryParse(tickStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var tickNm) && tickNm > 0)
                {
                    var perpRad = trackRad + Math.PI / 2.0;
                    var tickHalf = 0.3 / 60.0; // ~0.3nm half-tick
                    for (var d = tickNm; d <= lenNm; d += tickNm)
                    {
                        var mLat = tLat + Math.Cos(trackRad) * d / 60.0;
                        var mLon = tLon + Math.Sin(trackRad) * d / (60.0 * Math.Cos(tLat * Math.PI / 180.0));
                        var cosMLat = Math.Cos(mLat * Math.PI / 180.0);
                        var t1Lat = mLat + Math.Cos(perpRad) * tickHalf;
                        var t1Lon = mLon + Math.Sin(perpRad) * tickHalf / cosMLat;
                        var t2Lat = mLat - Math.Cos(perpRad) * tickHalf;
                        var t2Lon = mLon - Math.Sin(perpRad) * tickHalf / cosMLat;
                        layer.Lines.Add(new GroundMapData.MapLine
                        {
                            Points = { new[] { Math.Round(t1Lat, 8), Math.Round(t1Lon, 8) }, new[] { Math.Round(t2Lat, 8), Math.Round(t2Lon, 8) } }
                        });
                    }
                }
            }
        }
    }

    /// <summary>Tries to parse as coordinate first, then falls back to named fix resolution.</summary>
    private static (double lat, double lon) ResolvePoint(string text)
    {
        // Try coordinate parse first
        var (lat, lon) = GroundMapData.ParseCoordinate(text);
        if (lat != 0 || lon != 0) return (lat, lon);

        // Try named fix resolution
        return ResolveNamedFix(text);
    }

    /// <summary>Resolves a named fix/navaid/waypoint to coordinates via vatSys.</summary>
    private static (double lat, double lon) ResolveNamedFix(string name)
    {
        try
        {
            var intersection = Airspace2.GetIntersection(name.Trim());
            if (intersection?.LatLong is not null)
                return (intersection.LatLong.Latitude, intersection.LatLong.Longitude);
        }
        catch { }
        return (0, 0);
    }

    // ---- Profile resolution (same logic as GroundDataService) ----

    private void EnsureProfileDir()
    {
        try
        {
            var currentName = Profile.Name ?? "";
            if (!string.IsNullOrEmpty(_profileDir) && currentName == _profileName) return;
            if (!string.IsNullOrEmpty(_profileDir) && currentName != _profileName) _profileDir = "";
        }
        catch
        {
            if (!string.IsNullOrEmpty(_profileDir)) return;
        }

        _positions = null;
        _colours = null;
        _asdMaps = null;

        try
        {
            var name = Profile.Name;
            if (!string.IsNullOrEmpty(name))
            {
                var docDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var candidates = new[]
                {
                    Path.Combine(docDir, "vatSys Files", "Profiles", name),
                    Path.Combine(docDir, "vatSys", "Profiles", name),
                };
                foreach (var dir in candidates)
                {
                    if (Directory.Exists(dir))
                    {
                        _profileDir = dir;
                        _profileName = name ?? "";
                        return;
                    }
                }
            }
        }
        catch { }

        var profilesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "vatSys Files", "Profiles");

        if (Directory.Exists(profilesRoot))
        {
            foreach (var dir in Directory.GetDirectories(profilesRoot))
            {
                if (File.Exists(Path.Combine(dir, "Positions.xml")))
                {
                    _profileDir = dir;
                    return;
                }
            }
        }

        _profileDir = "";
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
    public string WakeCat { get; set; } = "";
    public string AircraftType { get; set; } = "";
    public string Runway { get; set; } = "";
    public string Destination { get; set; } = "";
    public double[][]? History { get; set; }
}
