using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using vatsys;
using vatsys.Plugin;

namespace VatSysExternalWindows;

// ---- What a window sends when a track or label item is clicked, and what it gets back ----

/// <summary>One action from a window: a click on a track or label item, or a
/// choice made in a menu or popup that an earlier action returned.</summary>
internal sealed class ActionRequest
{
    public bool Ground;
    /// <summary>TrackDto.Id of the track acted on.</summary>
    public string Id = "";
    public string Action = "";
    public string Arg = "";
    public string Arg2 = "";
    /// <summary>The ActionUi.Ctx of the menu or popup the choice was made in.</summary>
    public string Ctx = "";
    /// <summary>0 = left, 1 = middle, 2 = right.</summary>
    public int Button;
    /// <summary>Names of the popup's toggle buttons that are checked.</summary>
    public HashSet<string> Toggles = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ActionResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>Something the user should know: a vatSys window was opened on the PC.</summary>
    public string? Note { get; set; }
    /// <summary>A menu or popup for the window to show.</summary>
    public ActionUi? Ui { get; set; }
}

/// <summary>A vatSys menu or popup, described for the page to draw.</summary>
public sealed class ActionUi
{
    /// <summary>"menu" (a context menu), "list" (CFLMenu / LevelMenu / DiversionMenu /
    /// TimeMenu: a scrolling list with buttons beneath), "handoff" (HandoffWindow),
    /// "text" (the label data field) or "confirm".</summary>
    public string Kind { get; set; } = "menu";
    /// <summary>The action to send with the choice, and context to send back with it.</summary>
    public string Action { get; set; } = "";
    public string Ctx { get; set; } = "";
    public string? Title { get; set; }
    public string? Text { get; set; }
    public List<ActionUiItem> Items { get; set; } = new();
    /// <summary>list: indices of the selected items, and the first visible one.</summary>
    public List<int>? Selected { get; set; }
    public int Top { get; set; }
    /// <summary>menu: GenericContextMenu.MaximumVisibleItems (-1 = no limit).</summary>
    public int MaxVisible { get; set; } = -1;
    /// <summary>list: buttons beneath the list. text: unused.</summary>
    public List<ActionUiButton>? Buttons { get; set; }
    /// <summary>list: a text field beneath the list (TimeMenu). text: the field itself.</summary>
    public bool Entry { get; set; }
    public int MaxLength { get; set; }
    public string? Value { get; set; }
}

public sealed class ActionUiItem
{
    public string Text { get; set; } = "";
    /// <summary>What to send as Arg when chosen; the text when null.</summary>
    public string? Arg { get; set; }
    public bool Disabled { get; set; }
    /// <summary>handoff: the next sector (drawn checked); a controller who is not online.</summary>
    public bool Checked { get; set; }
    public bool Dim { get; set; }
    /// <summary>handoff: a 120 px button (a callsign) rather than a 60 px one.</summary>
    public bool Wide { get; set; }
}

public sealed class ActionUiButton
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>A ToggleButton (stays down) rather than a GenericButton (acts at once).</summary>
    public bool Toggle { get; set; }
    public bool Checked { get; set; }
}

