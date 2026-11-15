using System;
using System.Collections.Generic;
using vatsys;

namespace VatSysExternalWindows;

// ---- Map DTOs served to the web UI ----

/// <summary>Lightweight map metadata for the Maps menu (no geometry).</summary>
public sealed class MapMeta
{
    public string Folder { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    /// <summary>The map's own Center attribute, or 0/0 when it has none.</summary>
    public double CenterLat { get; set; }
    public double CenterLon { get; set; }
}

public sealed class MapLayer
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public int Priority { get; set; }
    public string CustomColour { get; set; } = "";
    public List<MapInfill> Infills { get; set; } = new();
    public List<MapLine> Lines { get; set; } = new();
    public List<MapLabel> Labels { get; set; } = new();
    public List<MapSymbol> Symbols { get; set; } = new();
    public List<MapRunway> Runways { get; set; } = new();
}

public sealed class MapInfill
{
    public string Name { get; set; } = "";
    /// <summary>"Normal", "Background" or "None". Background infills paint in the
    /// window's background colour.</summary>
    public string InfillType { get; set; } = "";
    /// <summary>Non-solid patterns fill with the Infill/Background checkerboard on
    /// the air window.</summary>
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
    /// <summary>Leader direction: N, NE, E, SE, S, SW, W or NW.</summary>
    public string Dir { get; set; } = "NE";
    /// <summary>Left, Center or Right.</summary>
    public string Align { get; set; } = "Left";
    /// <summary>Top, Middle or Bottom.</summary>
    public string VAlign { get; set; } = "Bottom";
    public bool Leader { get; set; } = true;
    /// <summary>vatsys.BorderFlags bits: 1=Left 2=Right 4=Top 8=Bottom.</summary>
    public int Border { get; set; }
}

public sealed class MapSymbol
{
    public string SymbolType { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
}

public sealed class MapRunway
{
    public string Name { get; set; } = "";
    public double[] P1 { get; set; } = { 0, 0 };
    public double[] P2 { get; set; } = { 0, 0 };
    public List<MapCentreline> Centrelines { get; set; } = new();
}

public sealed class MapCentreline
{
    public double[] From { get; set; } = { 0, 0 };
    public double[] To { get; set; } = { 0, 0 };
    /// <summary>Extended centreline track in degrees true (ticks are drawn across it).</summary>
    public double Track { get; set; }
    public List<double[]> Ticks { get; set; } = new();
}

/// <summary>
/// Serves map metadata and geometry straight from vatsys.DisplayMaps — the maps
/// vatSys itself parsed from the profile. That keeps airway lines, navaid and
/// bearing/range points, nearest-duplicate fix resolution, great-circle
/// segments, labels, symbols and runways identical to what vatSys draws,
/// instead of re-implementing its XML loader.
/// </summary>
public static class MapSource
{
    /// <summary>"Folder/Name", or just "Name" for maps directly under Maps\.</summary>
    public static string Key(DisplayMaps.Map map)
    {
        return string.IsNullOrEmpty(map.Folder) ? map.Name : map.Folder + "/" + map.Name;
    }

