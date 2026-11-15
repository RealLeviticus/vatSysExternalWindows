using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using vatsys;

namespace VatSysExternalWindows;

/// <summary>One track as vatSys would paint it. The server makes every painting
/// decision (which symbols, what colour, what the label says); the page only
/// projects the position and draws.</summary>
public sealed class TrackDto
{
    /// <summary>Stable key for the page's own label-move state (the pilot callsign).</summary>
    public string Id { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    /// <summary>HMI state on the Air window — vatSys paints tracks grouped by it,
    /// all symbols of a group before its labels. Null on the Ground window, where
    /// each track's symbol and label are painted together.</summary>
    public string? State { get; set; }
    /// <summary>Track colour (symbols, history, velocity vector).</summary>
    public int[] Col { get; set; } = { 0, 0, 0 };
    /// <summary>Ground window: the track is not painted at all when its position
    /// is outside the window.</summary>
    public bool Clip { get; set; }
    /// <summary>Symbols at the track position, in paint order.</summary>
    public List<TrackSymbolDto> Syms { get; set; } = new();
    /// <summary>Past positions, newest first: [lat, lon, heading].</summary>
    public double[][]? History { get; set; }
    /// <summary>Squawk used to pick history / velocity marker symbol variants (-1 = default).</summary>
    public int HistSsr { get; set; } = -1;
    /// <summary>Velocity vector length in minutes (0 = none), with the groundspeed
    /// and track it is drawn from.</summary>
    public int Vv { get; set; }
    public double Gs { get; set; }
    public double Trk { get; set; }
    public LabelDto? Label { get; set; }
}

public sealed class TrackSymbolDto
{
    /// <summary>TrackSymbols.Types name.</summary>
    public string Type { get; set; } = "";
    /// <summary>Squawk for SpecialCode symbol variants (-1 = default variant).</summary>
    public int Ssr { get; set; } = -1;
    /// <summary>Heading for rotate-to-track symbols (-1 = not rotated).</summary>
    public double Hdg { get; set; } = -1;
    /// <summary>Identing: painted only on the flash phase.</summary>
    public bool Ident { get; set; }
    /// <summary>Colour override (the ident colour); null = the track colour.</summary>
    public int[]? Col { get; set; }
}

public sealed class FontMetrics
{
    public double LabelFontSize { get; set; } = 18;
    /// <summary>ASDControlDX MainFontWidth / MainFontHeight: the label character cell.</summary>
    public int LabelFontWidth { get; set; }
    public int LabelFontHeight { get; set; }
    public double MapFontSize { get; set; } = 16;
    public int MapFontWidth { get; set; }
    public int MapFontHeight { get; set; }
}

/// <summary>Ports of the track-painting decisions in vatSys ASDControlDX that both
/// windows share.</summary>
internal static class TrackPainter
{
    public static TrackSymbolDto Symbol(string type, int ssr, double heading)
    {
        return new TrackSymbolDto { Type = type, Ssr = ssr, Hdg = Json.Number(heading, 1) };
    }

    /// <summary>SelectASDBrush: plugin colour, else by HMI state (quick-look states
    /// use their own colours).</summary>
    public static Color AsdColour(Track? tl, out bool isNonJurisdictionBrush)
    {
        isNonJurisdictionBrush = false;
        if (tl is null)
        {
            isNonJurisdictionBrush = true;
            return TrackLabelBuilder.Colour(Colours.Identities.NonJurisdiction);
        }

        var custom = VatSysInternals.GetCustomTrackColour(true, tl);
        if (custom != null) return Color.FromArgb(custom.R, custom.G, custom.B);

        var state = tl.State;
        var id = Colours.Identities.NonJurisdiction;
        if (tl.IQL && state != MMI.HMIStates.HandoverIn && state != MMI.HMIStates.HandoverOut)
        {
            switch (state)
            {
                case MMI.HMIStates.NonJurisdiction:
                case MMI.HMIStates.Preactive:
                case MMI.HMIStates.PostJurisdiction:
                case MMI.HMIStates.Announced:
                case MMI.HMIStates.GhostJurisdiction:
                    id = Colours.Identities.NonJurisdictionIQL;
                    break;
                case MMI.HMIStates.Jurisdiction:
                    id = Colours.Identities.JurisdictionIQL;
                    break;
            }
        }
        else
        {
            switch (state)
            {
                case MMI.HMIStates.Announced: id = Colours.Identities.Announced; break;
                case MMI.HMIStates.HandoverIn:
                case MMI.HMIStates.HandoverOut: id = Colours.Identities.Handover; break;
                case MMI.HMIStates.Jurisdiction: id = Colours.Identities.Jurisdiction; break;
                case MMI.HMIStates.PostJurisdiction: id = Colours.Identities.PostJurisdiction; break;
                case MMI.HMIStates.GhostJurisdiction: id = Colours.Identities.GhostJurisdiction; break;
            }
        }

        isNonJurisdictionBrush = id == Colours.Identities.NonJurisdiction;
        return TrackLabelBuilder.Colour(id);
    }

