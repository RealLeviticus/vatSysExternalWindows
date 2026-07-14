using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace VatSysExternalWindows;

/// <summary>
/// Parses vatSys profile data: Positions.xml for ASMGCS positions, ASMGCS map XMLs
/// for geometry (infills, lines, labels), and Colours.xml for the colour scheme.
/// Works with any profile, not just Australia.
/// </summary>
public static class GroundMapData
{
    // ---- Data models ----

    public sealed class GroundPosition
    {
        public string Name { get; set; } = "";
        public string Airport { get; set; } = "";
        public string Group { get; set; } = "";
        public double CenterLat { get; set; }
        public double CenterLon { get; set; }
        public double DefaultRange { get; set; } = 2;
        public double Rotation { get; set; }
        public double MagneticVariation { get; set; }
        public List<string> MapRefs { get; set; } = new();
    }

    public sealed class AirportMap
    {
        public string Icao { get; set; } = "";
        public double CenterLat { get; set; }
        public double CenterLon { get; set; }
        public double DefaultRange { get; set; } = 2;
        public double Rotation { get; set; }
        public double MagneticVariation { get; set; }
        public List<MapLayer> Layers { get; set; } = new();
    }

    public sealed class MapLayer
    {
        public string Type { get; set; } = "";
        public string Name { get; set; } = "";
        public string Folder { get; set; } = "";
        public int Priority { get; set; }
        public string CustomColour { get; set; } = "";
        public List<InfillPolygon> Infills { get; set; } = new();
        public List<MapLine> Lines { get; set; } = new();
        public List<MapLabel> Labels { get; set; } = new();
        public List<MapSymbol> Symbols { get; set; } = new();
    }

    /// <summary>Lightweight map metadata for dropdown menu (no geometry).</summary>
    public sealed class GroundMapMeta
    {
        public string Folder { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
    }

    public sealed class InfillPolygon
    {
        public string Name { get; set; } = "";
        /// <summary>Infill Type attribute: "" (Normal), "Background" or "None".
        /// Background infills paint in the window's background colour.</summary>
        public string InfillType { get; set; } = "";
        /// <summary>Infill Pattern (inherits the Map's Pattern): non-solid patterns
        /// fill with the Infill/Background checkerboard on the air window.</summary>
        public string Pattern { get; set; } = "";
        public List<double[]> Points { get; set; } = new();
    }

    public sealed class MapLine
    {
        public string Name { get; set; } = "";
        public string Pattern { get; set; } = "";
        public double Width { get; set; } = 1;
        public List<double[]> Points { get; set; } = new();
    }

    public sealed class MapLabel
    {
        public string Text { get; set; } = "";
        public double Lat { get; set; }
        public double Lon { get; set; }
    }

    public sealed class MapSymbol
    {
        public string SymbolType { get; set; } = "";
        public double Lat { get; set; }
        public double Lon { get; set; }
    }

    public sealed class ColourSet
    {
        public int[] Background { get; set; } = { 105, 105, 105 };
        public int[] Runway { get; set; } = { 40, 40, 40 };
        public int[] Taxiway { get; set; } = { 60, 60, 60 };
        public int[] Apron { get; set; } = { 75, 75, 75 };
        public int[] Building { get; set; } = { 90, 90, 90 };
        public int[] Other { get; set; } = { 150, 150, 150 };
        public int[] Arrival { get; set; } = { 255, 255, 160 };
        public int[] Departure { get; set; } = { 139, 220, 243 };
        public int[] Unknown { get; set; } = { 255, 156, 55 };
        public int[] Local { get; set; } = { 255, 100, 255 };
        public int[] WindowBackground { get; set; } = { 160, 170, 170 };
        public int[] DepressedButton { get; set; } = { 130, 146, 146 };
        public int[] SelectedButton { get; set; } = { 0, 0, 96 };
        public int[] InteractiveText { get; set; } = { 0, 0, 96 };
        public int[] GenericText { get; set; } = { 96, 0, 0 };
        public int[] Release { get; set; } = { 52, 59, 198 };
        public int[] Crossing { get; set; } = { 255, 0, 0 };
        /// <summary>All Custom colours from Colours.xml by name — vatSys resolves any
        /// map CustomColourName via Colours.GetCustomColorDX.</summary>
        public Dictionary<string, int[]> CustomColours { get; set; } = new();
    }

