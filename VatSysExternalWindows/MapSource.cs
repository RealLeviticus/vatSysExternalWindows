using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    /// <summary>The map's index in DisplayMaps.Maps. vatSys paints the visible maps
    /// in that list's order (sorted once by priority when the profile loads).</summary>
    public int Order { get; set; }
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

    /// <summary>Metadata for every map of a category, sorted by folder then name as
    /// the vatSys Maps menu sorts them. Empty until vatSys has finished loading the
    /// profile's maps.</summary>
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
            if (centre is not null && !ReferenceEquals(centre, mainCentre) && IsValid(centre) &&
                (centre.Latitude != 0 || centre.Longitude != 0))
            {
                meta.CenterLat = Math.Round(centre.Latitude, 7);
                meta.CenterLon = Math.Round(centre.Longitude, 7);
            }

            result.Add(meta);
        }

        // The menu uses LINQ orderby on the strings (the current culture's order)
        return result.OrderBy(m => m.Folder).ThenBy(m => m.Name).ToList();
    }

    /// <summary>Geometry for the maps identified by "Folder/Name" keys, whatever
    /// their category: a position may list maps of the other window type and vatSys
    /// paints those too. Each layer carries its paint order.</summary>
    public static List<MapLayer> GetLayers(IEnumerable<string> keys)
    {
        var result = new List<MapLayer>();
        var needed = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        if (needed.Count == 0) return result;

        var maps = Snapshot();
        for (var i = 0; i < maps.Length && needed.Count > 0; i++)
        {
            var map = maps[i];
            if (map is null || string.IsNullOrEmpty(map.Name)) continue;
            if (!needed.Remove(Key(map))) continue;

            // A map with nothing to draw is still returned, so the page stops asking for it
            var layer = ToLayer(map) ?? new MapLayer
            {
                Type = map.Type.ToString(), Name = map.Name, Folder = map.Folder ?? "",
                Priority = map.Priority, CustomColour = map.CustomColourName ?? "",
            };
            layer.Order = i;
            result.Add(layer);
        }

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

    /// <summary>Usable in JSON. A fix name vatSys could not resolve stays in the
    /// geometry as 0N 0E, exactly as vatSys draws it.</summary>
    private static bool IsValid(Coordinate? c)
    {
        if (c is null) return false;
        var lat = c.Latitude;
        var lon = c.Longitude;
        return !(double.IsNaN(lat) || double.IsNaN(lon) || double.IsInfinity(lat) || double.IsInfinity(lon));
    }
}

/// <summary>A logical position as vatSys loaded it from Positions.xml.</summary>
public sealed class PositionDto
{
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    /// <summary>ASMGCS positions: the airport the Ground window is for.</summary>
    public string Airport { get; set; } = "";
    public double CenterLat { get; set; }
    public double CenterLon { get; set; }
    public int DefaultRange { get; set; }
    public double Rotation { get; set; }
    public double MagneticVariation { get; set; }
    /// <summary>Keys of the maps the position shows.</summary>
    public List<string> MapRefs { get; set; } = new();
}

/// <summary>Positions and runtime maps, read from vatSys's own state.</summary>
public static class PositionSource
{
    /// <summary>The positions of one type, in the order of the vatSys Position menu:
    /// by name within each group (the page builds the group submenus).</summary>
    public static List<PositionDto> List(LogicalPositions.PositionTypes type)
    {
        var result = new List<PositionDto>();
        try
        {
            foreach (var p in LogicalPositions.Positions.Where(p => p != null && p.Type == type).OrderBy(p => p.Name).ToList())
            {
                var centre = p.DefaultCenter;
                var dto = new PositionDto
                {
                    Name = p.Name ?? "",
                    Group = p.Group ?? "",
                    Airport = p.ASMGCSAirport?.ICAOName ?? "",
                    CenterLat = centre?.Latitude ?? 0,
                    CenterLon = centre?.Longitude ?? 0,
                    DefaultRange = p.DefaultRange,
                    Rotation = p.Rotation,
                    MagneticVariation = p.MagneticVariation,
                };
                if (p.Maps != null)
                {
                    foreach (var map in p.Maps)
                    {
                        if (map != null && !string.IsNullOrEmpty(map.Name)) dto.MapRefs.Add(MapSource.Key(map));
                    }
                }
                result.Add(dto);
            }
        }
        catch { }
        return result;
    }
}

/// <summary>Runtime maps vatSys paints on its windows, in vatSys's paint order.</summary>
public sealed class DynamicMaps
{
    /// <summary>Painted before the file maps (ASD only): the controlled-sector
    /// infill, when the user's Dynamic Infill setting is on, then the active
    /// restricted-area infills.</summary>
    public List<MapLayer> Below { get; set; } = new();
    /// <summary>Painted after the file maps (ASD and Ground): active
    /// restricted-area outlines.</summary>
    public List<MapLayer> Above { get; set; } = new();

    private static volatile DynamicMaps _snapshot = new();

    /// <summary>The dynamic maps as vatSys currently has them. They belong to the
    /// GUI thread, so they are read there; if the GUI is busy the previous
    /// snapshot is served.</summary>
    public static DynamicMaps Get()
    {
        GuiThread.Run(() => _snapshot = Compute());
        return _snapshot;
    }

    private static DynamicMaps Compute()
    {
        var result = new DynamicMaps();
        var infillOn = true;
        try { infillOn = MMI.DynamicInfill; } catch { }
        if (infillOn)
        {
            try { Add(result.Below, DisplayMaps.DynamicInfill); } catch { }
        }
        try { Add(result.Below, DisplayMaps.DynamicRestrictedAreaInfills); } catch { }
        try { Add(result.Above, DisplayMaps.DynamicRestrictedAreas); } catch { }
        return result;
    }

    private static void Add(List<MapLayer> list, DisplayMaps.Map? map)
    {
        var layer = MapSource.ToLayer(map);
        if (layer is not null) list.Add(layer);
    }
}

/// <summary>Runs work on the vatSys GUI thread and waits briefly for it, so an
/// HTTP worker can serve state that belongs to that thread (open forms, the
/// dynamic maps) as it is now rather than as it was one poll ago.</summary>
internal static class GuiThread
{
    private const int WaitMs = 300;

    /// <summary>Queues <paramref name="work"/> on the GUI thread and waits up to
    /// 300 ms for it. Returns false if it had not finished (GUI busy, or no main
    /// window yet); the work may still complete later.</summary>
    public static bool Run(Action work)
    {
        // Deliberately not disposed: the queued delegate may signal after the wait gave up.
        var done = new ManualResetEventSlim(false);
        try
        {
            MMI.InvokeOnGUI(delegate
            {
                try { work(); }
                catch { }
                finally { try { done.Set(); } catch { } }
            });
            return done.Wait(WaitMs);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs <paramref name="work"/> on the GUI thread and returns its
    /// result, or <paramref name="fallback"/> if it had not finished within
    /// <paramref name="waitMs"/> (the work may still complete later).</summary>
    public static T Call<T>(Func<T> work, int waitMs, T fallback)
    {
        var done = new ManualResetEventSlim(false);
        var box = new StrongBox<T>(fallback);
        var finished = false;
        try
        {
            MMI.InvokeOnGUI(delegate
            {
                try { box.Value = work(); Volatile.Write(ref finished, true); }
                catch { }
                finally { try { done.Set(); } catch { } }
            });
            return done.Wait(waitMs) && Volatile.Read(ref finished) ? box.Value! : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
