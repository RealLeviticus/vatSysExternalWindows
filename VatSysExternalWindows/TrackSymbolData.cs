using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using vatsys;

namespace VatSysExternalWindows;

/// <summary>
/// Parses the profile's Tracks.xml track-symbol definitions exactly as
/// vatsys.TrackSymbols.LoadFile does: the profile file first, with any symbol
/// types it doesn't define pulled from DefaultTracks.xml in the common data
/// folder. Geometry, stroke widths and rotation flags are served to the web
/// UI so it can render the same symbols vatSys draws.
/// </summary>
public static class TrackSymbolData
{
    // TrackSymbols.Types names, in enum order. Ground* symbols default to
    // rotate-to-track (matches ASDControlDX geometry realisation defaults).
    private static readonly string[] SymbolTypes =
    {
        "Primary", "ModeA", "ModeS", "ADSB", "ADSC", "ADSCExtrapolated",
        "FlightPlan", "CoastRadar", "CoastADSB", "Cancelled", "HistoryMarker",
        "VelocityMarker", "SRPTimeMarker", "SRPDistanceMarker", "Selection",
        "Ground", "GroundLight", "GroundMedium", "GroundHeavy", "GroundSuper",
    };

    private static readonly HashSet<string> RotateDefaultTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ground", "GroundLight", "GroundMedium", "GroundHeavy", "GroundSuper",
    };

    public sealed class SymbolDef
    {
        public string Type { get; set; } = "";
        /// <summary>SSR code this variant applies to (octal-parsed, -1 = default variant).</summary>
        public int SpecialCode { get; set; } = -1;
        /// <summary>0 = filled geometry realisation; &gt;0 = stroked outline of this width.</summary>
        public double StrokeWidth { get; set; } = 2;
        public bool Rotate { get; set; }
        public List<ShapeDef> Shapes { get; set; } = new();
    }

    public sealed class ShapeDef
    {
        /// <summary>"ellipse", "rect" or "path".</summary>
        public string Kind { get; set; } = "";
        public double[]? Center { get; set; }
        public double RadiusX { get; set; }
        public double RadiusY { get; set; }
        public double[]? TopLeft { get; set; }
        public double[]? BottomRight { get; set; }
        public List<FigureDef>? Figures { get; set; }
    }

    public sealed class FigureDef
    {
        public double[] Start { get; set; } = { 0, 0 };
        /// <summary>FigureBegin="Filled" — participates in fill when StrokeWidth is 0.</summary>
        public bool Filled { get; set; }
        /// <summary>FigureEnd="Closed".</summary>
        public bool Closed { get; set; }
        public List<SegmentDef> Segs { get; set; } = new();
    }

    public sealed class SegmentDef
    {
        /// <summary>"l" = line to P; "b" = cubic bezier through B[0..2].</summary>
        public string T { get; set; } = "l";
        public double[]? P { get; set; }
        public double[][]? B { get; set; }
    }

    public static List<SymbolDef> Load(string profileDir)
    {
        var result = new List<SymbolDef>();

        XDocument? profileDoc = null;
        try
        {
            var path = Path.Combine(profileDir, "Tracks.xml");
            if (File.Exists(path)) profileDoc = XDocument.Load(path);
        }
        catch { }

        XDocument? defaultDoc = null;
        try
        {
            var path = Path.Combine(Helpers.GetCommonDataFolder(), "DefaultTracks.xml");
            if (File.Exists(path)) defaultDoc = XDocument.Load(path);
        }
        catch { }

        if (profileDoc is not null)
            LoadSymbols(profileDoc, result);

        // vatSys backfills any symbol type the profile doesn't define from DefaultTracks.xml
        if (defaultDoc is not null)
        {
            foreach (var type in SymbolTypes)
            {
                if (result.Any(s => s.Type.Equals(type, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var elem = defaultDoc.Root?.Elements("TrackSymbol")
                    .FirstOrDefault(e => string.Equals(e.Attribute("Type")?.Value, type, StringComparison.OrdinalIgnoreCase));
                if (elem is not null)
                {
                    var def = LoadSymbol(elem);
                    if (def is not null) result.Add(def);
                }
            }
        }

        return result;
    }

    private static void LoadSymbols(XDocument doc, List<SymbolDef> result)
    {
        var root = doc.Root;
        if (root is null) return;

        foreach (var elem in root.Elements("TrackSymbol"))
        {
            try
            {
                var def = LoadSymbol(elem);
                if (def is not null) result.Add(def);
            }
            catch { }
        }
    }

    private static SymbolDef? LoadSymbol(XElement elem)
    {
        var type = elem.Attribute("Type")?.Value ?? "";
        if (string.IsNullOrEmpty(type)) return null;

        var def = new SymbolDef { Type = type };

        // vatSys: StrokeWidth defaults to 2; SpecialCode is parsed base-8 (squawk digits)
        var swAttr = elem.Attribute("StrokeWidth")?.Value;
        if (swAttr is not null &&
            double.TryParse(swAttr, NumberStyles.Float, CultureInfo.InvariantCulture, out var sw))
        {
            def.StrokeWidth = sw;
        }

        var codeAttr = elem.Attribute("SpecialCode")?.Value;
        if (!string.IsNullOrEmpty(codeAttr))
        {
            try { def.SpecialCode = Convert.ToInt16(codeAttr, 8); } catch { }
        }

        var rotAttr = elem.Attribute("RotateToTrack")?.Value ?? "";
        def.Rotate = rotAttr.Equals("True", StringComparison.OrdinalIgnoreCase) ||
                     (!rotAttr.Equals("False", StringComparison.OrdinalIgnoreCase) &&
                      RotateDefaultTypes.Contains(type));

        // vatSys reads only the first child geometry element
        var geom = elem.Elements().FirstOrDefault();
        if (geom is null) return null;

        switch (geom.Name.LocalName)
        {
            case "RectangleGeometry":
                def.Shapes.Add(new ShapeDef
                {
                    Kind = "rect",
                    TopLeft = ParsePoint(geom.Attribute("TopLeft")?.Value),
                    BottomRight = ParsePoint(geom.Attribute("BottomRight")?.Value),
                });
                break;

            case "EllipseGeometry":
                def.Shapes.Add(new ShapeDef
                {
                    Kind = "ellipse",
                    Center = ParsePoint(geom.Attribute("Center")?.Value),
                    RadiusX = ParseDouble(geom.Attribute("RadiusX")?.Value),
                    RadiusY = ParseDouble(geom.Attribute("RadiusY")?.Value),
                });
                break;

            case "PathGeometry":
                var shape = new ShapeDef { Kind = "path", Figures = new List<FigureDef>() };
                foreach (var fig in geom.Elements("Figure"))
                {
                    var figure = new FigureDef
                    {
                        Start = ParsePoint(fig.Attribute("Start")?.Value) ?? new double[] { 0, 0 },
                        Filled = string.Equals(fig.Attribute("FigureBegin")?.Value, "Filled", StringComparison.OrdinalIgnoreCase),
                        Closed = string.Equals(fig.Attribute("FigureEnd")?.Value, "Closed", StringComparison.OrdinalIgnoreCase),
                    };

                    foreach (var seg in fig.Elements())
                    {
                        if (seg.Name.LocalName == "Line")
                        {
                            var p = ParsePoint(seg.Value);
                            if (p is not null) figure.Segs.Add(new SegmentDef { T = "l", P = p });
                        }
                        else if (seg.Name.LocalName == "Bezier")
                        {
                            var p1 = ParsePoint(seg.Element("Point1")?.Value);
                            var p2 = ParsePoint(seg.Element("Point2")?.Value);
                            var p3 = ParsePoint(seg.Element("Point3")?.Value);
                            if (p1 is not null && p2 is not null && p3 is not null)
                                figure.Segs.Add(new SegmentDef { T = "b", B = new[] { p1, p2, p3 } });
                        }
                    }

                    shape.Figures.Add(figure);
                }
                def.Shapes.Add(shape);
                break;

            default:
                return null;
        }

        return def;
    }

    private static double[]? ParsePoint(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s!.Split(',');
        if (parts.Length != 2) return null;
        if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return null;
        if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return null;
        return new[] { x, y };
    }

    private static double ParseDouble(string? s)
    {
        return double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