    // ---- Position discovery ----

    /// <summary>
    /// Reads Positions.xml and returns all ASMGCS type positions.
    /// </summary>
    public static List<GroundPosition> LoadPositions(string profileDir)
    {
        var result = new List<GroundPosition>();
        var path = Path.Combine(profileDir, "Positions.xml");
        if (!File.Exists(path)) return result;

        try
        {
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root is null) return result;

            // Top-level positions (no group)
            foreach (var elem in root.Elements("Position"))
                TryAddPosition(elem, "", result);

            // Positions inside <Group> elements
            foreach (var group in root.Elements("Group"))
            {
                var groupName = group.Attribute("Name")?.Value ?? "";
                foreach (var elem in group.Elements("Position"))
                    TryAddPosition(elem, groupName, result);
            }
        }
        catch { }

        result.Sort((a, b) => string.Compare(a.Airport, b.Airport, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static void TryAddPosition(XElement elem, string group, List<GroundPosition> result)
    {
        var type = elem.Attribute("Type")?.Value ?? "";
        if (!string.Equals(type, "ASMGCS", StringComparison.OrdinalIgnoreCase))
            return;

        var name = elem.Attribute("Name")?.Value ?? "";
        var airport = elem.Attribute("ASMGCSAirport")?.Value ?? name;
        var centerStr = elem.Attribute("DefaultCenter")?.Value ?? "";
        var rangeStr = elem.Attribute("DefaultRange")?.Value ?? "2";
        var rotStr = elem.Attribute("Rotation")?.Value ?? "0";
        var magVarStr = elem.Attribute("MagneticVariation")?.Value ?? "0";

        var (lat, lon) = ParseCoordinate(centerStr);
        double.TryParse(rangeStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var range);
        double.TryParse(rotStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var rot);
        double.TryParse(magVarStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var magVar);

        if (range <= 0) range = 2;

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

        result.Add(new GroundPosition
        {
            Name = name,
            Airport = airport,
            Group = group,
            CenterLat = lat,
            CenterLon = lon,
            DefaultRange = range,
            Rotation = rot,
            MagneticVariation = magVar,
            MapRefs = mapRefs,
        });
    }

    // ---- Map loading ----

    /// <summary>
    /// Loads the full airport map for a position, resolving all map references.
    /// </summary>
    public static AirportMap? LoadAirportMap(string profileDir, GroundPosition position)
    {
        var mapsDir = Path.Combine(profileDir, "Maps");
        if (!Directory.Exists(mapsDir)) return null;

        var map = new AirportMap
        {
            Icao = position.Airport,
            CenterLat = position.CenterLat,
            CenterLon = position.CenterLon,
            DefaultRange = position.DefaultRange,
            Rotation = position.Rotation,
            MagneticVariation = position.MagneticVariation,
        };

        // Resolve map references to actual files.
        // Refs like "Sydney/ASMGCS_YSSY_RWY" may point to "Sydney/ASMGCS_YSSY.xml"
        // which contains multiple <Map> elements (one per layer).
        var loadedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapRef in position.MapRefs)
        {
            var relativePath = mapRef.Replace('/', Path.DirectorySeparatorChar) + ".xml";
            var fullPath = Path.Combine(mapsDir, relativePath);

            if (!File.Exists(fullPath))
            {
                // Try stripping the layer suffix (_RWY, _TWY, _APR, _BLD, _INF, etc)
                var refName = Path.GetFileNameWithoutExtension(relativePath);
                var lastUnderscore = refName.LastIndexOf('_');
                if (lastUnderscore > 0)
                {
                    var baseName = refName.Substring(0, lastUnderscore);
                    var dir = Path.GetDirectoryName(relativePath) ?? "";
                    var altPath = Path.Combine(mapsDir, dir, baseName + ".xml");
                    if (File.Exists(altPath))
                        fullPath = altPath;
                }
            }

            if (!File.Exists(fullPath))
            {
                // Search by filename
                var fileName = Path.GetFileName(relativePath);
                var found = Directory.GetFiles(mapsDir, fileName, SearchOption.AllDirectories);
                if (found.Length == 0)
                {
                    // Try base name without suffix
                    var refName = Path.GetFileNameWithoutExtension(relativePath);
                    var lastUnderscore = refName.LastIndexOf('_');
                    if (lastUnderscore > 0)
                    {
                        var baseName = refName.Substring(0, lastUnderscore) + ".xml";
                        found = Directory.GetFiles(mapsDir, baseName, SearchOption.AllDirectories);
                    }
                }
                if (found.Length > 0)
                    fullPath = found[0];
                else
                    continue;
            }

            // Avoid loading the same file multiple times
            var canonical = Path.GetFullPath(fullPath);
            if (!loadedFiles.Add(canonical)) continue;

            // Derive folder from mapRef (e.g. "Adelaide" from "Adelaide/ASMGCS_YPAD_RWY")
            var slashIdx = mapRef.IndexOf('/');
            var folder = slashIdx > 0 ? mapRef.Substring(0, slashIdx) : "";

            LoadMapFile(fullPath, map.Layers, folder);
        }

        // If no map refs found, try loading by ICAO pattern
        if (map.Layers.Count == 0)
        {
            var pattern = "ASMGCS_" + position.Airport + "*.xml";
            var files = Directory.GetFiles(mapsDir, pattern, SearchOption.AllDirectories);
            foreach (var file in files)
            {
                // Derive folder from relative path under Maps dir
                var rel = file.Substring(mapsDir.Length).TrimStart(Path.DirectorySeparatorChar);
                var sepIdx = rel.IndexOf(Path.DirectorySeparatorChar);
                var fallbackFolder = sepIdx > 0 ? rel.Substring(0, sepIdx) : "";
                LoadMapFile(file, map.Layers, fallbackFolder);
            }
        }

        // Load global ground maps (e.g. ASMGCS_TAXI_ALL.xml) that apply to all positions
        var globalPattern = "ASMGCS_TAXI_ALL*.xml";
        var globalFiles = Directory.GetFiles(mapsDir, globalPattern, SearchOption.AllDirectories);
        foreach (var gf in globalFiles)
        {
            var canonical = Path.GetFullPath(gf);
            if (!loadedFiles.Add(canonical)) continue;
            var rel = gf.Substring(mapsDir.Length).TrimStart(Path.DirectorySeparatorChar);
            var sepIdx = rel.IndexOf(Path.DirectorySeparatorChar);
            var gFolder = sepIdx > 0 ? rel.Substring(0, sepIdx) : "";
            LoadMapFile(gf, map.Layers, gFolder);
        }

        // Sort by priority descending (matches vatSys: higher priority drawn first = behind)
        map.Layers.Sort((a, b) => b.Priority.CompareTo(a.Priority));

        return map.Layers.Count > 0 ? map : null;
    }

    /// <summary>
    /// Scans ALL XML files under Maps/ and returns metadata for every Ground-category map.
    /// Matches vatSys DisplayMaps.LoadMaps() which scans all XMLs in Maps/ recursively.
    /// </summary>
    public static List<GroundMapMeta> ScanAllGroundMaps(string profileDir)
    {
        var result = new List<GroundMapMeta>();
        var mapsDir = Path.Combine(profileDir, "Maps");
        if (!Directory.Exists(mapsDir)) return result;

        try
        {
            var files = Directory.GetFiles(mapsDir, "*.xml", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                try
                {
                    // Derive folder from directory name (matches vatSys)
                    // If file is directly in Maps/, folder = ""
                    var dirName = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
                    var folder = string.Equals(dirName, "Maps", StringComparison.OrdinalIgnoreCase) ? "" : dirName;

                    var doc = XDocument.Load(file);
                    var root = doc.Root;
                    if (root is null) continue;

                    foreach (var mapElem in root.Elements("Map"))
                    {
                        var type = mapElem.Attribute("Type")?.Value ?? "";
                        // Ground-category: Type starts with "Ground_" (matches vatSys MapTypes >= Ground_RWY)
                        if (!type.StartsWith("Ground_", StringComparison.OrdinalIgnoreCase)) continue;

                        var name = mapElem.Attribute("Name")?.Value ?? "";
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        result.Add(new GroundMapMeta { Folder = folder, Name = name, Type = type });
                    }
                }
                catch { }
            }
        }
        catch { }

        // Sort by folder then name (matches vatSys menu order)
        result.Sort((a, b) =>
        {
            var fc = string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase);
            return fc != 0 ? fc : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    /// <summary>
    /// Loads geometry for specific maps identified by folder+name pairs.
    /// Scans all XML files to find the matching Map elements.
    /// </summary>
    public static List<MapLayer> LoadSpecificLayers(string profileDir, List<string> mapKeys)
    {
        var result = new List<MapLayer>();
        if (mapKeys.Count == 0) return result;

        var mapsDir = Path.Combine(profileDir, "Maps");
        if (!Directory.Exists(mapsDir)) return result;

        // Build a set of (folder, name) pairs we need
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in mapKeys)
            needed.Add(key); // key format: "Folder/Name" or just "Name"

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
                        if (!type.StartsWith("Ground_", StringComparison.OrdinalIgnoreCase)) continue;

                        var name = mapElem.Attribute("Name")?.Value ?? "";
                        var key = string.IsNullOrEmpty(folder) ? name : folder + "/" + name;
                        if (!needed.Contains(key)) continue;

                        needed.Remove(key);
                        var priority = int.TryParse(mapElem.Attribute("Priority")?.Value, out var p) ? p : 99;
                        var customColour = mapElem.Attribute("CustomColourName")?.Value ?? "";
                        var layer = new MapLayer { Type = type, Name = name, Folder = folder, Priority = priority, CustomColour = customColour };

                        ParseMapGeometry(mapElem, layer);

                        if (layer.Infills.Count > 0 || layer.Lines.Count > 0 || layer.Labels.Count > 0)
                            result.Add(layer);
                    }
                }
                catch { }
            }
        }
        catch { }