    /// <summary>SelectGroundBrush: plugin colour, else departure / arrival / local for
    /// the window's airport, else unknown.</summary>
    public static Color GroundColour(Track? tl, FDP2.FDR? fdr, string icao)
    {
        if (tl != null)
        {
            var custom = VatSysInternals.GetCustomTrackColour(false, tl);
            if (custom != null) return Color.FromArgb(custom.R, custom.G, custom.B);
        }

        if (fdr != null && !string.IsNullOrWhiteSpace(icao))
        {
            if (string.Equals(fdr.DepAirport, icao, StringComparison.OrdinalIgnoreCase))
                return TrackLabelBuilder.Colour(Colours.Identities.GroundDeparture);
            if (string.Equals(fdr.DesAirport, icao, StringComparison.OrdinalIgnoreCase))
                return TrackLabelBuilder.Colour(Colours.Identities.GroundArrival);
            return TrackLabelBuilder.Colour(Colours.Identities.GroundLocal);
        }

        return TrackLabelBuilder.Colour(Colours.Identities.GroundUnknown);
    }

    /// <summary>History markers: the newest MMI.HistoryDots positions, newest first.</summary>
    public static double[][]? History(RDP.RadarTrack track)
    {
        try
        {
            var maxDots = MMI.HistoryDots;
            if (maxDots <= 0) return null;

            var history = track.PositionHistory?.ToArray();
            if (history is null || history.Length == 0) return null;

            var dots = new List<double[]>();
            var taken = 0;
            for (var i = history.Length - 1; i >= 0 && taken < maxDots; i--, taken++)
            {
                var h = history[i];
                if (h?.LatLong is null) continue;
                dots.Add(new[]
                {
                    Math.Round(h.LatLong.Latitude, 7), Math.Round(h.LatLong.Longitude, 7), Json.Number(h.Heading, 1),
                });
            }
            return dots.Count > 0 ? dots.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    // ---- Font metrics ----

    private static readonly object MetricsLock = new();
    private static FontMetrics? _metrics;
    private static string _metricsKey = "";

    /// <summary>The character cell sizes vatSys lays labels out with
    /// (MMI.GetFontWidth / GetFontHeight). Measured on the GUI thread, where the
    /// fonts live, and cached until the user changes the font.</summary>
    public static FontMetrics GetFontMetrics()
    {
        FontMetrics? measured = null;
        GuiThread.Run(() => measured = Measure());
        if (measured != null) return measured;

        lock (MetricsLock)
        {
            if (_metrics != null) return _metrics;
        }

        // The GUI thread was busy or not up yet: measure here rather than serve
        // nothing. The fonts are shared with the GUI thread, so this can fail.
        try { return Measure(); } catch { return new FontMetrics(); }
    }

    private static FontMetrics Measure()
    {
        var main = MMI.ASDMainFont;
        var map = MMI.ASDMapFont;
        var key = main.Name + "|" + main.Size + "|" + main.Style + "|" + map.Name + "|" + map.Size + "|" + map.Style;
        lock (MetricsLock)
        {
            if (_metrics != null && key == _metricsKey) return _metrics;
        }

        var metrics = new FontMetrics
        {
            LabelFontSize = main.Size,
            LabelFontWidth = FontWidth(main),
            LabelFontHeight = FontHeight(main),
            MapFontSize = map.Size,
            MapFontWidth = FontWidth(map),
            MapFontHeight = FontHeight(map),
        };
        lock (MetricsLock)
        {
            _metrics = metrics;
            _metricsKey = key;
        }
        return metrics;
    }

    // MMI.GetFontWidth: the width of "_" with no padding. It must be measured on a
    // Graphics, as vatSys does — the overload without one pads the result.
    private static int FontWidth(Font font)
    {
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        var size = TextRenderer.MeasureText(graphics, "_", font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        return size.Width > 0 ? size.Width : 12;
    }

    // MMI.GetFontHeight: the font's ascent in pixels, plus one
    private static int FontHeight(Font font)
    {
        var ascent = font.FontFamily.GetCellAscent(font.Style);
        return (int)Math.Round(font.Size * ascent / font.FontFamily.GetEmHeight(font.Style)) + 1;
    }
}
