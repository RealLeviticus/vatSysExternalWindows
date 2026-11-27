using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using vatsys;

namespace VatSysExternalWindows;

// ---- Label DTOs: a grid of character cells, laid out by the page ----

/// <summary>A track label as vatSys's PaintLabel would draw it, in character
/// cells. The page positions it from the track's screen location and draws
/// each cell; all text, colours and visibility decisions are made here.</summary>
public sealed class LabelDto
{
    /// <summary>Label direction in 22.5° steps (0-15) and leader length (0-2), as set in vatSys.</summary>
    public int Dir { get; set; }
    public int Len { get; set; }
    /// <summary>Label box width in characters (the label definition's MaximumWidth).</summary>
    public int Width { get; set; }
    /// <summary>Label box height in rows.</summary>
    public int Rows { get; set; }
    /// <summary>Rows above the leader end that the box starts at.</summary>
    public int AnchorRow { get; set; }
    /// <summary>On the flash phase vatSys paints the label with a transparent track
    /// brush: the leader and everything in the track colour disappear, and the
    /// lines in <see cref="Flash"/> / <see cref="SharedFlash"/> are what is left.</summary>
    public bool Blink { get; set; }
    public List<LabelLineDto>? Flash { get; set; }
    public List<LabelLineDto>? SharedFlash { get; set; }
    /// <summary>Leader line colour.</summary>
    public int[] Col { get; set; } = { 0, 0, 0 };
    public List<LabelLineDto> Lines { get; set; } = new();
    /// <summary>Lines to show during the timeshare phase when the label has shared
    /// lines and vatSys's Shared Labels setting is on; null otherwise.</summary>
    public List<LabelLineDto>? Shared { get; set; }
    /// <summary>Alert box colour, or null for no box.</summary>
    public int[]? Box { get; set; }
    /// <summary>The widest painted line, in characters, for the alert box: of
    /// <see cref="Lines"/> and of <see cref="Shared"/>.</summary>
    public int BoxWidth { get; set; }
    public int SharedBoxWidth { get; set; }
}

public sealed class LabelLineDto
{
    /// <summary>Justified: on the west side the cells run right-to-left from the box's right edge.</summary>
    public bool J { get; set; }
    public List<LabelCellDto> Cells { get; set; } = new();
}

public sealed class LabelCellDto
{
    /// <summary>Offset from the line start and width, in characters.</summary>
    public int X { get; set; }
    public int W { get; set; }
    /// <summary>Background fill colour, or null.</summary>
    public int[]? Back { get; set; }
    public List<LabelRunDto> Runs { get; set; } = new();
}

public sealed class LabelRunDto
{
    public string T { get; set; } = "";
    /// <summary>Text colour; null when only the border is painted.</summary>
    public int[]? C { get; set; }
    /// <summary>Offset inside the cell: Dx characters (truncated to pixels) plus Px pixels,
    /// and Dy row heights vertically.</summary>
    public double Dx { get; set; }
    public int Px { get; set; }
    public double Dy { get; set; }
    /// <summary>Border flags around the text (1=Left 2=Right 4=Top 8=Bottom) and its colour.</summary>
    public int B { get; set; }
    public int[]? BC { get; set; }
}

/// <summary>
/// Port of vatSys ASDControlDX.PaintLabel: walks the profile's label definition
/// for the label type and produces the cells vatSys would paint, using the
/// same item rules, widths, colours and alert handling.
/// </summary>
internal static class TrackLabelBuilder
{
    private static readonly Regex StatusRemark = new(@"\bSTS\/\w+\b", RegexOptions.Compiled);

    /// <summary>[r, g, b], or [r, g, b, a] when not opaque (plugin colours can carry alpha).</summary>
    public static int[] Rgb(Color c) => TrackBuilder.Rgba(c);

    /// <summary>Colours.GetColourDX: the brightness-adjusted colour vatSys paints with.</summary>
    public static Color Colour(Colours.Identities id) => VatColours.Dx(id);

