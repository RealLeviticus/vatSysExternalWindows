using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace VatSysExternalWindows;

/// <summary>
/// Parses vatSys profile data for the Ground window: Positions.xml for ASMGCS
/// positions and Colours.xml for the colour scheme. Map geometry comes from
/// vatSys's own loaded maps (see <see cref="MapSource"/>).
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
        /// <summary>Active restricted-area outlines, which vatSys also paints on the Ground window.</summary>
        public int[] PRDArea { get; set; } = { 100, 100, 100 };
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
                    case "PRDArea": colours.PRDArea = rgb; break;
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

    internal static int[]? ReadRgb(XElement elem)
    {
        if (!int.TryParse(elem.Element("R")?.Value, out var r)) return null;
        if (!int.TryParse(elem.Element("G")?.Value, out var g)) return null;
        if (!int.TryParse(elem.Element("B")?.Value, out var b)) return null;
        return new[] { r, g, b };
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
