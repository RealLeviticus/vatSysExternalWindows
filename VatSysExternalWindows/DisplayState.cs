using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using vatsys;

namespace VatSysExternalWindows;

/// <summary>vatSys's colour table as its windows use it.</summary>
internal static class VatColours
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>Colours.GetColourDX: the colour the ASD/Ground renderer paints with,
    /// with the user's brightness settings applied (DarkMagenta when undefined).</summary>
    public static Color Dx(Colours.Identities id)
    {
        try
        {
            var c = Colours.GetColourDX(id);
            return Color.FromArgb(c.R, c.G, c.B);
        }
        catch
        {
            return Color.DarkMagenta;
        }
    }

    /// <summary>Colours.GetColour: the unadjusted colour the WinForms chrome (menus,
    /// title bar) uses.</summary>
    public static Color Gdi(Colours.Identities id)
    {
        try { return Colours.GetColour(id); } catch { return SystemColors.ControlDark; }
    }

    /// <summary>Colours.GetCustomColorDX: a named Custom colour (AliceBlue when the
    /// profile doesn't define it).</summary>
    public static Color Custom(string name)
    {
        try
        {
            var c = Colours.GetCustomColorDX(name);
            return Color.FromArgb(c.R, c.G, c.B);
        }
        catch
        {
            return Color.AliceBlue;
        }
    }

    public static int[] Rgb(Color c) => new[] { (int)c.R, (int)c.G, (int)c.B };

    /// <summary>Every colour identity, as the renderer (Dx) and the chrome (Gdi) see
    /// it, plus the profile's named Custom colours.</summary>
    public static object All()
    {
        var dx = new Dictionary<string, int[]>();
        var gdi = new Dictionary<string, int[]>();
        foreach (Colours.Identities id in Enum.GetValues(typeof(Colours.Identities)))
        {
            if (id == Colours.Identities.Default || id == Colours.Identities.Custom || id == Colours.Identities.State) continue;
            dx[id.ToString()] = Rgb(Dx(id));
            gdi[id.ToString()] = Rgb(Gdi(id));
        }

        var custom = new Dictionary<string, int[]>();
        try
        {
            if (typeof(Colours).GetField("customColoursDX", Any)?.GetValue(null) is IDictionary table)
            {
                foreach (var key in table.Keys.Cast<object>().ToList())
                {
                    if (key is string name) custom[name] = Rgb(Custom(name));
                }
            }
        }
        catch { }

        return new { Dx = dx, Gdi = gdi, Custom = custom };
    }
}