    /// <param name="isAsmgcs">Ground window label (uses the track's ASMGCS label position).</param>
    /// <param name="labelType">Labels.LabelTypes name.</param>
    /// <param name="trackColour">The brush PaintLabel is given (already swapped to the
    /// non-jurisdiction label colour where vatSys does that).</param>
    public static LabelDto? Build(bool isAsmgcs, Track tl, FDP2.FDR? fdr, QuickTag? qt, RDP.RadarTrack? rt,
        NetworkPilot? pilot, string labelType, Color trackColour, bool blink = false)
    {
        var def = VatSysInternals.FindLabel(labelType);
        if (def is null) return null;

        // Alert selection, exactly as PaintLabel does it
        var alerts = VatSysInternals.GetAlerts(tl);
        var alert = alerts.FirstOrDefault(a => !a.Cancelled && a.Type != AlertTypes.EMG && a.Type != AlertTypes.RAD);
        var alert2 = alerts.FirstOrDefault(a => !a.Cancelled && (a.Type == AlertTypes.EMG || a.Type == AlertTypes.RAD));
        var alert4 = alerts.OrderBy(a => a.Priority).FirstOrDefault();
        AlertInfo? alert3 = null;
        if ((alert != null && alert2 != null && !alert.Inhibited && alert.Priority < alert2.Priority) ||
            (alert != null && alert2 != null && !alert.Inhibited && alert2.Inhibited) ||
            (alert != null && alert2 == null))
        {
            alert3 = alert;
        }
        else if ((alert != null && alert2 != null && !alert2.Inhibited && alert2.Priority < alert.Priority) ||
                 (alert != null && alert2 != null && !alert2.Inhibited && alert.Inhibited) ||
                 (alert2 != null && alert == null))
        {
            alert3 = alert2;
        }

        var ownSector = false;
        try { ownSector = alert4 != null && fdr != null && MMI.SectorsControlled.Contains(fdr.ControllingSector); } catch { }
        var priority = ownSector && alert4 != null ? (int)alert4.Priority : 0; // 1..4, 0 = none

        var context = new Context(tl, fdr, qt, rt, pilot, labelType, trackColour, alert, alert2, priority);

        var baseLines = def.Lines.Where(l => string.IsNullOrEmpty(l.SharedId)).ToList();
        var visibleLines = def.Lines.Where(l => IsLineVisible(l, tl, fdr)).ToList();

        var rows = baseLines.Count(l => IsLineVisible(l, tl, fdr));
        if (rows == 0) rows = def.Lines.Count;

        var label = new LabelDto
        {
            Dir = isAsmgcs ? tl.ASMGCSLabelDirection : tl.ASDLabelDirection,
            Len = isAsmgcs ? tl.ASMGCSLabelLeaderLength : tl.ASDLabelLeaderLength,
            Width = def.MaxWidth,
            Rows = rows,
            AnchorRow = Math.Max(0, visibleLines.FindIndex(l => l.Items.Exists(i => i.LeaderLineAnchor)) + def.AnchorOffset),
            Col = Rgb(trackColour),
        };

        label.Lines = BuildLines(def, baseLines, context, out var widest);
        label.BoxWidth = widest;

        // The same lines as PaintLabel leaves them when handed the transparent brush
        var flashContext = blink
            ? new Context(tl, fdr, qt, rt, pilot, labelType, Color.Transparent, alert, alert2, priority)
            : null;
        label.Blink = blink;
        if (flashContext != null) label.Flash = Remaining(BuildLines(def, baseLines, flashContext, out _));

        // Shared lines replace the line they are shared with on the timeshare phase
        var sharedOn = false;
        try { sharedOn = MMI.SharedLabels; } catch { }
        if (sharedOn && def.Lines.Exists(l => !string.IsNullOrEmpty(l.SharedId)))
        {
            var swapped = new List<LabelLineDef>(baseLines);
            foreach (var line in def.Lines.Where(l => !string.IsNullOrEmpty(l.SharedId)))
            {
                var index = swapped.FindIndex(l => l.Id == line.SharedId);
                if (index >= 0) swapped[index] = line;
            }
            label.Shared = BuildLines(def, swapped, context, out var sharedWidest);
            label.SharedBoxWidth = sharedWidest;
            if (flashContext != null) label.SharedFlash = Remaining(BuildLines(def, swapped, flashContext, out _));
        }

        var state = tl.State;
        if (def.BoxDefault && alert3 != null && !alert3.Inhibited &&
            (state == MMI.HMIStates.Jurisdiction || state == MMI.HMIStates.GhostJurisdiction ||
             state == MMI.HMIStates.HandoverIn || state == MMI.HMIStates.HandoverOut))
        {
            var emergency = alert3.Type == AlertTypes.EMG || alert3.Type == AlertTypes.RAD;
            label.Box = Rgb(Colour(emergency ? Colours.Identities.Emergency : Colours.Identities.Warning));
        }

        return label;
    }