        // Sort by priority descending (matches vatSys rendering order)
        result.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        return result;
    }

    private static void LoadMapFile(string filePath, List<MapLayer> layers, string folder = "")
    {
        try
        {
            var doc = XDocument.Load(filePath);
            var root = doc.Root;
            if (root is null) return;

            foreach (var mapElem in root.Elements("Map"))
            {
                var type = mapElem.Attribute("Type")?.Value ?? "";
                var name = mapElem.Attribute("Name")?.Value ?? "";
                var priority = int.TryParse(mapElem.Attribute("Priority")?.Value, out var p) ? p : 99;
                var customColour = mapElem.Attribute("CustomColourName")?.Value ?? "";

                var layer = new MapLayer { Type = type, Name = name, Folder = folder, Priority = priority, CustomColour = customColour };
                ParseMapGeometry(mapElem, layer);

                if (layer.Infills.Count > 0 || layer.Lines.Count > 0 || layer.Labels.Count > 0)
                    layers.Add(layer);
            }
        }
        catch { }
    }

    private static void ParseMapGeometry(XElement mapElem, MapLayer layer)
    {
        // vatSys: lines and infills without their own Pattern inherit the Map's Pattern
        var mapPattern = mapElem.Attribute("Pattern")?.Value ?? "";

        // vatSys LoadMaps uses GetElementsByTagName, which searches ALL descendants —
        // maps like ASMGCS.xml nest <Line> elements inside <Label>. Use Descendants
        // throughout to match.

        // Infills (filled polygons)
        foreach (var infillElem in mapElem.Descendants("Infill"))
        {
            var infillName = infillElem.Attribute("Name")?.Value ?? "";
            var infillType = infillElem.Attribute("Type")?.Value ?? "";
            var infillPattern = infillElem.Attribute("Pattern")?.Value ?? mapPattern;
            var raw = infillElem.Element("Point")?.Value ?? infillElem.Value;
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var polygon = ParsePolygonPoints(infillName, raw);
            polygon.InfillType = infillType;
            polygon.Pattern = infillPattern;
            if (polygon.Points.Count >= 3)
                layer.Infills.Add(polygon);
        }

        // Lines — slash-separated coordinates either wrapped in a <Point> child
        // or as the element's direct text (vatSys reads InnerText)
        foreach (var lineElem in mapElem.Descendants("Line"))
        {
            var lineName = lineElem.Attribute("Name")?.Value ?? "";
            var linePattern = lineElem.Attribute("Pattern")?.Value ?? mapPattern;
            var lineWidth = double.TryParse(lineElem.Attribute("Width")?.Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 1.0;
            var raw = lineElem.Element("Point")?.Value ?? lineElem.Value;
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var line = ParseLinePoints(lineName, raw);
            line.Pattern = linePattern;
            line.Width = lineWidth;
            if (line.Points.Count >= 2)
                layer.Lines.Add(line);
        }

        // Labels
        foreach (var labelElem in mapElem.Descendants("Label"))
        {
            foreach (var ptElem in labelElem.Elements("Point"))
            {
                var text = ptElem.Attribute("Name")?.Value ?? "";
                if (string.IsNullOrWhiteSpace(text)) continue;

                var (lat, lon) = ParseCoordinate(ptElem.Value.Trim());
                if (lat != 0 || lon != 0)
                {
                    layer.Labels.Add(new MapLabel { Text = text, Lat = lat, Lon = lon });
                }
            }
        }
    }

    // ---- Colours ----

    public static ColourSet LoadColours(string profileDir)
    {
        var colours = new ColourSet();
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

                // ids match the vatsys.Colours.Identities enum names; vatSys also
                // accepts the historical "WindowBackgound" misspelling.
                switch (id)
                {
                    case "GroundBackground": colours.Background = rgb; break;
                    case "GroundRunway": colours.Runway = rgb; break;
                    case "GroundTaxiway": colours.Taxiway = rgb; break;
                    case "GroundApron": colours.Apron = rgb; break;
                    case "GroundBuilding": colours.Building = rgb; break;
                    case "GroundOther": colours.Other = rgb; break;
                    case "GroundArrival": colours.Arrival = rgb; break;
                    case "GroundDeparture": colours.Departure = rgb; break;
                    case "GroundUnknown": colours.Unknown = rgb; break;
                    case "GroundLocal": colours.Local = rgb; break;
                    case "WindowBackground":
                    case "WindowBackgound": colours.WindowBackground = rgb; break;
                    case "WindowButtonDepressed": colours.DepressedButton = rgb; break;
                    case "WindowButtonSelected": colours.SelectedButton = rgb; break;
                    case "InteractiveText": colours.InteractiveText = rgb; break;
                    case "GenericText": colours.GenericText = rgb; break;
                    case "Custom":
                        var name = elem.Element("Name")?.Value ?? "";
                        if (string.IsNullOrWhiteSpace(name)) break;
                        colours.CustomColours[name] = rgb;
                        if (name.Equals("Release", StringComparison.OrdinalIgnoreCase)) colours.Release = rgb;
                        else if (name.Equals("Crossing", StringComparison.OrdinalIgnoreCase)) colours.Crossing = rgb;
                        break;
                }
            }
        }
        catch { }

        return colours;
    }

    // ---- Coordinate parsing ----

    private static int[]? ReadRgb(XElement elem)
    {
        if (!int.TryParse(elem.Element("R")?.Value, out var r)) return null;
        if (!int.TryParse(elem.Element("G")?.Value, out var g)) return null;
        if (!int.TryParse(elem.Element("B")?.Value, out var b)) return null;
        return new[] { r, g, b };
    }

    private static InfillPolygon ParsePolygonPoints(string name, string raw)
    {
        var poly = new InfillPolygon { Name = name };
        foreach (var part in raw.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            var (lat, lon) = ParseCoordinate(trimmed);
            if (lat != 0 || lon != 0)
                poly.Points.Add(new[] { Math.Round(lat, 8), Math.Round(lon, 8) });
        }
        return poly;
    }

    private static MapLine ParseLinePoints(string name, string raw)
    {
        var line = new MapLine { Name = name };
        foreach (var part in raw.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            var (lat, lon) = ParseCoordinate(trimmed);
            if (lat != 0 || lon != 0)
                line.Points.Add(new[] { Math.Round(lat, 8), Math.Round(lon, 8) });
        }
        return line;
    }

    /// <summary>
    /// Parses vatSys coordinate strings. Supports three formats:
    /// DMS:      ±DDMMSS.SSS±DDDMMSS.SSS  (e.g. "-335646.000+1511038.000")
    /// Deg+Min:  ±DDMM.MMMM±DDDMM.MMMM   (e.g. "-2738.6753+15242.788")
    /// Decimal:  ±DD.DDDDDD±DDD.DDDDDD     (e.g. "-33.950833+151.179722")
    /// </summary>
    public static (double lat, double lon) ParseCoordinate(string s)
    {
        s = s.Trim();
        if (s.Length < 3) return (0, 0);

        int lonStart = -1;
        for (int i = 1; i < s.Length; i++)
        {
            if (s[i] == '+' || s[i] == '-')
            {
                lonStart = i;
                break;
            }
        }

        if (lonStart < 0) return (0, 0);

        var latStr = s.Substring(0, lonStart);
        var lonStr = s.Substring(lonStart);

        if (IsDecimalDegrees(latStr))
        {
            if (!double.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) return (0, 0);
            if (!double.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) return (0, 0);
            return (lat, lon);
        }

        return (ParseDms(latStr), ParseDms(lonStr));
    }

    /// <summary>
    /// Decimal degrees have at most 3 digits before the decimal point (e.g. -33.95).
    /// DMS has 5+ digits before the decimal point (e.g. -335646.000).
    /// </summary>
    private static bool IsDecimalDegrees(string s)
    {
        var start = 0;
        if (s.Length > 0 && (s[0] == '-' || s[0] == '+')) start = 1;
        var dotIdx = s.IndexOf('.');
        var intDigits = (dotIdx >= 0 ? dotIdx : s.Length) - start;
        return intDigits <= 3;
    }

    private static double ParseDms(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return 0;

        var sign = 1.0;
        if (s[0] == '-') { sign = -1; s = s.Substring(1); }
        else if (s[0] == '+') { s = s.Substring(1); }

        var dotIdx = s.IndexOf('.');
        var intPart = dotIdx >= 0 ? s.Substring(0, dotIdx) : s;
        var fracPart = dotIdx >= 0 ? s.Substring(dotIdx) : "";

        if (intPart.Length < 4) return 0;

        if (intPart.Length <= 5)
        {
            // DDMM.MMMM or DDDMM.MMMM format (degrees + decimal minutes)
            // 4 digits = 2-digit degree (latitude), 5 digits = 3-digit degree (longitude)
            var degLen = intPart.Length - 2;
            if (!int.TryParse(intPart.Substring(0, degLen), NumberStyles.None, CultureInfo.InvariantCulture, out var deg)) return 0;
            if (!double.TryParse(intPart.Substring(degLen, 2) + fracPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var mins)) return 0;
            return sign * (deg + mins / 60.0);
        }

        // DDMMSS.SSS or DDDMMSS.SSS format (6+ digits)
        var mmStart = intPart.Length - 4;
        var ddPart = intPart.Substring(0, mmStart);

        if (!int.TryParse(ddPart, NumberStyles.None, CultureInfo.InvariantCulture, out var degrees)) return 0;
        if (!int.TryParse(intPart.Substring(mmStart, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)) return 0;
        if (!double.TryParse(intPart.Substring(mmStart + 2, 2) + fracPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return 0;

        return sign * (degrees + minutes / 60.0 + seconds / 3600.0);
    }
}