/// <summary>
/// What clicking a track or a label item does, ported from ASDControlDX.ASD_MouseDown
/// and the menus it opens. Anything that changes a flight goes through the same
/// vatSys calls the vatSys windows use. Menus and the small popups (levels,
/// runway, handoff, time, label data) are sent to the page to draw; the big
/// vatSys windows (flight plan, CPDLC editor, SELCAL, quick tag) are opened in
/// vatSys itself. Must be called on the GUI thread.
/// </summary>
internal static class TrackActions
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static ActionResult Execute(ActionRequest r)
    {
        try
        {
            var track = TrackBuilder.Find(r.Ground, r.Id);
            if (track is null) return Fail("That track is no longer displayed.");

            switch (r.Action)
            {
                case "click": return Click(r, track);
                case "acid": return AcidChoice(r, track);
                case "rwy": return RunwayChoice(r, track);
                case "direct": return DirectChoice(r, track);
                case "cpdlc": return CpdlcChoice(r, track);
                case "cfl": return CflChoice(r, track);
                case "prl": return PrlChoice(r, track);
                case "otd": return OtdChoice(r, track);
                case "opdata": return OpDataChoice(r, track);
                case "sar": return SarChoice(r, track);
                case "depart": return DepartChoice(r, track);
                case "handoff": return HandoffChoice(r, track);
                case "finish": return FinishChoice(r, track);
                default: return Fail("Unknown action.");
            }
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static ActionResult Done(string? note = null) => new() { Ok = true, Note = note };
    private static ActionResult Fail(string error) => new() { Ok = false, Error = error };
    private static ActionResult Show(ActionUi ui) => new() { Ok = true, Ui = ui };
    private const string NoPermission = "You do not have permission to modify this FDR";

    /// <summary>The FDR vatSys's click handling uses: in a Ground window the
    /// ASMGCS-coupled one counts too.</summary>
    private static FDP2.FDR? Fdr(ActionRequest r, Track track) => track.GetFDR(r.Ground);

    // ---- A click on a track or a label item: the switch in ASD_MouseDown ----

    private static ActionResult Click(ActionRequest r, Track track)
    {
        var fdr = Fdr(r, track);

        // Another plugin's label item sees the click first
        if (r.Ctx.Length > 0)
        {
            var custom = VatSysInternals.GetCustomLabelItem(r.Ctx, track, fdr, track.GetRadarTrack());
            if (custom?.OnMouseClick != null && Enum.TryParse<MMI.ClickspotTypes>(r.Arg, out var type))
            {
                var button = r.Button == 1 ? CustomLabelItemMouseButton.Middle
                    : r.Button == 2 ? CustomLabelItemMouseButton.Right : CustomLabelItemMouseButton.Left;
                if (Activator.CreateInstance(typeof(CustomLabelItemMouseClickEventArgs), Any, null,
                        new object[] { button, type, custom, track }, CultureInfo.InvariantCulture)
                    is CustomLabelItemMouseClickEventArgs args)
                {
                    custom.OnMouseClick(args);
                    if (args.Handled) return Done();
                }
            }
        }

        switch (r.Arg)
        {
            case "Track_Select":
                if (r.Ground)
                {
                    MMI.SelectOrDeselectGroundTrack(track);
                }
                else
                {
                    // In vatSys's conflict-tool selection modes a click picks the
                    // second track through a menu on the PC, not a selection
                    if (VatSysInternals.StaticMember(typeof(MMI), "LATC_SELECT") is true)
                        return Fail("A conflict tool selection is in progress in vatSys.");
                    MMI.SelectOrDeselectTrack(track);
                }
                return Done();

            case "Track_IQL":
                track.IQL = !track.IQL;
                return Done();

            case "Label_CPrompt":
                track.CPrompt = !track.CPrompt;
                return Done();

            case "Label_Extend_Toggle":
                track.ManualExtend = !track.ManualExtend;
                return Done();

            case "Label_NewCFL_Toggle":
                track.NewCFL = !track.NewCFL;
                return Done();

            case "Label_CPDLC_Message_Toggle":
                track.CPDLCMessageToggle = !track.CPDLCMessageToggle;
                return Done();

            case "Label_Toggle_1":
            case "Label_Toggle_2":
            case "Label_Toggle_3":
            {
                var toggles = track.LabelLineToggles;
                if (toggles != null && r.Arg2.Length > 0 && toggles.ContainsKey(r.Arg2)) toggles[r.Arg2] = !toggles[r.Arg2];
                return Done();
            }

            case "Label_Emerg":
            case "Label_Warning":
            {
                // The clickspot carries the alert PaintLabel's selection settled on
                var alert = TrackLabelBuilder.SelectAlert(VatSysInternals.GetAlerts(track), out _, out _);
                if (alert?.Raw is null) return Done();
                var cancel = r.Arg == "Label_Warning" && alert.Inhibited && alert.CanCancel;
                VatSysInternals.Invoke(alert.Raw.GetType(), alert.Raw, cancel ? "Cancel" : "Inhibit");
                return Done();
            }

            case "Label_ACID_Menu":
                return AcidMenu(r, track);

            case "Label_Runway":
                return fdr is null ? Done() : RunwayMenu(fdr);

            case "Label_CFL_Popup":
                return CflMenu(track);

            case "Label_PRL_Popup":
                return PrlMenu(fdr);

            case "Label_WX_L_Popup":
                return OtdMenu(fdr, "L");

            case "Label_WX_R_Popup":
                return OtdMenu(fdr, "R");

            case "Label_OpData_Popup":
                return OpDataField(r, track);

            case "Label_SAR_Popup":
                return SarMenu(fdr);

            case "Label_CPDLC_Menu":
            {
                if (fdr is null) return Done();
                if (RadioRequests(fdr).Count != 0) return CpdlcMenu(fdr);
                return OnPc(() => MMI.OpenCPDLCWindow(fdr), "CPDLC editor");
            }

            case "Label_CPDLC_Editor":
                return fdr is null ? Done() : OnPc(() => MMI.OpenCPDLCWindow(fdr), "CPDLC editor");

            default:
                // Label_Field18_Popup, Label_PRW_Popup and Label_Custom_Item do nothing in vatSys either
                return Done();
        }
    }

    /// <summary>Opens one of vatSys's own windows. Several of them are modal, so
    /// the call is queued rather than made here: it must not hold up the reply.</summary>
    private static ActionResult OnPc(Action open, string what)
    {
        MMI.InvokeOnGUI(delegate
        {
            try { open(); }
            catch (Exception ex) { try { Errors.Add(ex, "External Windows"); } catch { } }
        });
        return Done(what + " opened in vatSys");
    }

    // ---- Label_ACID_Menu: MMI.OpenAircraftMenu ----

    private static ContextMenuStrip? _acidMenu;

    /// <summary>vatSys's own AircraftContextMenu, configured for the track: the
    /// items it would show are read from it, and a choice is clicked on it, so
    /// both are vatSys's code. A separate instance from the one vatSys shows.</summary>
    private static ContextMenuStrip ConfiguredAcidMenu(ActionRequest r, Track track)
    {
        var type = VatSysInternals.VatSysType("vatsys.AircraftContextMenu") ?? throw new MissingMemberException("vatsys.AircraftContextMenu");
        _acidMenu ??= (ContextMenuStrip)Activator.CreateInstance(type, true);
        type.GetProperty("Source", Any)!.SetValue(_acidMenu, track);
        type.GetProperty("ASMGCS", Any)!.SetValue(_acidMenu, r.Ground);
        VatSysInternals.Invoke(type, _acidMenu, "Configure");
        return _acidMenu;
    }

    private static readonly string[] SupervisorItems = { "Quicklook", "Flightplan", "Message", "Editor" };

    private static ActionResult AcidMenu(ActionRequest r, Track track)
    {
        var ui = new ActionUi { Kind = "menu", Action = "acid" };

        if (MMI.SupervisorMode)
        {
            // The supervisor's menu; INF and Kill are left to vatSys itself
            var callsign = track.GetPilot()?.Callsign;
            if (callsign is null) return Done();
            ui.Title = callsign;
            foreach (var text in SupervisorItems) ui.Items.Add(new ActionUiItem { Text = text });
            return Show(ui);
        }

        var menu = ConfiguredAcidMenu(r, track);
        for (var i = 0; i < menu.Items.Count; i++)
        {
            var item = menu.Items[i];
            if (!item.Available) continue;
            if (i == 0) ui.Title = item.Text;
            else ui.Items.Add(new ActionUiItem { Text = item.Text, Disabled = !item.Enabled });
        }
        return ui.Items.Count == 0 && ui.Title is null ? Done() : Show(ui);
    }

    private static ActionResult AcidChoice(ActionRequest r, Track track)
    {
        if (MMI.SupervisorMode)
        {
            var callsign = track.GetPilot()?.Callsign;
            if (callsign is null) return Done();
            var planned = FDP2.GetFDRs?.FirstOrDefault(f => f.Callsign == callsign);
            switch (r.Arg)
            {
                case "Quicklook": track.IQL = !track.IQL; return Done();
                case "Flightplan": return OnPc(() => MMI.OpenFPWindow(planned), "Flight plan window");
                case "Message": return OnPc(() => MMI.OpenPMWindow(callsign), "Message window");
                case "Editor": return OnPc(() => MMI.OpenCPDLCWindow(planned), "CPDLC editor");
                default: return Fail("Unknown menu item.");
            }
        }

        // The item must be one vatSys offers for the track as it is now
        var menu = ConfiguredAcidMenu(r, track);
        ToolStripItem? chosen = null;
        for (var i = 1; i < menu.Items.Count; i++)
        {
            if (menu.Items[i].Available && menu.Items[i].Enabled && menu.Items[i].Text == r.Arg) chosen = menu.Items[i];
        }
        if (chosen is null) return Fail(r.Arg + " is no longer available for this track.");

        var fdr = Fdr(r, track);
        if (fdr != null)
        {
            // These open a menu or a modal popup in vatSys; the page shows its own
            switch (r.Arg)
            {
                case "Direct": return DirectMenu(fdr);
                case "Handoff": return HandoffMenu(track, false);
                case "Depart": return TimeMenu("depart", "TIME", DateTime.UtcNow, selected: false, cancelButton: false);
                case "Finish":
                    return Show(new ActionUi
                    {
                        Kind = "confirm", Action = "finish",
                        Title = "Cancel " + fdr.Callsign + "?",
                        Text = "This will finish the FDR for " + fdr.Callsign + ".",
                    });
            }
        }

        // Everything else is the vatSys menu item itself
        var click = chosen;
        switch (r.Arg)
        {
            case "Flightplan": return OnPc(click.PerformClick, "Flight plan window");
            case "CPDLC": return OnPc(click.PerformClick, "CPDLC editor");
            case "SELCAL": return OnPc(click.PerformClick, "SELCAL window");
            case "Edit":
            case "Quicktag": return OnPc(click.PerformClick, "Quick tag window");
            case "Reroute": return OnPc(click.PerformClick, "Graphic reroute started; it is drawn on the ASD");
            default:
                click.PerformClick();
                return Done();
        }
    }

    private static ActionResult FinishChoice(ActionRequest r, Track track)
    {
        var fdr = Fdr(r, track);
        if (fdr is null || r.Arg != "yes") return Done();
        FDP2.CancelFDR(fdr, sendEst: true);
        return Done();
    }

    // ---- Direct: MMI.OpenDirectToMenu ----

    private static List<FDP2.FDR.ExtractedRoute.Segment> DirectWaypoints(FDP2.FDR fdr)
    {
        var index = fdr.ParsedRoute.OverflownIndex;
        if (index < 0) index = 0;
        var start = fdr.ParsedRoute[index];
        return fdr.ParsedRoute.ToList().SkipWhile(s => s != start).Skip(1)
            .Where(s => s.Type == FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.WAYPOINT).ToList();
    }

    private static ActionResult DirectMenu(FDP2.FDR fdr)
    {
        if (!fdr.HavePermission || fdr.ParsedRoute.Count <= 0) return Done();
        var waypoints = DirectWaypoints(fdr);
        if (waypoints.Count == 0) return Done();

        var ui = new ActionUi { Kind = "menu", Action = "direct", Title = fdr.Callsign, MaxVisible = 10 };
        for (var i = 0; i < waypoints.Count; i++)
        {
            var name = waypoints[i].Intersection.Name;
            ui.Items.Add(new ActionUiItem { Text = name, Arg = i + "|" + name });
        }
        return Show(ui);
    }

    private static ActionResult DirectChoice(ActionRequest r, Track track)
    {
        var fdr = Fdr(r, track);
        if (fdr is null) return Done();
        if (!fdr.HavePermission) return Fail(NoPermission);
        if (fdr.ParsedRoute.Count <= 0) return Done();

        var bar = r.Arg.IndexOf('|');
        var waypoints = DirectWaypoints(fdr);
        if (bar <= 0 || !int.TryParse(r.Arg.Substring(0, bar), out var index) || index < 0 || index >= waypoints.Count ||
            waypoints[index].Intersection.Name != r.Arg.Substring(bar + 1))
        {
            return Fail("The route has changed; open the menu again.");
        }

        VatSysInternals.Invoke(typeof(MMI), null, "DirectToMenuModifyRoute", fdr, null, waypoints[index]);
        return Done();
    }

    // ---- Label_Runway: MMI.OpenRWYMenu ----

    private static List<Airspace2.SystemRunway> Runways(FDP2.FDR fdr)
    {
        var airport = fdr.ATD != DateTime.MaxValue ? fdr.DesAirport : fdr.DepAirport;
        return Airspace2.GetRunways(airport) ?? new List<Airspace2.SystemRunway>();
    }

    private static ActionResult RunwayMenu(FDP2.FDR fdr)
    {
        if (!fdr.HavePermission) return Done();
        var ui = new ActionUi { Kind = "menu", Action = "rwy", Title = fdr.Callsign };
        foreach (var runway in Runways(fdr)) ui.Items.Add(new ActionUiItem { Text = runway.Name, Arg = "R|" + runway.Name });
        ui.Items.Add(new ActionUiItem { Text = "None", Arg = "N|" });
        return Show(ui);
    }

    private static ActionResult RunwayChoice(ActionRequest r, Track track)
    {
        var fdr = Fdr(r, track);
        if (fdr is null) return Done();
        if (!fdr.HavePermission) return Fail(NoPermission);

        Airspace2.SystemRunway? runway = null;
        if (r.Arg.StartsWith("R|", StringComparison.Ordinal))
        {
            var name = r.Arg.Substring(2);
            runway = Runways(fdr).FirstOrDefault(x => x.Name == name);
            if (runway is null) return Fail("Runway " + name + " is not available.");
        }

        if (fdr.ATD != DateTime.MaxValue) FDP2.SetArrivalRunway(fdr, runway);
        else FDP2.SetDepartureRunway(fdr, runway);
        return Done();
    }

    // ---- Label_CPDLC_Menu: MMI.OpenCPDLCMenu ----

    private static List<RadioMessage> RadioRequests(FDP2.FDR fdr)
    {
        var messages = Network.GetRadioMessages ?? new List<RadioMessage>();
        return messages.Where(m => m.Address == fdr.Callsign && m.Request && !m.Acknowledged).ToList();
    }

    private static object NetworkInstance() =>
        VatSysInternals.StaticMember(typeof(Network), "Instance") ?? throw new InvalidOperationException("Not connected.");

    private static ActionResult CpdlcMenu(FDP2.FDR fdr)
    {
        var ui = new ActionUi { Kind = "menu", Action = "cpdlc", Title = RadioRequests(fdr).Last().Address };
        foreach (var text in new[] { "Standby", "Request Deferred", "Unable", "Editor...", "Close Request" })
            ui.Items.Add(new ActionUiItem { Text = text });
        return Show(ui);
    }

    private static ActionResult CpdlcChoice(ActionRequest r, Track track)
    {
        var fdr = Fdr(r, track);
        if (fdr is null) return Done();
        var request = RadioRequests(fdr).LastOrDefault();
        if (request is null) return Fail("The request has been closed.");

        switch (r.Arg)
        {
            case "Standby":
            case "Request Deferred":
                VatSysInternals.Invoke(typeof(Network), NetworkInstance(), "SendRadioMessage",
                    request.FSDFrequencies, request.Address, r.Arg, false, request);
                return Done();
            case "Unable":
                VatSysInternals.Invoke(typeof(Network), NetworkInstance(), "SendRadioMessage",
                    request.FSDFrequencies, request.Address, "Unable", false, request, true);
                return Done();
            case "Editor...":
                return OnPc(() => MMI.OpenCPDLCWindow(fdr, request), "CPDLC editor");
            case "Close Request":
                request.Acknowledged = true;
                return Done();
            default:
                return Fail("Unknown menu item.");
        }
    }

    // ---- Label_CFL_Popup: CFLMenu ----

    private static string LevelText(int level) => level == 2 ? "VSA" : level == 1 ? "000" : (level / 100).ToString("d3");

    /// <summary>ListBox.FindString: the first item starting with the text.</summary>
    private static int FindString(List<string> items, string text) =>
        items.FindIndex(i => i.StartsWith(text, StringComparison.OrdinalIgnoreCase));

    private static List<string> CflLevels()
    {
        var levels = new List<int>();
        for (var l = 590; l > 410; l -= 20) levels.Add(l);
        for (var l = 410; l > 180; l -= 10) levels.Add(l);
        for (var l = 180; l >= 0; l -= 5) levels.Add(l);
        var prime = MMI.PrimePosition;
        if (prime != null)
        {
            foreach (var option in prime.CFLOptions) levels.Add(option / 100);
            levels.Sort();
            levels.Reverse();
        }
        var items = levels.Select(l => l.ToString("d3")).ToList();
        items.Add("VSA");
        items.Add("NONE");
        return items;
    }

    private static string? CflQuickLevel()
    {
        var prime = MMI.PrimePosition;
        return prime != null && prime.CFLQuickLevel != -1 ? (prime.CFLQuickLevel / 100).ToString("D3") : null;
    }

    private static ActionResult CflMenu(Track track)
    {
        // MMI.OpenCFLMenu takes the track's own FDR, in a Ground window too
        var fdr = track.GetFDR();
        var quickTag = track.GetQuickTag();
        if ((fdr != null && !fdr.HavePermission) || (fdr is null && quickTag is null)) return Done();

        var items = CflLevels();
        var ui = new ActionUi
        {
            Kind = "list", Action = "cfl",
            Title = (fdr != null ? fdr.Callsign : quickTag!.Callsign)?.ToUpper(),
            Selected = new List<int>(), Buttons = new List<ActionUiButton>(),
        };
        foreach (var item in items) ui.Items.Add(new ActionUiItem { Text = item });

        var quick = CflQuickLevel();
        if (quick != null) ui.Buttons.Add(new ActionUiButton { Name = "quick", Text = quick });

        var defaultLevel = VatSysInternals.StaticMember(VatSysInternals.VatSysType("vatsys.CFLMenu"), "CFL_DEFAULT_LEVEL") is int d ? d : 18000;
        void Select(int level)
        {
            var index = FindString(items, LevelText(level));
            if (index != -1) ui.Selected!.Add(index);
        }
        void SelectAndShow(int level)
        {
            var index = FindString(items, LevelText(level));
            if (index == -1) return;
            ui.Selected!.Add(index);
            ui.Top = index - 3 >= 0 ? index - 3 : index;
        }
        void ShowDefault(int fallbackLevel)
        {
            int index;
            if (defaultLevel != -1)
            {
                index = FindString(items, (defaultLevel / 100).ToString("d3"));
                if (index - 3 >= 0) index -= 3;
            }
            else
            {
                index = fallbackLevel >= 0 ? FindString(items, (fallbackLevel / 100).ToString("d3")) : -1;
            }
            ui.Top = index != -1 ? index : items.Count - 1;
        }

        if (fdr != null)
        {
            var block = fdr.CFLLower != -1;
            ui.Buttons.Add(new ActionUiButton { Name = "vis", Text = "VISUAL", Toggle = true, Checked = fdr.CFLVisual });
            ui.Buttons.Add(new ActionUiButton { Name = "blk", Text = "BLK", Toggle = true, Checked = block });
            if (Network.PrimaryFrequencySet) ui.Buttons.Add(new ActionUiButton { Name = "cpdlc", Text = "CPDLC", Toggle = true });

            if (block) { Select(fdr.CFLLower); Select(fdr.CFLUpper); }
            else if (fdr.CFLUpper != -1) SelectAndShow(fdr.CFLUpper);
            else ShowDefault(fdr.RFL);
        }
        else if (quickTag!.CFL != -1)
        {
            SelectAndShow(quickTag.CFL);
        }
        else
        {
            ShowDefault(-1);
        }

        return Show(ui);
    }

    /// <summary>A level as CFLMenu reads its list: VSA = 2, 000 = 1, NONE = -1.</summary>
    private static int ParseLevel(string text) =>
        text == "VSA" ? 2 : text == "000" ? 1 : text == "NONE" ? -1 : int.Parse(text, CultureInfo.InvariantCulture) * 100;

    private static ActionResult CflChoice(ActionRequest r, Track track)
    {
        var fdr = track.GetFDR();
        var quickTag = track.GetQuickTag();
        if (fdr != null && !fdr.HavePermission) return Fail(NoPermission);
        if (fdr is null && quickTag is null) return Done();

        var items = CflLevels();
        var visual = r.Toggles.Contains("vis");
        var cpdlc = fdr != null && r.Toggles.Contains("cpdlc");
        string? message = null;

        if (r.Arg == "#quick")
        {
            var quick = CflQuickLevel();
            if (quick is null) return Fail("No quick level is set.");
            var level = int.Parse(quick, CultureInfo.InvariantCulture) * 100;
            if (fdr != null)
            {
                FDP2.SetCFL(fdr, -1, level, visual);
                message = fdr.PRL != -1 && fdr.PRL > level ? "DESCEND TO AND MAINTAIN " + quick
                    : fdr.PRL != -1 && fdr.PRL < level ? "CLIMB TO AND MAINTAIN " + quick
                    : "MAINTAIN " + quick;
            }
            else
            {
                quickTag!.CFL = level;
            }
        }
        else if (fdr != null && r.Toggles.Contains("blk"))
        {
            // Two levels, in list order
            if (!items.Contains(r.Arg) || !items.Contains(r.Arg2)) return Fail("Pick two levels for a block.");
            FDP2.SetCFL(fdr, ParseLevel(r.Arg), ParseLevel(r.Arg2), visual);
            message = "MAINTAIN BLOCK " + r.Arg + " TO " + r.Arg2;
        }
        else
        {
            if (!items.Contains(r.Arg)) return Fail("Unknown level.");
            var level = ParseLevel(r.Arg);
            if (fdr != null)
            {
                FDP2.SetCFL(fdr, -1, level, visual);
                message = fdr.PRL > level ? "DESCEND TO AND MAINTAIN " + r.Arg
                    : fdr.PRL < level ? "CLIMB TO AND MAINTAIN " + r.Arg
                    : "MAINTAIN " + r.Arg;
            }
            else
            {
                quickTag!.CFL = level;
            }
        }

        // ASD_MouseDown: a CFL set from the label is highlighted as new
        track.NewCFL = true;

        if (cpdlc && message != null && fdr != null)
        {
            if (!Network.PrimaryFrequencySet)
            {
                Errors.Add(new Exception("No primary frequency set for CPDLC") { Source = "CPDLC" });
                return Fail("No primary frequency set for CPDLC");
            }
            var callsign = fdr.Callsign;
            var request = (Network.GetRadioMessages ?? new List<RadioMessage>())
                .LastOrDefault(m => !m.History && m.Request && m.Address == callsign);
            VatSysInternals.Invoke(typeof(Network), NetworkInstance(), "SendRadioMessage",
                callsign, message, true, request, request != null);
        }

        return Done();
    }

    // ---- Label_PRL_Popup: LevelMenu ----

    private static List<string> PlainLevels()
    {
        var items = new List<string>();
        for (var l = 590; l > 410; l -= 20) items.Add(l.ToString("d3"));
        for (var l = 410; l > 180; l -= 10) items.Add(l.ToString("d3"));
        for (var l = 180; l >= 0; l -= 5) items.Add(l.ToString("d3"));
        items.Add("NONE");
        return items;
    }

    private static ActionResult PrlMenu(FDP2.FDR? fdr)
    {
        if (fdr is null || fdr.CoupledTrack != null) return Done();

        var items = PlainLevels();
        var ui = new ActionUi { Kind = "list", Action = "prl", Title = fdr.Callsign, Selected = new List<int>() };
        foreach (var item in items) ui.Items.Add(new ActionUiItem { Text = item });

        if (fdr.PRL != -1)
        {
            var index = FindString(items, (fdr.PRL / 100).ToString("d3"));
            if (index != -1)
            {
                ui.Selected.Add(index);
                ui.Top = index - 3 >= 0 ? index - 3 : index;
            }
        }
        else
        {
            // LevelMenu.LVL_DEFAULT_LEVEL
            var index = FindString(items, "180");
            if (index - 3 >= 0) index -= 3;
            ui.Top = index != -1 ? index : items.Count - 1;
        }
        return Show(ui);
    }

    private static ActionResult PrlChoice(ActionRequest r, Track track)
    {
        var fdr = Fdr(r, track);
        if (fdr is null || fdr.CoupledTrack != null) return Done();
        if (!PlainLevels().Contains(r.Arg)) return Fail("Unknown level.");
        fdr.PRL = r.Arg == "NONE" ? -1 : int.Parse(r.Arg, CultureInfo.InvariantCulture) * 100;
        return Done();
    }

    // ---- Label_WX_L_Popup / Label_WX_R_Popup: DiversionMenu ----

    private static List<string> Deviations()
    {
        var items = new List<string>();
        for (var d = 500; d > 200; d -= 50) items.Add(d.ToString("d"));
        for (var d = 200; d > 60; d -= 20) items.Add(d.ToString("d"));
        for (var d = 60; d > 10; d -= 5) items.Add(d.ToString("d"));
        for (var d = 10; d >= 0; d--) items.Add(d.ToString("d"));
        items.Add("NONE");
        return items;
    }

    private static ActionResult OtdMenu(FDP2.FDR? fdr, string side)
    {
        if (fdr is null) return Done();

        var both = fdr.OTDLeft != 0 && fdr.OTDLeft == fdr.OTDRight;
        var items = Deviations();
        var ui = new ActionUi
        {
            Kind = "list", Action = "otd", Ctx = side, Title = fdr.Callsign, Selected = new List<int>(),
            Buttons = new List<ActionUiButton> { new() { Name = "lr", Text = "LR", Toggle = true, Checked = both } },
        };
        foreach (var item in items) ui.Items.Add(new ActionUiItem { Text = item });

        var value = both ? Math.Max(fdr.OTDLeft, fdr.OTDRight) : side == "L" ? fdr.OTDLeft : fdr.OTDRight;
        if (value == 0)
        {
            ui.Top = items.Count - 1;
        }
        else
        {
            var index = items.IndexOf(value.ToString("d"));
            if (index != -1)
            {
                ui.Selected.Add(index);
                ui.Top = index - 3 >= 0 ? index - 3 : index;
            }
        }
        return Show(ui);
    }

    private static ActionResult OtdChoice(ActionRequest r, Track track)
    {
        var fdr = Fdr(r, track);
        if (fdr is null) return Done();
        if (!Deviations().Contains(r.Arg)) return Fail("Unknown deviation.");

        var value = r.Arg == "NONE" ? 0 : int.Parse(r.Arg, CultureInfo.InvariantCulture);
        if (r.Toggles.Contains("lr")) { fdr.OTDLeft = value; fdr.OTDRight = value; }
        else if (r.Ctx == "L") fdr.OTDLeft = value;
        else if (r.Ctx == "R") fdr.OTDRight = value;
        else return Fail("Unknown side.");
        return Done();
    }

    // ---- Label_OpData_Popup: the text field ASD_MouseDown puts over the item ----

    private static ActionResult OpDataField(ActionRequest r, Track track)
    {
        var fdr = Fdr(r, track);
        string text;
        if (fdr != null)
        {
            if (!fdr.HavePermission) return Done();
            text = fdr.LabelOpData ?? "";
        }
        else if (track.GetQuickTag() != null)
        {
            text = track.GetQuickTag().LabelData ?? "";
        }
        else
        {
            return Done();
        }
        return Show(new ActionUi { Kind = "text", Action = "opdata", Value = text, MaxLength = 11, Entry = true });
    }

    private static ActionResult OpDataChoice(ActionRequest r, Track track)
    {
        var text = r.Arg.ToUpperInvariant();
        if (text.Length > 11) text = text.Substring(0, 11);

        var fdr = Fdr(r, track);
        if (fdr != null)
        {
            if (!fdr.HavePermission) return Fail(NoPermission);
            FDP2.SetLabelData(fdr, text);
        }
        else if (track.GetQuickTag() != null)
        {
            track.GetQuickTag().LabelData = text;
        }
        return Done();
    }

    // ---- TimeMenu: the SAR time (Label_SAR_Popup) and the departure time (Depart) ----

    /// <summary>TimeMenu: every minute from 90 after the seed down to 90 before it.
    /// The seed (to the tick) travels with the popup so the choice is read against
    /// the same list.</summary>
    private static ActionResult TimeMenu(string action, string title, DateTime seedTime, bool selected, bool cancelButton)
    {
        var seed = seedTime.Subtract(TimeSpan.FromSeconds(seedTime.Second));
        var ui = new ActionUi
        {
            Kind = "list", Action = action, Title = title, Selected = new List<int>(),
            Ctx = seedTime.Ticks.ToString(CultureInfo.InvariantCulture), Entry = true, MaxLength = 4,
        };
        if (cancelButton) ui.Buttons = new List<ActionUiButton> { new() { Name = "cancel", Text = "Cancel" } };

        var seedIndex = -1;
        for (var i = -90; i <= 90; i++)
        {
            var time = i == 0 ? seed : seedTime - TimeSpan.FromMinutes(i);
            if (i == 0) seedIndex = ui.Items.Count;
            ui.Items.Add(new ActionUiItem { Text = time.ToString("HHmm"), Arg = "T" + time.Ticks.ToString(CultureInfo.InvariantCulture) });
        }
        if (selected) ui.Selected.Add(seedIndex);

        var texts = ui.Items.Select(i => i.Text).ToList();
        var found = FindString(texts, seed.ToString("HHmm"));
        if (found != -1) ui.Top = found - 3 >= 0 ? found - 3 : found;
        return Show(ui);
    }

    /// <summary>The time chosen in a TimeMenu: a list item ("T" + ticks) or four
    /// typed digits, which are read against the seed's hour and minute.</summary>
    private static bool TryReadTime(ActionRequest r, out DateTime time, out string error)
    {
        time = DateTime.MaxValue;
        error = "";
        if (!long.TryParse(r.Ctx, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seedTicks) ||
            seedTicks < DateTime.MinValue.Ticks || seedTicks > DateTime.MaxValue.Ticks)
        {
            error = "Open the time menu again.";
            return false;
        }
        var seedTime = new DateTime(seedTicks, DateTimeKind.Utc);
        var seed = seedTime.Subtract(TimeSpan.FromSeconds(seedTime.Second));

        if (r.Arg.StartsWith("T", StringComparison.Ordinal))
        {
            if (!long.TryParse(r.Arg.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) ||
                Math.Abs(ticks - seedTicks) > TimeSpan.FromMinutes(91).Ticks)
            {
                error = "Unknown time.";
                return false;
            }
            time = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }

        if (r.Arg.Length != 4 || !r.Arg.All(char.IsDigit))
        {
            error = "Invalid time entered. Use 4 digit format HHmm";
            return false;
        }
        var hours = int.Parse(r.Arg.Substring(0, 2), CultureInfo.InvariantCulture) - seed.Hour;
        var minutes = int.Parse(r.Arg.Substring(2), CultureInfo.InvariantCulture) - seed.Minute;
        time = seed + TimeSpan.FromMinutes(hours * 60 + minutes);
        return true;
    }

    private static ActionResult SarMenu(FDP2.FDR? fdr)
    {
        if (fdr is null) return Done();
        return fdr.SarTime != DateTime.MaxValue
            ? TimeMenu("sar", fdr.Callsign, fdr.SarTime, selected: true, cancelButton: true)
            : TimeMenu("sar", fdr.Callsign, DateTime.UtcNow, selected: false, cancelButton: true);
    }

    private static ActionResult SarChoice(ActionRequest r, Track track)
    {
        var fdr = Fdr(r, track);
        if (fdr is null) return Done();

        if (r.Arg == "#cancel")
        {
            fdr.SarTime = DateTime.MaxValue;
        }
        else
        {
            if (!TryReadTime(r, out var time, out var error)) return Fail(error);
            fdr.SarTime = time;
        }

        // Alerts.Instance.RemoveAlerts(MMI.FindTrack(fdr), AlertTypes.SAR)
        var alertsType = VatSysInternals.VatSysType("vatsys.Alerts");
        var alerts = VatSysInternals.StaticMember(alertsType, "Instance");
        var alertTrack = MMI.FindTrack(fdr);
        if (alertsType != null && alerts != null && alertTrack != null)
            VatSysInternals.Invoke(alertsType, alerts, "RemoveAlerts", alertTrack, AlertTypes.SAR);
        return Done();
    }

    private static ActionResult DepartChoice(ActionRequest r, Track track)
    {
        var fdr = Fdr(r, track);
        if (fdr is null) return Done();
        if (!TryReadTime(r, out var time, out var error)) return Fail(error);
        FDP2.DepartFDR(fdr, time);
        return Done();
    }

    // ---- Handoff: HandoffWindow ----

    private static bool Online(string callsign)
    {
        var atcs = Network.GetOnlineATCs ?? new List<NetworkATC>();
        return atcs.Any(a => string.Equals(a.Callsign, callsign, StringComparison.OrdinalIgnoreCase));
    }

    private static List<SectorsVolumes.Sector> HandoffSectors()
    {
        var atcs = Network.GetOnlineATCs ?? new List<NetworkATC>();
        return (from s in SectorsVolumes.Sectors
                where s.HandoffEligible
                orderby atcs.Any(a => a.Callsign == s.Callsign) descending, s.Name
                select s).ToList();
    }

    /// <summary>Online controllers the profile has no handoff sector for.</summary>
    private static List<NetworkATC> UnknownControllers()
    {
        var atcs = Network.GetOnlineATCs ?? new List<NetworkATC>();
        return atcs.Where(a => a.IsRealATC && !SectorsVolumes.Sectors.Exists(s =>
            s.Callsign.ToUpperInvariant() == a.Callsign.ToUpperInvariant() && s.HandoffEligible)).ToList();
    }

    /// <summary>HandoffWindow.LoadSectors: the sector the route enters next, or the
    /// online sector that has taken it over.</summary>
    private static SectorsVolumes.Sector? NextSector(FDP2.FDR fdr)
    {
        var segment = fdr.ParsedRoute.ToList()
            .Where(s => s.Type == FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.ZPOINT && s.Tag is SectorsVolumes.Volume volume &&
                        fdr.ControllingSector != SectorsVolumes.FindSector(volume))
            .FirstOrDefault(s => s.ETO > DateTime.UtcNow);
        if (segment?.Tag is not SectorsVolumes.Volume next) return null;

        var sector = SectorsVolumes.FindSector(next);
        if (sector is null || ExactOnline(sector.Callsign)) return sector;

        SectorsVolumes.Sector? owner = null;
        foreach (var group in SectorsVolumes.SectorGroupings.Keys)
        {
            if (ExactOnline(group.Callsign) && group.SubSectors.Contains(sector) &&
                (owner is null || owner.SubSectors.Count > group.SubSectors.Count))
            {
                owner = group;
            }
        }
        return owner ?? sector;
    }

    private static bool ExactOnline(string callsign)
    {
        var atcs = Network.GetOnlineATCs ?? new List<NetworkATC>();
        return atcs.Any(a => a.Callsign == callsign && a.ValidATC);
    }

    private static ActionResult HandoffMenu(Track track, bool others)
    {
        // MMI.OpenHandoffMenu takes the track's own FDR
        var fdr = track.GetFDR();
        if (fdr is null || !fdr.HavePermission) return Done();

        var ui = new ActionUi { Kind = "handoff", Action = "handoff", Title = "Handoff", Ctx = others ? "other" : "sectors" };
        if (others)
        {
            foreach (var atc in UnknownControllers())
            {
                var callsign = atc.Callsign.ToUpperInvariant();
                ui.Items.Add(new ActionUiItem { Text = callsign, Arg = "A|" + atc.Callsign, Wide = true });
            }
        }
        else
        {
            var next = NextSector(fdr);
            var sectors = HandoffSectors();
            for (var i = 0; i < sectors.Count; i++)
            {
                var sector = sectors[i];
                ui.Items.Add(new ActionUiItem
                {
                    Text = sector.Name.ToUpperInvariant(),
                    Arg = "S|" + i + "|" + sector.Name,
                    Checked = next != null && next == sector,
                    Dim = !Online(sector.Callsign),
                });
            }
        }
        return Show(ui);
    }

    private static ActionResult HandoffChoice(ActionRequest r, Track track)
    {
        var fdr = track.GetFDR();
        if (fdr is null) return Done();
        if (!fdr.HavePermission) return Fail(NoPermission);

        if (r.Arg == "#other") return HandoffMenu(track, r.Ctx != "other");
        if (r.Arg == "#none")
        {
            MMI.HandoffToNone(fdr);
            return Done();
        }

        var parts = r.Arg.Split('|');
        if (parts.Length == 3 && parts[0] == "S")
        {
            var sectors = HandoffSectors();
            // The list is ordered by who is online, which may have changed since it was sent
            var sector = int.TryParse(parts[1], out var index) && index >= 0 && index < sectors.Count && sectors[index].Name == parts[2]
                ? sectors[index]
                : sectors.FirstOrDefault(s => s.Name == parts[2]);
            if (sector is null) return Fail("Sector " + parts[2] + " is not available.");
            MMI.HandoffJurisdiction(fdr, sector);
            return Done();
        }

        if (parts.Length == 2 && parts[0] == "A")
        {
            var atc = UnknownControllers().FirstOrDefault(a => a.Callsign == parts[1]);
            if (atc is null) return Fail(parts[1] + " is no longer online.");
            if (VatSysInternals.Invoke(typeof(SectorsVolumes), null, "CreateDummySector", atc.Callsign) is not SectorsVolumes.Sector dummy)
                return Fail("Handoff to " + parts[1] + " is not available.");
            MMI.HandoffJurisdiction(fdr, dummy);
            return Done();
        }

        return Fail("Unknown handoff target.");
    }
}