    /// <summary>Flash-phase lines, or an empty list when nothing in them is painted.</summary>
    private static List<LabelLineDto> Remaining(List<LabelLineDto> lines)
    {
        return lines.Exists(l => l.Cells.Count > 0) ? lines : new List<LabelLineDto>();
    }

    private static bool IsLineVisible(LabelLineDef line, Track track, FDP2.FDR? fdr)
    {
        if (line.Visibility != "Toggle" && line.Visibility != "Hidden") return true;
        if (IsLineToggled(line, track)) return true;

        foreach (var item in line.Items)
        {
            try
            {
                if (item.Type == "LABEL_ITEM_CPDLC_MESSAGE" && track.CPDLCMessageToggle) return true;
                if (fdr is null) continue;
                if (item.Type == "LABEL_ITEM_CFL_LOWER" && fdr.CFLLower != -1) return true;
                if (item.Type == "LABEL_ITEM_WXDEV_L" && fdr.OTDLeft != 0) return true;
                if (item.Type == "LABEL_ITEM_WXDEV_R" && fdr.OTDRight != 0) return true;
            }
            catch { }
        }
        return false;
    }

    private static bool IsLineToggled(LabelLineDef line, Track track)
    {
        try
        {
            return line.Visibility == "Toggle" && track.LabelLineToggles != null &&
                   track.LabelLineToggles.TryGetValue(line.Id, out var on) && on;
        }
        catch
        {
            return false;
        }
    }

    private sealed class Context
    {
        public Context(Track tl, FDP2.FDR? fdr, QuickTag? qt, RDP.RadarTrack? rt, NetworkPilot? pilot,
            string labelType, Color trackColour, AlertInfo? alert, AlertInfo? alert2, int priority)
        {
            Track = tl; Fdr = fdr; QuickTag = qt; Radar = rt; Pilot = pilot; LabelType = labelType;
            TrackColour = trackColour; Alert = alert; Alert2 = alert2; Priority = priority;
        }

        public readonly Track Track;
        public readonly FDP2.FDR? Fdr;
        public readonly QuickTag? QuickTag;
        public readonly RDP.RadarTrack? Radar;
        public readonly NetworkPilot? Pilot;
        public readonly string LabelType;
        public readonly Color TrackColour;
        public readonly AlertInfo? Alert;
        public readonly AlertInfo? Alert2;
        public readonly int Priority;
    }

    /// <summary>Lays out the given lines. A line that ends up with no width takes
    /// no row, exactly as in vatSys (empty fixed-width items still count).</summary>
    private static List<LabelLineDto> BuildLines(LabelDef def, List<LabelLineDef> lines, Context c, out int widest)
    {
        var result = new List<LabelLineDto>();
        widest = 0;

        foreach (var line in lines)
        {
            if (!IsLineVisible(line, c.Track, c.Fdr)) continue;

            var row = new LabelLineDto { J = def.Justified || line.Justified };
            var x = 0;
            foreach (var item in line.Items)
            {
                try { x = AddItem(row, x, line, item, c); }
                catch { }
            }

            if (x > widest) widest = x;
            if (x == 0) continue;

            row.Cells.RemoveAll(cell => cell.Back is null && cell.Runs.Count == 0);
            result.Add(row);
        }

        return result;
    }

