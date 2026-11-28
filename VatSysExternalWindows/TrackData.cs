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
    /// <summary>Stable key for the page's own label-move state (the callsign).</summary>
    public string Id { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    /// <summary>MMI.HMIStates value. Tracks arrive sorted by it, as Render sorts
    /// them; the Air window paints each state's symbols before that state's labels.</summary>
    public int State { get; set; }
    /// <summary>Track colour (symbols, history, velocity vector): [r, g, b] or [r, g, b, a].</summary>
    public int[] Col { get; set; } = { 0, 0, 0 };
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
    /// <summary>Short route probe, in paint order: 4 numbers = a line
    /// [lat, lon, lat, lon], 3 numbers = a marker [lat, lon, heading].</summary>
    public List<double[]>? Srp { get; set; }
    /// <summary>The marker's symbol type (SRPTimeMarker or SRPDistanceMarker).</summary>
    public string? SrpSym { get; set; }
    public LabelDto? Label { get; set; }
    /// <summary>The track has no clickspot (a cancelled radar track).</summary>
    public bool NoClick { get; set; }
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

/// <summary>The fonts vatSys draws its windows with, and the character cells it
/// lays text out on.</summary>
public sealed class FontMetrics
{
    public string Family { get; set; } = "Terminus (TTF)";
    public double LabelFontSize { get; set; } = 18;
    /// <summary>ASDControlDX MainFontWidth / MainFontHeight: the label character cell.</summary>
    public int LabelFontWidth { get; set; }
    public int LabelFontHeight { get; set; }
    public double MapFontSize { get; set; } = 16;
    public int MapFontWidth { get; set; }
    public int MapFontHeight { get; set; }
    /// <summary>MMI.eurofont_winsml (menus, title bar, ATC list) and its cell
    /// (MMI.FontWidth_Extrasmall / FontHeight_Extrasmall).</summary>
    public double SmallFontSize { get; set; } = 16;
    public int SmallFontWidth { get; set; }
    public int SmallFontHeight { get; set; }
    /// <summary>Where DirectWrite puts the baseline of a line centred in a box,
    /// below the box's centre, as a fraction of the font size:
    /// (ascent - descent - lineGap) / 2 / emHeight.</summary>
    public double BaselineShift { get; set; } = 0.288;
}

/// <summary>The font measurements ASDControlDX takes in OnPaint.</summary>
internal static class FontMeasurer
{
    private static readonly object MetricsLock = new();
    private static FontMetrics? _metrics;
    private static string _metricsKey = "";

    /// <summary>Measured on the GUI thread, where the fonts live, and cached until
    /// the user changes the font size.</summary>
    public static FontMetrics Get()
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
        var small = MMI.eurofont_winsml;
        var key = main.Name + "|" + main.Size + "|" + main.Style + "|" + map.Name + "|" + map.Size + "|" + map.Style;
        lock (MetricsLock)
        {
            if (_metrics != null && key == _metricsKey) return _metrics;
        }

        var family = main.FontFamily;
        var em = family.GetEmHeight(main.Style);
        var metrics = new FontMetrics
        {
            Family = family.Name,
            LabelFontSize = main.Size,
            LabelFontWidth = FontWidth(main),
            LabelFontHeight = FontHeight(main),
            MapFontSize = map.Size,
            MapFontWidth = FontWidth(map),
            MapFontHeight = FontHeight(map),
            SmallFontSize = small.Size,
            SmallFontWidth = FontWidth(small),
            SmallFontHeight = FontHeight(small),
        };
        if (em > 0)
        {
            // Line spacing is ascent + descent + line gap, so this is
            // (ascent - descent - gap) / 2 in em units
            metrics.BaselineShift = Math.Round(
                (family.GetCellAscent(main.Style) - family.GetLineSpacing(main.Style) / 2.0) / em, 4);
        }

        lock (MetricsLock)
        {
            _metrics = metrics;
            _metricsKey = key;
        }
        return metrics;
    }

    // MMI.GetFontWidth: the width of "_" with no padding. It must be measured on a
    // Graphics, as vatSys does; the overload without one pads the result.
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