/// <summary>
/// The parts of an ASD / Ground window that are not maps or tracks: the user's
/// display settings and the tool graphics vatSys paints from shared state
/// (ATC list, visibility centres, conflict areas, closest approach, graphic routes).
/// </summary>
internal static class DisplayState
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    // ---- Settings ----

    public static object GetSettings()
    {
        return new
        {
            CompassRose = Flag(() => MMI.CompassRose),
            ScaleMarker = Flag(() => MMI.ScaleMarker),
            VisCenters = Flag(() => MMI.VisCenters),
            // A secondary ASD window shows the ATC list under the ATCList2 setting
            AtcList = Flag(() => MMI.ATCList2),
            DynamicInfill = Flag(() => MMI.DynamicInfill),
            // MenuRenderer: "Default" highlights checked items, "Traditional" ticks them
            CheckMarks = StaticMember(typeof(MMI), "MenuCheckMarkStyle")?.ToString() ?? "Default",
            BrlLabelType = StaticMember(typeof(MMI), "BRLLabelType")?.ToString() ?? "Normal",
        };
    }

    private static bool Flag(Func<bool> read)
    {
        try { return read(); } catch { return false; }
    }

    // ---- Tracks ----

    /// <summary>MMI.AircraftTracks ordered the way Render orders them
    /// (OrderBy State, a stable sort).</summary>
    public static List<Track> OrderedTracks()
    {
        try
        {
            if (StaticMember(typeof(MMI), "AircraftTracks") is ConcurrentDictionary<object, Track> tracks)
                return tracks.Values.Where(t => t != null).OrderBy(t => t.State).ToList();
        }
        catch { }
        return new List<Track>();
    }

    // ---- Overlays ----

    public static object GetOverlays(bool ground)
    {
        var tracks = OrderedTracks();
        return new
        {
            Settings = GetSettings(),
            Atc = ground ? null : Try(AtcList),
            Vis = Try(VisibilityCentres),
            Latcs = ground ? null : Try(Latcs),
            Tops = ground ? null : Try(Tops),
            Routes = ground ? null : Try(() => Routes(tracks)),
        };
    }

    private static T? Try<T>(Func<T?> build) where T : class
    {
        try { return build(); } catch { return null; }
    }

    /// <summary>PaintATCList: one row per online controller — the sector name (or
    /// callsign), then its frequencies.</summary>
    private static object AtcList()
    {
        IList<NetworkATC> list = Network.GetOnlineATCs ?? new List<NetworkATC>();
        if (MMI.ATCListFilterNonATC) list = list.Where(a => a.IsRealATC).ToList();

        string DisplayName(NetworkATC atc)
        {
            var callsign = atc.Callsign;
            var sector = SectorsVolumes.Sectors.FirstOrDefault(s => s.Callsign == callsign);
            return sector != null ? sector.Name : callsign;
        }

        var nameWidth = 0;
        foreach (var atc in list)
        {
            var length = (DisplayName(atc) ?? "").Length;
            if (atc.RequestingBreak) length += 4;
            if (length > nameWidth) nameWidth = length;
        }

        var rows = new List<object>();
        foreach (var atc in list)
        {
            var name = DisplayName(atc) ?? "";
            var freq = "";
            var frequencies = atc.Frequencies;
            if (frequencies != null && frequencies.Length > 0 && frequencies[0] != 99998)
                freq = string.Join(",", frequencies.Select(f => Conversions.FSDFrequencyToString(f)));
            if (atc.RequestingBreak) name = "**" + name + "**";
            rows.Add(new { Name = name, Freq = freq });
        }

        return new { NameWidth = nameWidth, Rows = rows };
    }

    /// <summary>PaintVisRange: the visibility centres and the radius of the ring
    /// drawn round each (the radio range, capped by the network range).</summary>
    private static object? VisibilityCentres()
    {
        var network = StaticMember(typeof(Network), "Instance");
        if (network is null) return null;

        var centres = new List<double[]>();
        if (Member(network, "VisibilityCenters") is IEnumerable list)
        {
            foreach (var item in list)
            {
                if (item is Coordinate c) centres.Add(LatLon(c));
            }
        }

        var radioRange = 0;
        var method = network.GetType().GetMethod("GetRadioRange", Any, null, Type.EmptyTypes, null);
        if (method?.Invoke(network, null) is int range) radioRange = range;
        if (Member(network, "Range") is int networkRange && networkRange < radioRange) radioRange = networkRange;

        return new { Centres = centres, Range = radioRange };
    }

    /// <summary>PaintLATCs: conflict-area segments with their entry/exit times.</summary>
    private static List<object> Latcs()
    {
        var result = new List<object>();
        if (StaticMember(typeof(MMI), "LATCs") is not IEnumerable latcs) return result;

        foreach (var latc in latcs)
        {
            if (latc is null || Member(latc, "Segments") is not IEnumerable segments) continue;
            foreach (var segment in segments.Cast<object>().ToList())
            {
                if (segment is null) continue;
                if (Member(segment, "startLatlong") is not Coordinate start) continue;
                if (Member(segment, "endLatlong") is not Coordinate end) continue;
                var callsign = Member(segment, "callsign") as string ?? "";
                var startTime = Member(segment, "startTime") is DateTime st ? st : DateTime.MaxValue;
                var endTime = Member(segment, "endTime") is DateTime et ? et : DateTime.MaxValue;
                result.Add(new
                {
                    A = LatLon(start),
                    B = LatLon(end),
                    AText = startTime != DateTime.MaxValue ? callsign + " " + startTime.ToString("HH:mm") : null,
                    BText = endTime != DateTime.MaxValue ? callsign + " " + endTime.ToString("HH:mm") : null,
                });
            }
        }
        return result;
    }

    /// <summary>PaintTOPs: completed closest-approach results.</summary>
    private static List<object> Tops()
    {
        var result = new List<object>();
        if (StaticMember(typeof(MMI), "TOPs") is not IEnumerable tops) return result;

        foreach (var item in tops)
        {
            if (item is not TimeOfPassing top || !top.Completed) continue;
            if (top.Position1 is null || top.Position2 is null) continue;

            var distance = top.Distance.ToString("f0") + "nm";
            if (top.Distance < 100.0) distance = top.Distance.ToString("0.0") + "nm";
            else if (top.Distance > 9999.0) distance = ">9999";

            result.Add(new
            {
                A = LatLon(top.Position1),
                B = LatLon(top.Position2),
                Lines = new[]
                {
                    top.Time.ToString("HH:mm"), distance, top.FDR1?.Callsign ?? "", top.FDR2?.Callsign ?? "",
                },
            });
        }
        return result;
    }

    /// <summary>PaintRoutes: the graphic route of every track that has one switched
    /// on, as an ordered list of drawing steps in lat/lon. Kinds: "line" (A to B),
    /// "square" (waypoint marker at P), "leader" (20 px stub east of P) and "label"
    /// (text block 20 px east of P).</summary>
    private static List<List<object>> Routes(List<Track> tracks)
    {
        var result = new List<List<object>>();
        foreach (var track in tracks)
        {
            try
            {
                var route = Route(track);
                if (route != null && route.Count > 0) result.Add(route);
            }
            catch { }
        }
        return result;
    }

    private static List<object>? Route(Track track)
    {
        var fdr = track.GetFDR();
        if (!track.GraphicRTE || fdr is null) return null;

        var ops = new List<object>();
        var list = fdr.ParsedRoute.ToList();
        var first = fdr.ParsedRoute.OverflownIndex;
        if (first >= list.Count) first = list.Count - 1;
        if (first < 0) first = 0;

        var labels = track.GraphicRTELabels.ToList();
        var trackLocation = track.GetLocation();
        // vatSys starts from the window centre if nothing sets the first point
        double[]? previous = null;

        for (var i = first; i < list.Count; i++)
        {
            var startsAtWaypoint = false;
            if (i == 0 || i == first)
            {
                if (VatSysInternals.GetAlerts(track).Any(a => a.Type == AlertTypes.RAM) && fdr.PredictedPosition != null)
                {
                    previous = LatLon(fdr.PredictedPosition.Location);
                    ops.Add(new { K = "line", A = LatLon(trackLocation), B = previous, C = "Warning", W = 2, D = "dash" });
                    var before = list.FindLastIndex(i, s => s.Type == FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.WAYPOINT);
                    var after = list.FindIndex(i, s => s.Type == FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.WAYPOINT);
                    if (before != -1 && after != -1 && before != after)
                    {
                        ops.Add(new
                        {
                            K = "line",
                            A = LatLon(list[before].Intersection.LatLong),
                            B = LatLon(list[after].Intersection.LatLong),
                            C = "Warning", W = 1, D = "dot",
                        });
                    }
                }
                else if (track.Type == Track.TrackTypes.TRACK_TYPE_FP && !MMI.FPASD)
                {
                    previous = LatLon(list[i].Intersection.LatLong);
                    startsAtWaypoint = true;
                }
                else
                {
                    previous = LatLon(trackLocation);
                }
            }

            if (!startsAtWaypoint && i == first && i != list.Count - 1 && (i != 0 || !(fdr.ATD == DateTime.MaxValue)))
                continue;

            var segment = list[i];
            if (segment.Type != FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.WAYPOINT) continue;

            var point = LatLon(segment.Intersection.LatLong);
            ops.Add(new { K = "square", P = point, C = "Route" });

            if (track.GraphicRTELabelsVisible)
            {
                ops.Add(new { K = "leader", P = point, C = "Route" });

                // A label the controller moved or extended on the main ASD keeps that state
                var routeLabel = labels.SingleOrDefault(l => l.ExtractedRouteSegment == segment);
                var eto = "--:-- ";
                if (segment.ETO != DateTime.MaxValue) eto = segment.ETO.ToString("HH:mm") + " ";

                string? extended = null;
                if (routeLabel != null && routeLabel.Extended)
                {
                    var name = segment.Intersection.Name;
                    if (segment.Intersection.Type == Airspace2.Intersection.Types.Unknown && !string.IsNullOrEmpty(segment.Intersection.FullName))
                        name = segment.Intersection.FullName;
                    extended = name + " " + $"{segment.PCL / 100.0:000}";
                }

                ops.Add(new
                {
                    K = "label",
                    P = routeLabel?.Latlong != null ? LatLon(routeLabel.Latlong) : point,
                    Eto = eto,
                    EtoC = segment.IsPETO ? "StaticTools" : "Route",
                    Text = fdr.Callsign + " " + fdr.CFLString,
                    Ext = extended,
                    C = "Route",
                });
            }

            if (i > 0)
                ops.Add(new { K = "line", A = previous, B = point, C = segment.ParseFailed ? "Warning" : "Route", W = 1, D = "" });
            previous = point;
        }

        return ops;
    }

    // ---- Helpers ----

    /// <summary>[lat, lon]. NaN is not valid JSON and would lose the whole response.</summary>
    public static double[] LatLon(Coordinate c)
    {
        var lat = c.Latitude;
        var lon = c.Longitude;
        if (double.IsNaN(lat) || double.IsInfinity(lat)) lat = 0;
        if (double.IsNaN(lon) || double.IsInfinity(lon)) lon = 0;
        return new[] { Math.Round(lat, 7), Math.Round(lon, 7) };
    }

    private static object? StaticMember(Type type, string name)
    {
        try
        {
            var field = type.GetField(name, Any);
            if (field is not null) return field.GetValue(null);
            return type.GetProperty(name, Any)?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static object? Member(object target, string name)
    {
        try
        {
            var type = target.GetType();
            var field = type.GetField(name, Any);
            if (field is not null) return field.GetValue(target);
            return type.GetProperty(name, Any)?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }
}