    /// <summary>One label item: resolves its colours, then adds its cell. Returns the
    /// new horizontal position (characters).</summary>
    private static int AddItem(LabelLineDto row, int x, LabelLineDef line, LabelItemDef item, Context c)
    {
        var tl = c.Track;
        var fdr = c.Fdr;
        var qt = c.QuickTag;
        var rt = c.Radar;
        var trackCol = c.TrackColour;

        // Item colours: the track colour unless the item, or the alert priority,
        // overrides it. With the transparent (flash) brush the item's own colours
        // are not applied; the alert-priority ones still are.
        var itemCol = trackCol;
        var borderCol = trackCol;
        Color? backCol = null;
        if (trackCol.A != 0)
        {
            if (item.Colour != Colours.Identities.Default) itemCol = Colour(item.Colour);
            if (item.BorderColour != Colours.Identities.Default) borderCol = Colour(item.BorderColour);
            if (item.BackColour != Colours.Identities.Default) backCol = Colour(item.BackColour);
        }
        if (c.Priority >= 1 && c.Priority <= 4)
        {
            var p = c.Priority - 1;
            if (item.PriorityBackColour[p] != Colours.Identities.Default) backCol = Colour(item.PriorityBackColour[p]);
            if (item.PriorityColour[p] != Colours.Identities.Default) itemCol = Colour(item.PriorityColour[p]);
            if (item.PriorityBorderColour[p] != Colours.Identities.Default) borderCol = Colour(item.PriorityBorderColour[p]);
        }
        var hasBack = backCol.HasValue;
        var border = item.Border;

        x += item.LeftPadding;
        var start = x;

        LabelCellDto Cell(int width)
        {
            var cell = new LabelCellDto { X = start, W = width };
            row.Cells.Add(cell);
            x = start + width;
            return cell;
        }

        void Fill(LabelCellDto cell)
        {
            if (hasBack && backCol.HasValue && backCol.Value.A != 0) cell.Back = Rgb(backCol.Value);
        }

        void Text(LabelCellDto cell, string? text, Color colour, double dx = 0, int px = 0, double dy = 0)
        {
            if (string.IsNullOrEmpty(text)) return;
            var drawText = colour.A != 0;
            var drawBorder = border != BorderFlags.None && borderCol.A != 0;
            if (!drawText && !drawBorder) return;
            cell.Runs.Add(new LabelRunDto
            {
                T = text!, C = drawText ? Rgb(colour) : null, Dx = dx, Px = px, Dy = dy,
                B = drawBorder ? (int)border : 0, BC = drawBorder ? Rgb(borderCol) : null,
            });
        }

        string Level3(int feet) => (feet / 100).ToString("D3");

        switch (item.Type)
        {
            case "UNKNOWN":
            {
                // Items other plugins supply (strip bay, events, ...)
                var custom = VatSysInternals.GetCustomLabelItem(item.UnknownType, tl, fdr, rt);
                var text = "";
                if (custom != null)
                {
                    if (itemCol.A != 0)
                    {
                        itemCol = CustomColour(custom.ForeColourIdentity, custom.CustomForeColour, itemCol);
                        backCol = CustomColour(custom.BackColourIdentity, custom.CustomBackColour, backCol ?? Color.Transparent);
                        borderCol = CustomColour(custom.BorderColourIdentity, custom.CustomBorderColour, borderCol);
                    }
                    text = custom.Text ?? "";
                    border = custom.Border;
                }
                var cell = Cell(text.Length);
                Fill(cell);
                if (custom != null) Text(cell, text, itemCol);
                break;
            }

            case "LABEL_ITEM_CPROMPT":
            {
                var cell = Cell(1);
                Fill(cell);
                if (tl.CPrompt) Text(cell, "C", itemCol);
                break;
            }

            case "LABEL_ITEM_RWY":
            {
                var cell = Cell(3);
                Fill(cell);
                if (fdr != null)
                {
                    // Departure runway until ATD is set, then the arrival runway
                    Text(cell, fdr.ATD != DateTime.MaxValue ? fdr.ArrivalRunway?.Name : fdr.DepartureRunway?.Name, itemCol);
                }
                break;
            }

            case "LABEL_ITEM_SID":
            {
                var cell = Cell(7);
                Fill(cell);
                if (fdr != null) Text(cell, fdr.SID?.Name, itemCol);
                break;
            }

            case "LABEL_ITEM_WARNING":
            {
                var cell = Cell(4);
                if (c.Alert != null)
                {
                    Fill(cell);
                    Text(cell, c.Alert.Type.ToString(), itemCol);
                }
                break;
            }

            case "LABEL_ITEM_EMERG":
            {
                var cell = Cell(3);
                if (c.Alert2 != null)
                {
                    Fill(cell);
                    Text(cell, c.Alert2.Type.ToString(), itemCol);
                }
                break;
            }

            case "LABEL_ITEM_ACID":
            {
                var text = AcidText(c);
                if (!string.IsNullOrEmpty(text))
                {
                    var cell = Cell(text!.Length);
                    Fill(cell);
                    Text(cell, text, itemCol);
                }
                break;
            }

            case "LABEL_ITEM_CPDLC":
            {
                var cell = Cell(1);
                if (fdr != null)
                {
                    if (RadioRequests(fdr).Count != 0) Fill(cell);
                    if (fdr.ReceiveOnly) Text(cell, "-", itemCol);
                    else if (fdr.TextOnly) Text(cell, "+", itemCol);
                    else if (HasOpenRequest(fdr)) Text(cell, "*", itemCol);
                }
                else
                {
                    Fill(cell);
                }
                break;
            }

            case "LABEL_ITEM_WAKETURBCAT":
            {
                var cell = Cell(1);
                Fill(cell);
                if (fdr != null) Text(cell, fdr.AircraftWake, itemCol);
                break;
            }

            case "LABEL_ITEM_FIELD18RMK":
            {
                var cell = Cell(1);
                Fill(cell);
                if (fdr != null && StatusRemark.IsMatch(fdr.Remarks ?? "")) Text(cell, "*", itemCol);
                break;
            }

            case "LABEL_ITEM_LEVEL":
            {
                var cell = Cell(3);
                Fill(cell);
                if (rt != null && rt.CorrectedAltitude != -1) Text(cell, Level3(rt.CorrectedAltitude), itemCol);
                else if (fdr != null && fdr.PRL != -1) Text(cell, Level3(fdr.PRL), itemCol);
                break;
            }

            case "LABEL_ITEM_ASMGCS_HEIGHT":
            {
                var cell = Cell(5);
                Fill(cell);
                if (rt != null && rt.CorrectedAltitude != -1)
                {
                    Text(cell, rt.CorrectedAltitude <= RDP.TRANSITION_ALTITUDE
                        ? rt.CorrectedAltitude.ToString("D5")
                        : "FL" + Level3(rt.CorrectedAltitude), itemCol);
                }
                break;
            }

            case "LABEL_ITEM_RADAR_SENSORS":
            {
                var cell = Cell(3);
                Fill(cell);
                if (rt != null)
                {
                    var text = "";
                    if (rt.Coasting)
                    {
                        text = "C";
                    }
                    else
                    {
                        var types = rt.RadarTypes;
                        if (types.HasFlag(RDP.RadarTypes.SMR) || types.HasFlag(RDP.RadarTypes.PRI)) text += "A";
                        if (types.HasFlag(RDP.RadarTypes.ADSB) || types.HasFlag(RDP.RadarTypes.SMR_ADSB)) text += "B";
                        if (types.HasFlag(RDP.RadarTypes.SSR_ModeC) || types.HasFlag(RDP.RadarTypes.SSR_ModeS)) text += "R";
                    }
                    Text(cell, text, itemCol);
                }
                break;
            }

            case "LABEL_ITEM_ASMGCS_MOVEMENT_CAT":
            {
                var cell = Cell(1);
                Fill(cell);
                if (c.LabelType == "GroundDeparture") Text(cell, "D", itemCol);
                else if (c.LabelType == "GroundArrival") Text(cell, "A", itemCol);
                break;
            }

            case "LABEL_ITEM_VERTICALINDICATOR":
            {
                var cell = Cell(1);
                Fill(cell);
                if (rt != null && rt.VerticalSpeed != -9999.0 && !rt.Coasting)
                {
                    // vatSys font glyphs: ® climb, ¬ descent, > level
                    Text(cell, rt.VerticalSpeed > 300.0 ? "®" : rt.VerticalSpeed < -300.0 ? "¬" : ">", itemCol);
                }
                break;
            }

            case "LABEL_ITEM_CFL":
            {
                var cell = Cell(3);
                Fill(cell);
                // A new CFL is highlighted in the item colour; otherwise the track colour
                var colour = tl.NewCFL ? itemCol : trackCol;
                if (fdr != null && fdr.CFLUpper != -1) Text(cell, fdr.CFLUpper == 2 ? "VSA" : Level3(fdr.CFLUpper), colour);
                else if (qt != null && qt.CFL != -1) Text(cell, qt.CFL == 2 ? "VSA" : Level3(qt.CFL), colour);
                break;
            }

            case "LABEL_ITEM_ASMGCS_CFL":
            {
                var cell = Cell(5);
                Fill(cell);
                if (fdr != null && fdr.CFLUpper != -1 && fdr.CFLUpper != 1 && fdr.CFLUpper != 2)
                {
                    Text(cell, fdr.CFLUpper <= RDP.TRANSITION_ALTITUDE
                        ? fdr.CFLUpper.ToString("D5")
                        : "FL" + Level3(fdr.CFLUpper), trackCol);
                }
                break;
            }

            case "LABEL_ITEM_FRULES":
            {
                var cell = Cell(1);
                Fill(cell);
                if (fdr != null)
                {
                    if (fdr.FlightRules == "V" || fdr.FlightRules == "Z" || fdr.FlightRules == "Y")
                    {
                        Text(cell, fdr.FlightRules, trackCol);
                    }
                    else if (fdr.CFLVisual)
                    {
                        // Small "v" with a raised "s" beside it
                        Text(cell, "ᵛ", trackCol);
                        Text(cell, "ₛ", trackCol, dx: 0.5, px: -1, dy: -0.25);
                    }
                    else if (!fdr.RVSM)
                    {
                        var inBand = fdr.CFLUpper > FDP2.RVSM_BAND_LOWER || fdr.PRL > FDP2.RVSM_BAND_LOWER;
                        Text(cell, "X", inBand ? itemCol : trackCol);
                    }
                }
                break;
            }

            case "LABEL_ITEM_GROUNDSPEED":
            {
                var cell = Cell(2);
                Fill(cell);
                var groundspeed = tl.GetGroundspeed();
                Text(cell, groundspeed > 995.0 || groundspeed < 0.0 ? "++" : Math.Round(groundspeed / 10.0).ToString("00."), itemCol);
                break;
            }

            case "LABEL_ITEM_ADSBINDICATOR":
            {
                var cell = Cell(1);
                Fill(cell);
                if (fdr != null && !fdr.ADSB && (rt == null || !rt.RadarTypes.HasFlag(RDP.RadarTypes.ADSB))) Text(cell, "b", itemCol);
                break;
            }

            case "LABEL_ITEM_WXDEV_L":
            {
                var cell = Cell(4);
                Fill(cell);
                if (fdr != null)
                {
                    if (fdr.OTDLeft != 0)
                    {
                        var value = fdr.OTDLeft.ToString();
                        Text(cell, value, trackCol);
                        Text(cell, "L", itemCol, dx: value.Length);
                    }
                    else if (IsLineToggled(line, tl) && fdr.OTDRight == 0)
                    {
                        Text(cell, "--", trackCol);
                        Text(cell, "L", itemCol, dx: 2);
                    }
                }
                break;
            }

            case "LABEL_ITEM_WXDEV_R":
            {
                var cell = Cell(4);
                Fill(cell);
                if (fdr != null)
                {
                    if (fdr.OTDRight != 0)
                    {
                        var value = fdr.OTDRight.ToString();
                        if (fdr.OTDRight < 100)
                        {
                            Text(cell, value, trackCol, dx: 1);
                            Text(cell, "R", itemCol, dx: 1 + value.Length);
                        }
                        else
                        {
                            Text(cell, "R", itemCol);
                            Text(cell, value, trackCol, dx: 1);
                        }
                    }
                    else if (IsLineToggled(line, tl) && fdr.OTDLeft == 0)
                    {
                        Text(cell, "--", trackCol, dx: 1);
                        Text(cell, "R", itemCol, dx: 3);
                    }
                }
                break;
            }

            case "LABEL_ITEM_CFL_LOWER":
            {
                var cell = Cell(3);
                Fill(cell);
                if (fdr != null && fdr.CFLLower != -1)
                    Text(cell, fdr.CFLLower == 2 ? "VSA" : Level3(fdr.CFLLower), tl.NewCFL ? itemCol : trackCol);
                break;
            }

            case "LABEL_ITEM_OPDATA":
            {
                // Always 11 wide; when justified the text itself is placed as its own cell
                var justified = row.J;
                var cell = Cell(11);
                Fill(cell);
                var text = fdr != null ? fdr.LabelOpData : qt?.LabelData;
                if (!string.IsNullOrEmpty(text))
                {
                    text = text!.Substring(0, Math.Min(11, text.Length));
                    if (justified)
                    {
                        var textCell = new LabelCellDto { X = start, W = text.Length };
                        row.Cells.Add(textCell);
                        Text(textCell, text, itemCol);
                    }
                    else
                    {
                        Text(cell, text, itemCol);
                    }
                }
                break;
            }

            case "LABEL_ITEM_ADES":
            {
                var cell = Cell(4);
                Fill(cell);
                if (fdr != null) Text(cell, fdr.DesAirport, itemCol);
                break;
            }

            case "LABEL_ITEM_ADEP":
            {
                var cell = Cell(4);
                Fill(cell);
                if (fdr != null) Text(cell, fdr.DepAirport, itemCol);
                break;
            }

            case "LABEL_ITEM_ATYPE":
            {
                var cell = Cell(4);
                Fill(cell);
                if (fdr != null) Text(cell, fdr.AircraftType, itemCol);
                break;
            }

            case "LABEL_ITEM_PBN_CAP":
            {
                var cell = Cell(1);
                Fill(cell);
                if (fdr != null)
                {
                    if (fdr.PBNCapability == 'Z') itemCol = Colour(Colours.Identities.StaticTools);
                    Text(cell, fdr.PBNCapability.ToString(), itemCol);
                }
                break;
            }

            case "LABEL_ITEM_CPDLC_MESSAGE":
            {
                if (fdr is null) break;
                var requests = RadioRequests(fdr);
                if (requests.Count != 0)
                {
                    var text = requests[requests.Count - 1].Message ?? "";
                    if (requests.Count > 1) text = "*" + text;
                    var cell = Cell(text.Length);
                    Fill(cell);
                    Text(cell, text, itemCol);
                }
                break;
            }

            case "LABEL_ITEM_NEXT_FIX":
            case "LABEL_ITEM_NEXT_FIX_2":
            {
                var cell = Cell(5);
                Fill(cell);
                if (fdr != null)
                {
                    var waypoint = item.Type == "LABEL_ITEM_NEXT_FIX"
                        ? fdr.ParsedRoute?.NextWaypoint
                        : fdr.ParsedRoute?.NextPlusOneWaypoint;
                    var text = waypoint?.Name ?? "";
                    if (text.Length > 5) text = text.Substring(0, 4) + "*";
                    Text(cell, text, itemCol);
                }
                break;
            }

            // Any other item type is not painted by vatSys either and takes no space.
        }

        return x;
    }