    /// <summary>Metadata for every map of a category, sorted by folder then name.
    /// Empty until vatSys has finished loading the profile's maps.</summary>
    public static List<MapMeta> ListMaps(DisplayMaps.MapCategories category)
    {
        var result = new List<MapMeta>();
        foreach (var map in Snapshot())
        {
            if (map.Category != category || string.IsNullOrWhiteSpace(map.Name)) continue;

            var meta = new MapMeta { Folder = map.Folder ?? "", Name = map.Name, Type = map.Type.ToString() };

            // Maps without a Center attribute default to the main ASD centre —
            // report those as "no centre" so the UI doesn't recentre on them.
            Coordinate? centre = null;
            try { centre = map.Center; } catch { }
            Coordinate? mainCentre = null;
            try { mainCentre = MMI.MainASDCentre; } catch { }
            if (centre is not null && !ReferenceEquals(centre, mainCentre) && IsValid(centre))
            {
                meta.CenterLat = Math.Round(centre.Latitude, 7);
                meta.CenterLon = Math.Round(centre.Longitude, 7);
            }

            result.Add(meta);
        }

        result.Sort((a, b) =>
        {
            var fc = string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase);
            return fc != 0 ? fc : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    /// <summary>Geometry for the maps identified by "Folder/Name" keys, sorted by
    /// priority descending (vatSys draws higher priority first, i.e. behind).</summary>
    public static List<MapLayer> GetLayers(DisplayMaps.MapCategories category, IEnumerable<string> keys)
    {
        var result = new List<MapLayer>();
        var needed = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        if (needed.Count == 0) return result;

        foreach (var map in Snapshot())
        {
            if (needed.Count == 0) break;
            if (map.Category != category || string.IsNullOrEmpty(map.Name)) continue;
            if (!needed.Remove(Key(map))) continue;

            var layer = ToLayer(map);
            if (layer is not null) result.Add(layer);
        }

        result.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        return result;
    }

    /// <summary>Converts a vatSys map to its DTO, or null when it has nothing to draw.</summary>
    public static MapLayer? ToLayer(DisplayMaps.Map? map)
    {
        if (map is null) return null;

        var layer = new MapLayer
        {
            Type = map.Type.ToString(),
            Name = map.Name ?? "",
            Folder = map.Folder ?? "",
            Priority = map.Priority,
            CustomColour = map.CustomColourName ?? "",
        };

        foreach (var inf in map.Infills)
        {
            if (inf?.Points is null || inf.Points.Count < 3) continue;
            layer.Infills.Add(new MapInfill
            {
                Name = inf.Name ?? "",
                InfillType = inf.Type.ToString(),
                Pattern = inf.Pattern.ToString(),
                Points = ToPoints(inf.Points),
            });
        }

        foreach (var ln in map.Lines)
        {
            if (ln?.Points is null || ln.Points.Count < 2) continue;
            layer.Lines.Add(new MapLine
            {
                Name = ln.Name ?? "",
                Pattern = ln.Pattern.ToString(),
                Width = ln.Width,
                Points = ToPoints(ln.Points),
            });
        }

        foreach (var lb in map.Labels)
        {
            if (lb is null || !IsValid(lb.Location) || string.IsNullOrEmpty(lb.Name)) continue;
            layer.Labels.Add(new MapLabel
            {
                Text = lb.Name,
                Lat = Math.Round(lb.Location.Latitude, 7),
                Lon = Math.Round(lb.Location.Longitude, 7),
                Dir = lb.Direction.ToString(),
                Align = lb.Alignment.ToString(),
                VAlign = lb.VerticalAlignment.ToString(),
                Leader = lb.Leader,
                Border = (int)lb.Border,
            });
        }

        foreach (var sym in map.Symbols)
        {
            if (sym is null || !IsValid(sym.Location)) continue;
            layer.Symbols.Add(new MapSymbol
            {
                SymbolType = sym.Type.ToString(),
                Lat = Math.Round(sym.Location.Latitude, 7),
                Lon = Math.Round(sym.Location.Longitude, 7),
            });
        }

        foreach (var rwy in map.Runways)
        {
            if (rwy is null || !IsValid(rwy.Threshold1) || !IsValid(rwy.Threshold2)) continue;
            var runway = new MapRunway
            {
                Name = rwy.Name ?? "",
                P1 = ToPoint(rwy.Threshold1),
                P2 = ToPoint(rwy.Threshold2),
            };
            AddCentreline(runway, rwy.Threshold1, rwy.ExtendedCentrelineLength1, rwy.ExtendedCentrelineTrack1, rwy.TickInterval1);
            AddCentreline(runway, rwy.Threshold2, rwy.ExtendedCentrelineLength2, rwy.ExtendedCentrelineTrack2, rwy.TickInterval2);
            layer.Runways.Add(runway);
        }

        var hasGeometry = layer.Infills.Count > 0 || layer.Lines.Count > 0 || layer.Labels.Count > 0 ||
                          layer.Symbols.Count > 0 || layer.Runways.Count > 0;
        return hasGeometry ? layer : null;
    }

    /// <summary>Extended centreline and its tick marks, positioned exactly as
    /// ASDControlDX.PaintMap does (bearing/range from the threshold).</summary>
    private static void AddCentreline(MapRunway runway, Coordinate threshold, int lengthNm, double track, int tickNm)
    {
        if (lengthNm <= 0) return;

        try
        {
            var end = Conversions.CalculateLLFromBearingRange(threshold, lengthNm, track);
            if (!IsValid(end)) return;

            var line = new MapCentreline { From = ToPoint(threshold), To = ToPoint(end), Track = track };
            if (tickNm > 0)
            {
                for (var d = tickNm; d <= lengthNm; d += tickNm)
                {
                    var tick = Conversions.CalculateLLFromBearingRange(threshold, d, track);
                    if (IsValid(tick)) line.Ticks.Add(ToPoint(tick));
                }
            }
            runway.Centrelines.Add(line);
        }
        catch { }
    }

    /// <summary>Copies the map list so a profile (re)load on another thread can't
    /// invalidate the enumeration. Empty until vatSys has loaded its maps.</summary>
    private static DisplayMaps.Map[] Snapshot()
    {
        try
        {
            if (!DisplayMaps.Loaded) return Array.Empty<DisplayMaps.Map>();
            return DisplayMaps.Maps.ToArray();
        }
        catch
        {
            return Array.Empty<DisplayMaps.Map>();
        }
    }

    private static List<double[]> ToPoints(List<Coordinate> coords)
    {
        var points = new List<double[]>(coords.Count);
        foreach (var c in coords)
        {
            if (IsValid(c)) points.Add(ToPoint(c));
        }
        return points;
    }

    private static double[] ToPoint(Coordinate c)
    {
        return new[] { Math.Round(c.Latitude, 7), Math.Round(c.Longitude, 7) };
    }

    private static bool IsValid(Coordinate? c)
    {
        if (c is null) return false;
        var lat = c.Latitude;
        var lon = c.Longitude;
        if (double.IsNaN(lat) || double.IsNaN(lon) || double.IsInfinity(lat) || double.IsInfinity(lon)) return false;
        return lat != 0 || lon != 0;
    }
}