    /// <summary>LABEL_ITEM_ACID: the FDR callsign (or the squawk in SSR-All mode),
    /// the QuickTag callsign, or for an uncorrelated radar track its Mode S /
    /// ADS-B callsign, else its squawk.</summary>
    private static string? AcidText(Context c)
    {
        var fdr = c.Fdr;
        var rt = c.Radar;
        var ssrAll = false;
        try { ssrAll = MMI.SSRAll; } catch { }

        if (fdr != null)
        {
            return !ssrAll || fdr.CoupledTrack == null || c.Pilot == null
                ? fdr.Callsign
                : Squawk(c.Pilot.TransponderCode);
        }

        if (!ssrAll && c.QuickTag != null) return c.QuickTag.Callsign;

        if (c.Track.Type == Track.TrackTypes.TRACK_TYPE_RADAR && rt != null)
        {
            var types = rt.RadarTypes;
            if ((!ssrAll && types.HasFlag(RDP.RadarTypes.SSR_ModeS)) ||
                types.HasFlag(RDP.RadarTypes.ADSB) || types.HasFlag(RDP.RadarTypes.SMR_ADSB))
            {
                return c.Pilot?.Callsign;
            }

            if ((types.HasFlag(RDP.RadarTypes.SSR_ModeC) || types.HasFlag(RDP.RadarTypes.SSR_ModeS)) && c.Pilot != null)
                return Squawk(c.Pilot.TransponderCode);
        }

        return "";
    }

    private static string Squawk(int code) => Convert.ToString(code, 8).PadLeft(4, '0');

    private static readonly object RadioLock = new();
    private static List<RadioMessage> _radioMessages = new();
    private static DateTime _radioMessagesUtc = DateTime.MinValue;

    /// <summary>The network's radio messages. Every label asks for them, so the
    /// list is copied at most a few times a second.</summary>
    private static List<RadioMessage> RadioMessages()
    {
        lock (RadioLock)
        {
            if ((DateTime.UtcNow - _radioMessagesUtc).TotalMilliseconds > 250)
            {
                try { _radioMessages = Network.GetRadioMessages ?? new List<RadioMessage>(); }
                catch { _radioMessages = new List<RadioMessage>(); }
                _radioMessagesUtc = DateTime.UtcNow;
            }
            return _radioMessages;
        }
    }

    /// <summary>Network.GetRadioRequests: unacknowledged requests addressed to the flight.</summary>
    private static List<RadioMessage> RadioRequests(FDP2.FDR fdr)
    {
        try
        {
            return RadioMessages().Where(r => r.Address == fdr.Callsign && r.Request && !r.Acknowledged).ToList();
        }
        catch
        {
            return new List<RadioMessage>();
        }
    }

    private static bool HasOpenRequest(FDP2.FDR fdr)
    {
        try
        {
            return RadioMessages().Any(r => r.Address == fdr.Callsign && r.Request && !r.History);
        }
        catch
        {
            return false;
        }
    }

    private static Color CustomColour(Colours.Identities identity, vatsys.Plugin.CustomColour? custom, Color current)
    {
        switch (identity)
        {
            case Colours.Identities.Default:
            case Colours.Identities.State:
                return current;
            case Colours.Identities.Custom:
                return custom is null ? current : Color.FromArgb(custom.A, custom.R, custom.G, custom.B);
            default:
                return Colour(identity);
        }
    }
}
