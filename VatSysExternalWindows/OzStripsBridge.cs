using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using vatsys;

namespace VatSysExternalWindows;

/// <summary>
/// Reads and drives the OzStrips plugin (electronic flight strips) for the
/// <c>/ozstrips</c> page.
///
/// OzStrips is a separate plugin that may be absent or a different version, so
/// nothing here references it at compile time: every member is reached by
/// reflection through <see cref="Refl"/> and a miss degrades to "not available"
/// (state) or an error result (commands). All of it runs on the vatSys GUI
/// thread, which owns OzStrips' WinForms controls and its unsynchronised lists.
/// Member names were taken from the decompiled OzStrips build installed
/// alongside this plugin; the private/internal ones are listed where they are
/// used so an OzStrips update that breaks them is easy to trace.
/// </summary>
internal static class OzStripsBridge
{
    private const string GuiNamespace = "MaxRumsey.OzStripsPlugin.GUI";
    private const string OpenWindowReason = "OzStrips is not running";
    private const string StartingReason = "Starting OzStrips...";
    private const string NotInstalledReason = "OzStrips is not installed";

    /// <summary>How long a request waits for the GUI thread.</summary>
    private const int WaitMs = 300;
    /// <summary>Pages polling at once share a snapshot this fresh.</summary>
    private const int ShareMs = 200;
    /// <summary>A snapshot older than this is not shown as current when the GUI stops answering.</summary>
    private const int StaleMs = 5000;
    /// <summary>A queued build that has not run after this long is assumed lost (no main window yet).</summary>
    private const int RequeueMs = 2000;
    private const int TypeScanIntervalMs = 2000;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------

    private static readonly object Gate = new();
    private static StateDto? _last;      // guarded by Gate
    private static long _queuedAt = -1;  // Clock ms when a build was queued; -1 = none pending
    private static volatile bool _changed; // a command ran since the last build

    /// <summary>A JSON-serialisable snapshot of the OzStrips window. Always
    /// returns an object; <c>Available = false</c> carries a <c>Reason</c>.</summary>
    public static object GetState()
    {
        try
        {
            if (FindControllerType() is null) return StateDto.Unavailable(NotInstalledReason);

            StateDto? before;
            lock (Gate) before = _last;
            if (before is not null && !_changed && Clock.ElapsedMilliseconds - before.BuiltAt < ShareMs) return before;

            // One build in flight at a time, however many pages are polling
            var now = Clock.ElapsedMilliseconds;
            var queued = Interlocked.Read(ref _queuedAt);
            if ((queued < 0 || now - queued > RequeueMs) &&
                Interlocked.CompareExchange(ref _queuedAt, now, queued) == queued)
            {
                try { MMI.InvokeOnGUI(BuildOnGui); }
                catch { Interlocked.Exchange(ref _queuedAt, -1); }
            }

            lock (Gate)
            {
                if (ReferenceEquals(_last, before)) Monitor.Wait(Gate, WaitMs);
                // GUI busy: the last snapshot stands in until it is too old to pass as current
                if (_last is not null && Clock.ElapsedMilliseconds - _last.BuiltAt < StaleMs) return _last;
            }

            return StateDto.Unavailable("vatSys is busy");
        }
        catch (Exception ex)
        {
            return StateDto.Unavailable("OzStrips could not be read: " + Describe(ex));
        }
    }

    private static void BuildOnGui()
    {
        StateDto dto;
        _changed = false;
        try { dto = BuildState(); }
        catch (Exception ex) { dto = StateDto.Unavailable("OzStrips could not be read: " + Describe(ex)); }

        dto.BuiltAt = Clock.ElapsedMilliseconds;
        lock (Gate)
        {
            _last = dto;
            Monitor.PulseAll(Gate);
        }
        Interlocked.Exchange(ref _queuedAt, -1);
    }

    private static StateDto BuildState()
    {
        var oz = Oz.Resolve(out var reason);
        if (oz is null)
        {
            // Not running: the page may start it (the "start" command)
            var unavailable = StateDto.Unavailable(reason);
            if (reason == OpenWindowReason && FindPlugin() is not null)
            {
                unavailable.CanStart = true;
                unavailable.Reason = StartingReason;
            }
            return unavailable;
        }

        var bm = oz.BayManager;
        var aerodrome = Refl.Str(bm, "AerodromeName");
        var aeroState = Refl.Get(bm, "AerodromeState");
        var connection = Refl.Get(oz.SocketConn, "State")?.ToString() ?? "DISCONNECTED";
        var connections = Strings(Refl.Get(aeroState, "Connections"));
        connections.Sort(StringComparer.Ordinal);
        var cdmEnabled = Refl.Get(Refl.Get(aeroState, "CDMParameters"), "Enabled") is true;

        var dto = new StateDto
        {
            Available = true,
            Aerodrome = aerodrome,
            // The ATIS letter and METAR only exist as what the window shows:
            // MainForm.ATISLabel, and MainFormController._metar (private field)
            Atis = (Refl.Get(oz.MainForm, "ATISLabel") as Control)?.Text ?? "",
            Metar = Refl.Str(oz.Controller, "_metar"),
            UtcTime = DateTime.UtcNow.ToString("HH:mm:ss", Inv),
            ConnectionState = connection,
            Connected = connection == "CONNECTED",
            Connections = connections,
            ConnectionsCount = connections.Count,
            PendingPDCs = CountPendingPdcs(bm, aeroState),
            AutoFillAvailable = AutoFillAvailable(oz),
            StripScale = Math.Round(Math.Max(1.0, Math.Min(3.0, Convert.ToDouble(Refl.Get(oz.Settings, "StripScale") ?? 1f, Inv))), 3),
            StripColours = StripColours(oz),
            Aerodromes = Strings(Refl.Get(oz.AerodromeManager, "AerodromeList")),
            RunwayPairs = RunwayPairs(oz, aerodrome),
            SmartResizeMode = Refl.Int(oz.Settings, "SmartResize", 3),
            KeepStripPicked = Refl.Bool(oz.Settings, "KeepStripPicked"),
            CircuitActive = Refl.Bool(bm, "CircuitActive"),
            CoordinatorActive = Refl.Bool(bm, "CoordinatorBayActive"),
            CdmEnabled = cdmEnabled,
            CdmRates = cdmEnabled ? CdmRates(aeroState) : new List<CdmRateDto>(),
            PdcSoundEnabled = oz.MenuItem("PDCSoundMenuItem")?.Checked ?? true,
            GroundMapsInhibited = oz.MenuItem("InhibitGroundMaps")?.Checked ?? false,
            // OzStrips enables this item only for a circuit-capable aerodrome with send permission
            CircuitToggleAvailable = oz.MenuItem("ToggleCircuitToolStrip")?.Enabled ?? false,
            Layouts = MenuTexts(oz.MenuItem("ViewListToolStrip")),
            CurrentLayout = CurrentLayout(oz),
        };

        var ctx = new StripContext(bm, aeroState, oz.Settings);
        var layout = LayoutFor(oz, aerodrome, dto.CurrentLayout);

        foreach (var bay in Snapshot(Refl.Get(Refl.Get(bm, "BayRepository"), "Bays")))
        {
            if (Refl.Get(bay, "BayTypes") is not IList types || types.Count == 0 || types[0] is null) continue;

            var name = Refl.Str(bay, "Name");
            var bayDto = new BayDto
            {
                Bay = types[0].ToString(),
                BayId = Convert.ToInt32(types[0], Inv),
                Name = name,
                // The layout's own column. Bay.VerticalBoardNumber is where the
                // desktop window currently puts the bay, which follows its width.
                Column = layout.Columns.TryGetValue(name, out var column) ? column : Refl.Int(bay, "VerticalBoardNumber", 0),
                CdmDisplay = layout.CdmBays.Contains(name),
            };

            // OzStrips draws a bay from the end of its list to the start
            var items = Snapshot(Refl.Get(bay, "Strips"));
            for (var i = items.Length - 1; i >= 0; i--)
            {
                var item = items[i];
                switch (Refl.Get(item, "Type")?.ToString())
                {
                    case "STRIP":
                    {
                        var strip = Refl.Get(item, "Strip");
                        if (strip is null) break;
                        StripDto? stripDto;
                        try { stripDto = BuildStrip(strip, ctx); }
                        catch { stripDto = null; }
                        if (stripDto is not null) bayDto.Items.Add(new BayItemDto { ItemType = "STRIP", Strip = stripDto });
                        break;
                    }

                    case "QUEUEBAR":
                    {
                        // As BayRenderController.Paint labels it: the strips below the bar
                        var queued = 0;
                        for (var j = i; j >= 0; j--)
                        {
                            if (Refl.Get(items[j], "Type")?.ToString() == "STRIP") queued++;
                        }
                        bayDto.Items.Add(new BayItemDto { ItemType = "QUEUEBAR", Text = "Queue (" + queued.ToString(Inv) + ")" });
                        break;
                    }

                    case "BAR":
                        bayDto.Items.Add(new BayItemDto
                        {
                            ItemType = "BAR",
                            Text = Refl.Str(item, "BarText"),
                            Style = Refl.Get(item, "Style") is int barStyle ? barStyle : 0, // StripListItem.Style is internal
                        });
                        break;
                }
            }

            dto.Bays.Add(bayDto);
        }

        return dto;
    }

    /// <summary>What does not change from strip to strip within one snapshot.</summary>
    private sealed class StripContext
    {
        public StripContext(object bayManager, object? aerodromeState, object? settings)
        {
            Picked = Refl.Get(bayManager, "PickedStrip");
            LastTransmit = Refl.Bool(settings, "ShowLastTransmit")
                ? Refl.Str(Refl.Get(bayManager, "LastTransmitManager"), "LastReceivedFrom")
                : "";
            WorldFlight = new HashSet<string>(Strings(Refl.Get(aerodromeState, "WorldFlightTeams")), StringComparer.Ordinal);
            NoSlot = new HashSet<string>(Strings(Refl.Get(aerodromeState, "InvalidDestinationAircraft")), StringComparer.Ordinal);
        }

        public object? Picked { get; }
        public string LastTransmit { get; }
        public HashSet<string> WorldFlight { get; }
        public HashSet<string> NoSlot { get; }
    }

    /// <summary>One strip, with the text and colours StripView gives each element.</summary>
    private static StripDto? BuildStrip(object strip, StripContext ctx)
    {
        if (Refl.Get(strip, "FDR") is not FDP2.FDR fdr) return null;

        var callsign = fdr.Callsign ?? "";
        var stripType = Refl.Get(strip, "StripType")?.ToString() ?? "UNKNOWN";

        var firstWpt = Refl.Str(strip, "FirstWpt");
        if (firstWpt.Length > 5) firstWpt = firstWpt.Substring(0, 5) + "...";

        var cfl = Refl.Str(strip, "CFL");
        if (cfl.Contains("B")) cfl = "BLK";

        var gate = Refl.Str(strip, "Gate");
        var allocatedBay = Refl.Str(strip, "AllocatedBay");
        var standAutofilled = allocatedBay.Length > 0 && gate.Length == 0;

        var pdcFlags = Refl.Get(strip, "PDCFlags");
        var pdcSent = HasFlag(pdcFlags, "SENT");
        var pdcRequested = false;
        var pdcNeedsAck = false;
        if (stripType != "ARRIVAL")
        {
            var request = Refl.Get(strip, "PDCRequest");
            pdcRequested = request is not null && HasFlag(Refl.Get(request, "Flags"), "REQUESTED") && !pdcSent;
            pdcNeedsAck = pdcRequested && !HasFlag(pdcFlags, "ACKNOWLEDGED");
        }

        var cdm = Refl.Get(strip, "CDMResult");
        var cdmActive = cdm is not null && Refl.Get(Refl.Get(cdm, "Aircraft"), "State")?.ToString() != "PUSHED";

        var tot = "00:00";
        if (Refl.Get(strip, "TakeOffTime") is DateTime takeOff)
            tot = (DateTime.UtcNow - takeOff).ToString(@"mm\:ss", Inv);

        return new StripDto
        {
            Callsign = callsign,
            CLX = Refl.Str(strip, "CLX"),
            Gate = standAutofilled ? allocatedBay : gate,
            Remark = Refl.Str(strip, "Remark"),
            DepartureFreq = Refl.Str(strip, "DepartureFrequency"),
            Runway = Refl.Str(strip, "RWY"),
            CockLevel = Refl.Int(strip, "CockLevel", 0),
            Bay = Refl.Get(strip, "CurrentBay")?.ToString() ?? "",
            StripType = stripType,
            Time = Refl.Str(strip, "Time"),
            AircraftType = fdr.AircraftType ?? "",
            WTC = fdr.AircraftWake ?? "",
            SSR = fdr.AssignedSSRCode == -1 ? "XXXX" : Convert.ToString(fdr.AssignedSSRCode, 8).PadLeft(4, '0'),
            ADES = fdr.DesAirport ?? "",
            SID = Refl.Str(strip, "SID"),
            FirstWpt = firstWpt,
            RFL = Refl.Str(strip, "RFL"),
            CFL = cfl,
            GLOP = fdr.GlobalOpData ?? "",
            FlightRules = fdr.FlightRules ?? "",
            Ready = Refl.Bool(strip, "Ready"),
            RouteIndicator = fdr.TextOnly ? "T" : fdr.ReceiveOnly ? "R" : "",
            AllocatedBay = allocatedBay,
            Crossing = Refl.Bool(strip, "Crossing"),
            HDG = Refl.Str(strip, "HDG"),
            Tot = tot,
            PDCIndicator = fdr.PDCSent || pdcSent ? "P" : "",
            PdcRequested = pdcRequested,
            PdcNeedsAck = pdcNeedsAck,
            SidTransition = Refl.Get(strip, "SIDTransition") is not null,
            RouteAlert = AlertActive(strip, "ROUTE"),
            RflAlert = AlertActive(strip, "RFL"),
            SsrAlert = AlertActive(strip, "SSR"),
            ReadyAlert = AlertActive(strip, "READY"),
            NoHdgAlert = AlertActive(strip, "NO_HDG"),
            VfrSidAlert = AlertActive(strip, "VFR_SID"),
            CdmActive = cdmActive,
            CdmReadyToPush = cdmActive && Refl.Bool(strip, "ReadyToPush"),
            CdmHasSlot = cdmActive && Refl.Get(cdm, "Slot") is not null,
            StandAutofilled = standAutofilled,
            Picked = ReferenceEquals(ctx.Picked, strip),
            LastTransmit = ctx.LastTransmit.Length > 0 && ctx.LastTransmit == callsign,
            WorldFlight = ctx.WorldFlight.Contains(callsign),
            NoSlot = ctx.NoSlot.Contains(callsign),
        };
    }

    /// <summary>Strip.IsAlertActive(AlertTypes), false when it cannot be asked.</summary>
    private static bool AlertActive(object strip, string alert)
    {
        try
        {
            var value = AlertValue(strip, alert);
            return value is not null && Refl.Call(strip, "IsAlertActive", value) is true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The AlertTypes value called <paramref name="alert"/>; the enum's
    /// type is taken from Strip.InhibitedAlerts.</summary>
    private static object? AlertValue(object strip, string alert)
    {
        var type = Refl.Get(strip, "InhibitedAlerts")?.GetType();
        return type is { IsEnum: true } ? EnumValue(type, alert) : null;
    }

    /// <summary>As MainFormController.UpdateTimer counts them: requested, the
    /// aircraft has a strip here, and no PDC sent yet.</summary>
    private static int CountPendingPdcs(object bayManager, object? aerodromeState)
    {
        try
        {
            var repository = Refl.Get(bayManager, "StripRepository");
            if (repository is null) return 0;

            var count = 0;
            foreach (var request in Snapshot(Refl.Get(aerodromeState, "PDCRequests")))
            {
                if (!HasFlag(Refl.Get(request, "Flags"), "REQUESTED")) continue;
                var strip = Refl.Call(repository, "GetStrip", Refl.Str(request, "Callsign"));
                if (strip is not null && !HasFlag(Refl.Get(strip, "PDCFlags"), "SENT")) count++;
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    private static bool AutoFillAvailable(Oz oz)
    {
        // BayManager.AutoAssigner is internal; the window's "AutoFill Status"
        // item shows the same answer in its colour, so fall back to that.
        var assigner = Refl.Get(oz.BayManager, "AutoAssigner");
        if (assigner is not null && Refl.Get(assigner, "IsFunctional") is bool functional) return functional;

        var item = oz.MenuItem("AutoFillAvailable");
        return item is not null && item.BackColor.ToArgb() == System.Drawing.Color.LightGreen.ToArgb();
    }

    /// <summary>Strip background per type from OzStrips' AerodromeSettings.xml,
    /// keyed by StripType name. Only plain hex colours are passed on.</summary>
    private static Dictionary<string, string> StripColours(Oz oz)
    {
        var result = new Dictionary<string, string>();
        foreach (var colour in Snapshot(Refl.GetStatic(Oz.TypeOf("AerodromeManager"), "StripColours")))
        {
            var key = Refl.Get(colour, "Type")?.ToString();
            var hex = Refl.Str(colour, "Colour").Trim().TrimStart('#');
            if (string.IsNullOrEmpty(key) || (hex.Length != 6 && hex.Length != 3) || !hex.All(Uri.IsHexDigit)) continue;
            result[key!] = "#" + hex;
        }
        return result;
    }

    private static List<string> RunwayPairs(Oz oz, string aerodrome)
    {
        var result = new List<string>();
        var settings = Refl.Get(oz.AerodromeManager, "Settings");
        foreach (var autoMap in Snapshot(Refl.Get(settings, "AutoMapAerodromes")))
        {
            if (Refl.Str(autoMap, "ICAOCode") != aerodrome) continue;

            foreach (var pair in Strings(Refl.Get(autoMap, "RunwayPairs")))
            {
                var clean = NormaliseRunwayPair(pair);
                if (clean.Length > 0 && !result.Contains(clean)) result.Add(clean);
            }
            break;
        }
        return result;
    }

    private static string CurrentLayout(Oz oz)
    {
        // MainFormController._layoutName is a private field; the window title
        // ("OzStrips :: YSSY-VATSIM :: All") ends with the same name.
        var name = Refl.Str(oz.Controller, "_layoutName");
        if (name.Length > 0) return name;

        var title = oz.MainForm?.Text ?? "";
        var at = title.LastIndexOf(" :: ", StringComparison.Ordinal);
        return at >= 0 ? title.Substring(at + 4) : "";
    }

    /// <summary>What AerodromeSettings.xml says about the bays of one layout.</summary>
    private sealed class LayoutInfo
    {
        /// <summary>Bay name to the column the layout gives it.</summary>
        public Dictionary<string, int> Columns { get; } = new();
        /// <summary>Bays that show the CDM rates in place of the queue buttons.</summary>
        public HashSet<string> CdmBays { get; } = new();
    }

    private static object? _layoutSettings;
    private static string _layoutKey = "";
    private static LayoutInfo _layout = new();

    /// <summary>The current layout's bay columns (AerodromeSettings.Layouts) and
    /// which bays carry the CDM display (AerodromeSettings.Bays), read as
    /// MainFormController.AerodromeTypeChanged reads them. Recomputed only when
    /// the settings, aerodrome type or layout change.</summary>
    private static LayoutInfo LayoutFor(Oz oz, string aerodrome, string layoutName)
    {
        var settings = Refl.Get(oz.AerodromeManager, "Settings");
        if (settings is null) return new LayoutInfo();

        string type;
        try { type = Refl.Call(oz.AerodromeManager!, "GetAerodromeType", aerodrome) as string ?? ""; }
        catch { return new LayoutInfo(); }

        var key = type + "\n" + layoutName;
        if (ReferenceEquals(_layoutSettings, settings) && _layoutKey == key) return _layout;

        var result = new LayoutInfo();
        foreach (var layout in Snapshot(Refl.Get(settings, "Layouts")))
        {
            if (Refl.Str(layout, "Type") != type || Refl.Str(layout, "Name") != layoutName) continue;

            foreach (var element in Snapshot(Refl.Get(layout, "Elements")))
            {
                result.Columns[Refl.Str(element, "Name")] = Refl.Int(element, "Column", 0);
            }
            break;
        }

        foreach (var bayType in Snapshot(Refl.Get(settings, "Bays")))
        {
            if (Refl.Str(bayType, "Type") != type) continue;

            foreach (var definition in Snapshot(Refl.Get(bayType, "Bays")))
            {
                if (Refl.Bool(definition, "CDMDisplay")) result.CdmBays.Add(Refl.Str(definition, "Name"));
            }
            break;
        }

        _layoutSettings = settings;
        _layoutKey = key;
        _layout = result;
        return result;
    }

    /// <summary>The departure counts CDMStatsRenderController paints in a CDM
    /// bay's header: for each period, departures against the rate's target,
    /// coloured by how far apart they are.</summary>
    private static List<CdmRateDto> CdmRates(object? aerodromeState)
    {
        var result = new List<CdmRateDto>();
        if (Refl.Get(Refl.Get(aerodromeState, "CDMStatistics"), "CDMRates") is not IDictionary rates) return result;

        var rate = Refl.Get(Refl.Get(aerodromeState, "CDMParameters"), "DefaultRate") is TimeSpan set ? set : TimeSpan.FromMinutes(2.0);
        var perHour = 60f / (float)rate.TotalMinutes;

        foreach (DictionaryEntry entry in rates) // IDictionary's own enumerator: the periods in order
        {
            if (entry.Key is not int minutes || entry.Value is not int count) continue;

            var target = (int)(perHour * (minutes / 60f));
            var over = count - target;
            result.Add(new CdmRateDto
            {
                Minutes = minutes,
                Count = count,
                Target = target,
                Colour = over == 0 ? "#008000"            // Green
                    : over > 0 && over <= 2 ? "#ff4500"  // OrangeRed
                    : over >= 3 ? "#ff0000"              // Red
                    : over >= -2 ? "#0000ff"             // Blue
                    : "#800080",                         // Purple
            });
        }
        return result;
    }

    private static List<string> MenuTexts(ToolStripMenuItem? menu)
    {
        var result = new List<string>();
        if (menu is null) return result;

        foreach (ToolStripItem item in menu.DropDownItems)
        {
            if (item is ToolStripMenuItem && !string.IsNullOrEmpty(item.Text)) result.Add(item.Text);
        }
        return result;
    }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    /// <summary>Commands that open a window in vatSys. One of them (Keyboard
    /// Settings) is shown with ShowDialog, which only returns when the window
    /// is closed, and any of the others could become modal in a later OzStrips;
    /// so these are queued and never waited for.</summary>
    private static readonly HashSet<string> WindowActions = new(StringComparer.Ordinal)
    {
        "openFdr", "openCdm", "openPdc", "openPm", "openReroute",
        "openSettings", "openKeySettings", "showAbout", "showSignalRLog", "overrideAtis",
    };

    private static readonly HashSet<string> StripActions = new(StringComparer.Ordinal)
    {
        "move", "moveUp", "moveDown", "moveToBarUp", "moveToBarDown", "cock", "ready", "sidTrigger",
        "toggleTot", "assignSsr", "openFdr", "showRoute", "openCdm", "openPdc", "openPm", "openReroute",
        "crossing", "inhibitAlert", "inhibitSidAlert", "inhibit", "flip", "setClx", "setRemark", "setGate",
        "setFreq", "setCfl", "setRwy", "setSid", "setGlop", "queueUp",
    };

    /// <summary>Performs one page command on the GUI thread. <paramref name="bay"/>
    /// is a StripBay name or number. Returns <c>Ok</c>, or <c>Ok = false</c> with an <c>Error</c>.</summary>
    public static object Execute(string action, string? callsign, string? bay, string? value, int? style)
    {
        try
        {
            action = (action ?? "").Trim();
            if (action.Length == 0) return CommandResult.Fail("No action given.");
            if (FindControllerType() is null) return CommandResult.Fail(NotInstalledReason);

            callsign = callsign?.Trim();
            bay = bay?.Trim();

            var done = new ManualResetEventSlim(false); // not disposed: the GUI may signal after the wait gave up
            CommandResult? result = null;

            MMI.InvokeOnGUI(delegate
            {
                try { result = Dispatch(action, callsign, bay, value, style); }
                catch (Exception ex) { result = CommandResult.Fail(Describe(ex)); }
                finally
                {
                    _changed = true; // the next poll must not be answered from before this command
                    try { done.Set(); } catch { }
                }
            });

            if (WindowActions.Contains(action)) return CommandResult.Pending();

            // A command still running after the wait (GUI busy, or OzStrips put
            // up a message box) will finish on its own; its outcome is not known here.
            return done.Wait(WaitMs) ? result ?? CommandResult.Success() : CommandResult.Pending();
        }
        catch (Exception ex)
        {
            return CommandResult.Fail(Describe(ex));
        }
    }

    // ------------------------------------------------------------------
    // Starting OzStrips without opening its window
    // ------------------------------------------------------------------

    private static object? _plugin;
    private static long _lastStart = -StartIntervalMs;
    private const int StartIntervalMs = 5000;

    /// <summary>The OzStrips plugin object vatSys loaded (Plugins.instance.iplugins,
    /// both private), or null.</summary>
    private static object? FindPlugin()
    {
        if (_plugin is not null) return _plugin;

        var pluginType = Oz.TypeOf("OzStrips");
        if (pluginType is null) return null;

        try
        {
            var plugins = Refl.GetStatic(typeof(MMI).Assembly.GetType("vatsys.Plugin.Plugins", false), "instance");
            if (Refl.Get(plugins, "iplugins") is IEnumerable loaded)
            {
                foreach (var plugin in loaded)
                {
                    if (pluginType.IsInstanceOfType(plugin)) return _plugin = plugin;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Starts OzStrips with its window minimised. Everything OzStrips
    /// knows lives in its window, and its server connection ignores every
    /// message while that window is not visible (SocketConn.MainFormValid), so
    /// it cannot run without one; a minimised window counts as visible, takes no
    /// screen space, and is one OzStrips already handles (its own menu item
    /// restores it). This is OzStrips.OpenGUI (private) with the window state
    /// set first; it uses the plugin's private _gui, _readyForConnection and
    /// _aerodromeManager fields.</summary>
    private static CommandResult StartInBackground()
    {
        var controller = Refl.GetStatic(FindControllerType(), "Instance");
        if (controller is not null && Refl.Get(controller, "IsDisposed") is false && Refl.Get(controller, "Visible") is true)
            return CommandResult.Success();

        // A failed start is not retried at once by every page that is polling
        var now = Clock.ElapsedMilliseconds;
        if (now - _lastStart < StartIntervalMs) return CommandResult.Pending();
        _lastStart = now;

        var plugin = FindPlugin();
        var mainFormType = Oz.TypeOf("MainForm");
        if (plugin is null || mainFormType is null) return CommandResult.Fail("OzStrips could not be started from here; open it from vatSys.");

        var owner = Application.OpenForms["MainForm"]; // the vatSys main window, as OzStrips finds it
        if (owner is null) return CommandResult.Fail("vatSys is still starting.");

        var gui = Refl.Get(plugin, "_gui") as Form;
        if (gui is null || gui.IsDisposed)
        {
            gui = Activator.CreateInstance(mainFormType, Refl.Get(plugin, "_readyForConnection") is true, Refl.Get(plugin, "_aerodromeManager")) as Form;
            if (gui is null) return CommandResult.Fail("OzStrips could not be started from here; open it from vatSys.");
            Refl.Set(plugin, "_gui", gui);
        }
        else if (gui.Visible)
        {
            return CommandResult.Success();
        }

        // Showing a window activates it, even minimised: hand the keyboard back
        var active = Form.ActiveForm;
        gui.WindowState = FormWindowState.Minimized;
        gui.Show(owner);
        if (active is not null && !active.IsDisposed && !ReferenceEquals(active, gui)) active.Activate();
        return CommandResult.Success();
    }

    private static CommandResult Dispatch(string action, string? callsign, string? bayArg, string? value, int? style)
    {
        if (action == "start") return StartInBackground();

        var oz = Oz.Resolve(out var reason);
        if (oz is null) return CommandResult.Fail(reason);

        var controller = oz.Controller;
        var bm = oz.BayManager;
        var clicked = new object[] { controller, EventArgs.Empty }; // (sender, e) for OzStrips' own click handlers

        switch (action)
        {
            case "setAerodrome":
            {
                var aerodrome = (value ?? "").Trim().ToUpperInvariant();
                if (aerodrome.Length != 4 || !aerodrome.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9'))
                    return CommandResult.Fail("Enter a 4-letter ICAO code.");
                Refl.Call(controller, "SetAerodrome", aerodrome);
                return CommandResult.Success();
            }

            case "setSmartResize":
            {
                if (!int.TryParse(value, NumberStyles.Integer, Inv, out var mode) || mode < 0 || mode > 3)
                    return CommandResult.Fail("Smart resize is 0 to 3.");
                Refl.Call(controller, "SetSmartResizeColumnMode", mode);
                return CommandResult.Success();
            }

            case "toggleCircuit":
            {
                // The window's own item: it carries OzStrips' enabled state for this aerodrome
                var item = oz.MenuItem("ToggleCircuitToolStrip");
                if (item is null) Refl.Call(controller, "ToggleCircuitBay", clicked); // internal
                else if (!item.Enabled) return CommandResult.Fail("The circuit bay cannot be toggled here.");
                else item.PerformClick();
                return CommandResult.Success();
            }

            case "toggleCoordinator":
                Refl.Call(controller, "ToggleCoordBay", clicked); // internal; its menu item is a private field
                return CommandResult.Success();

            case "toggleCdm":
                Refl.Call(controller, "ToggleCDM", clicked); // internal; its menu item is a private field
                return CommandResult.Success();

            case "setLayout":
            {
                var menu = oz.MenuItem("ViewListToolStrip");
                if (menu is null) return CommandResult.Fail("OzStrips' view modes could not be read.");
                foreach (ToolStripItem item in menu.DropDownItems)
                {
                    if (item is not ToolStripMenuItem || !string.Equals(item.Text, value, StringComparison.OrdinalIgnoreCase)) continue;
                    item.PerformClick();
                    return CommandResult.Success();
                }
                return CommandResult.Fail("No such view mode.");
            }

            case "togglePdcSound":
            {
                var item = oz.MenuItem("PDCSoundMenuItem");
                if (item is null) Refl.Call(controller, "PDCSoundToggle", clicked); // internal
                else item.PerformClick();
                return CommandResult.Success();
            }

            case "toggleGroundMaps":
            {
                var item = oz.MenuItem("InhibitGroundMaps");
                if (item is null) return CommandResult.Fail("OzStrips' ground map setting could not be read.");
                item.PerformClick();
                return CommandResult.Success();
            }

            case "openSettings":
                Refl.Call(controller, "ShowSettings", clicked);
                return CommandResult.Success();

            case "openKeySettings":
                Refl.Call(controller, "ShowKeySettings", clicked);
                return CommandResult.Success();

            case "showAbout":
            {
                // What MainForm.AboutToolStripMenuItem_Click (private) does
                var aboutType = Oz.TypeOf("Controls.About");
                var modalType = Oz.TypeOf("BaseModal");
                if (aboutType is null || modalType is null) return CommandResult.Fail("OzStrips' About window was not found.");
                if (Activator.CreateInstance(modalType, Activator.CreateInstance(aboutType), "About OzStrips") is Form modal)
                    modal.Show(oz.MainForm);
                return CommandResult.Success();
            }

            case "showSignalRLog":
                Refl.Call(controller, "ShowMessageList_Click", clicked);
                return CommandResult.Success();

            case "reloadStripElements":
            {
                var listType = Oz.TypeOf("DTO.StripElementList");
                if (listType is null) return CommandResult.Fail("OzStrips' strip layout loader was not found.");
                Refl.CallStatic(listType, "Load"); // internal static
                return CommandResult.Success();
            }

            case "reloadAerodromeList":
                if (oz.AerodromeManager is null) return CommandResult.Fail("OzStrips' aerodrome manager was not found.");
                Refl.Call(oz.AerodromeManager, "LoadSettings");
                return CommandResult.Success();

            case "overrideAtis":
                Refl.Call(controller, "OverrideATISClick", clicked);
                return CommandResult.Success();

            case "toggleCrossBar":
            case "toggleReleaseBar":
            {
                var pair = NormaliseRunwayPair(value);
                if (pair.Length < 2 || pair.Length % 2 != 0) return CommandResult.Fail("Not a runway pair.");
                Refl.Call(bm, "ToggleCrossReleaseBar", pair, action == "toggleReleaseBar" ? "Released" : "Crossing");
                return CommandResult.Success();
            }

            case "toggleCrossBarDirect":
            {
                // MainFormController.ToggleCrossBar without its desktop drop-down:
                // an aerodrome with runway pairs needs one named (toggleCrossBar)
                if (RunwayPairs(oz, Refl.Str(bm, "AerodromeName")).Count > 0)
                    return CommandResult.Fail("Choose the runway being crossed.");
                const string text = "XXX CROSSING XXX";
                if (Refl.Call(bm, "DeleteBarByParams", "Runway", 3, text) is not true) Refl.Call(bm, "AddBar", "Runway", 3, text);
                return CommandResult.Success();
            }

            case "toggleReleaseBarDirect":
                // OzStrips only ever releases a named runway pair
                return CommandResult.Fail("Choose the runway being released.");

            case "toggleQueueBar":
            {
                var bay = FindBay(bm, bayArg, out var error);
                if (bay is null) return CommandResult.Fail(error);
                Refl.Call(bay, "AddDivider", false);
                return CommandResult.Success();
            }

            case "addBar":
            {
                var bay = FindBay(bm, bayArg, out var error);
                if (bay is null) return CommandResult.Fail(error);
                var text = CleanText(value, 64, upper: false);
                if (text.Length == 0) return CommandResult.Fail("Enter bar text.");
                var barStyle = style is >= 1 and <= 3 ? style.Value : 1;
                Refl.Call(bay, "AddBar", barStyle, text);
                return CommandResult.Success();
            }

            // Not in the fork: the desktop removes a bar by picking it and
            // pressing INHIBIT, which a page cannot do without this.
            case "deleteBar":
            {
                var bay = FindBay(bm, bayArg, out var error);
                if (bay is null) return CommandResult.Fail(error);
                foreach (var item in Snapshot(Refl.Get(bay, "Strips")))
                {
                    if (Refl.Get(item, "Type")?.ToString() != "BAR" || Refl.Str(item, "BarText") != (value ?? "")) continue;
                    if (style.HasValue && !(Refl.Get(item, "Style") is int itemStyle && itemStyle == style.Value)) continue;

                    if (ReferenceEquals(Refl.Get(bm, "PickedStripItem"), item)) Refl.Call(bm, "RemovePicked", false, true);
                    Refl.Call(bay, "DeleteBar", item);
                    return CommandResult.Success();
                }
                return CommandResult.Fail("That bar is no longer there.");
            }
        }

        if (!StripActions.Contains(action)) return CommandResult.Fail("Unknown action '" + CleanText(action, 40, upper: false) + "'.");
        if (string.IsNullOrEmpty(callsign)) return CommandResult.Fail("No strip selected.");

        var repository = Refl.Get(bm, "StripRepository");
        var bays = Refl.Get(bm, "BayRepository");
        if (repository is null || bays is null) return CommandResult.Fail("OzStrips' strips could not be read.");

        var strip = Refl.Call(repository, "GetStrip", callsign);
        if (strip is null) return CommandResult.Fail("No strip for " + CleanText(callsign, 12, upper: false) + ".");
        if (Refl.Get(strip, "FDR") is not FDP2.FDR fdr) return CommandResult.Fail("That strip has no flight plan.");

        var stripController = Refl.Get(strip, "Controller");
        var picked = ReferenceEquals(Refl.Get(bm, "PickedStrip"), strip);

        switch (action)
        {
            case "move":
            {
                var bay = FindBay(bm, bayArg, out var error);
                if (bay is null) return CommandResult.Fail(error);
                Refl.Call(bm, "MoveStrip", bay, strip);
                // BayManager.DropStrip's follow-up when the desktop's picked strip is the one that moved
                if (picked)
                {
                    Refl.Set(bm, "PickedStripItem", Refl.Call(bay, "GetListItem", strip));
                    Refl.Call(bm, "RemovePicked", true);
                }
                return CommandResult.Success();
            }

            case "moveUp":
            case "moveDown":
            {
                if (!FindItem(bays, strip, out var bay, out var item)) return CommandResult.Fail("That strip is not in a bay.");
                Refl.Call(bay, "ChangeStripPosition", item, action == "moveUp" ? 1 : -1);
                return CommandResult.Success();
            }

            case "moveToBarUp":
            case "moveToBarDown":
            {
                if (!FindItem(bays, strip, out var bay, out var item)) return CommandResult.Fail("That strip is not in a bay.");
                MoveToNextBar(bay, item, action == "moveToBarUp" ? 1 : -1);
                return CommandResult.Success();
            }

            case "cock":
                Refl.Call(strip, "CockStrip");
                return CommandResult.Success();

            case "ready":
                Refl.Call(Required(stripController, "Strip.Controller"), "ToggleReady");
                return CommandResult.Success();

            case "sidTrigger":
                Refl.Call(strip, "SIDTrigger");
                return CommandResult.Success();

            case "toggleTot":
                Refl.Call(strip, "TakeOff");
                return CommandResult.Success();

            case "assignSsr":
                if (!IsController()) return CommandResult.Fail(NotControllerError);
                Refl.Call(Required(stripController, "Strip.Controller"), "AssignSSR");
                return CommandResult.Success();

            case "openFdr":
                MMI.OpenFPWindow(fdr);
                return CommandResult.Success();

            case "showRoute":
            {
                // StripView's SHOW_ROUTE
                var track = MMI.FindTrack(fdr);
                if (track is null) return CommandResult.Fail("That aircraft has no track.");
                if (track.GraphicRTE) MMI.HideGraphicRoute(track);
                else MMI.ShowGraphicRoute(track, true);
                return CommandResult.Success();
            }

            case "openCdm":
                Refl.Call(Required(stripController, "Strip.Controller"), "OpenCDM");
                return CommandResult.Success();

            case "openPdc":
            {
                // StripView's OPEN_PDC: a requested PDC is acknowledged and opens
                // OzStrips' PDC window, anything else opens vatSys's
                var controllerObj = Required(stripController, "Strip.Controller");
                var request = Refl.Get(strip, "PDCRequest");
                var requestFlags = Refl.Get(request, "Flags");
                if (request is not null && HasFlag(requestFlags, "REQUESTED") && Refl.Get(strip, "StripType")?.ToString() != "ARRIVAL")
                {
                    if (!HasFlag(requestFlags, "ACKNOWLEDGED") && Refl.Get(strip, "PDCFlags") is { } flags && flags.GetType().IsEnum)
                    {
                        var acknowledged = Convert.ToInt64(flags, Inv) | Convert.ToInt64(EnumValue(flags.GetType(), "ACKNOWLEDGED"), Inv);
                        Refl.Set(strip, "PDCFlags", Enum.ToObject(flags.GetType(), acknowledged));
                        Refl.Call(strip, "SyncStrip");
                    }
                    Refl.Call(controllerObj, "OpenPDCWindow");
                }
                else
                {
                    Refl.Call(controllerObj, "OpenVatSysPDCWindow");
                }
                return CommandResult.Success();
            }

            case "openPm":
                MMI.OpenPMWindow(fdr.Callsign);
                return CommandResult.Success();

            case "openReroute":
                Refl.Call(Required(stripController, "Strip.Controller"), "OpenRerouteMenu");
                return CommandResult.Success();

            case "crossing":
                Refl.Set(strip, "Crossing", !Refl.Bool(strip, "Crossing"));
                Refl.Call(Required(stripController, "Strip.Controller"), "SetCross");
                return CommandResult.Success();

            case "inhibitAlert":
            case "inhibitSidAlert":
            {
                var name = action == "inhibitSidAlert" ? "VFR_SID" : AlertName(value);
                if (name is null) return CommandResult.Fail("Unknown alert.");
                var alert = AlertValue(strip, name);
                if (alert is null) return CommandResult.Fail("OzStrips' alerts could not be read.");
                if (Refl.Call(strip, "IsAlertActive", alert) is true) Refl.Call(strip, "InhibitAlert", alert);
                return CommandResult.Success();
            }

            case "inhibit":
            {
                // BayManager.Inhibit, for this strip rather than the desktop's picked one
                var current = Refl.Get(strip, "CurrentBay");
                if (current is null || !current.GetType().IsEnum) return CommandResult.Fail("OzStrips' bays could not be read.");
                Refl.Set(strip, "CurrentBay", EnumValue(current.GetType(), "BAY_DEAD"));
                Refl.Call(strip, "SyncStrip");
                Refl.Call(bm, "UpdateBay", strip);
                if (picked) Refl.Call(bm, "RemovePicked", true, true);
                return CommandResult.Success();
            }

            case "flip":
                Refl.Call(strip, "FlipFlop");
                return CommandResult.Success();

            // The free-text fields, with the lengths OzStrips' own drop-downs allow
            case "setClx":
                return SetAndSync(strip, "CLX", value, 3);
            case "setRemark":
                return SetAndSync(strip, "Remark", value, 10);
            case "setGate":
                return SetAndSync(strip, "Gate", value, 4);
            case "setFreq":
                return SetAndSync(strip, "DepartureFrequency", value, 7);

            // These go to vatSys's flight plan through OzStrips' own setters,
            // which do nothing for an observer
            case "setCfl":
                if (value is null) return CommandResult.Fail("No value given.");
                if (!IsController()) return CommandResult.Fail(NotControllerError);
                Refl.Set(strip, "CFL", CleanText(value, 7, upper: true));
                return CommandResult.Success();

            case "setRwy":
            {
                if (value is null) return CommandResult.Fail("No value given.");
                if (!IsController()) return CommandResult.Fail(NotControllerError);
                var runway = CleanText(value, 3, upper: true);
                var runways = Airspace2.GetRunways(fdr.DepAirport);
                if (runways is null || !runways.Any(r => r.Name == runway)) return CommandResult.Fail("No runway " + runway + " at " + fdr.DepAirport + ".");
                Refl.Set(strip, "RWY", runway);
                return CommandResult.Success();
            }

            case "setSid":
            {
                if (value is null) return CommandResult.Fail("No value given.");
                if (!IsController()) return CommandResult.Fail(NotControllerError);
                var sid = CleanText(value, 10, upper: true);
                // Strip.SID's setter raises a vatSys error for a SID the runway lacks; answer the page instead
                var sids = fdr.DepartureRunway?.SIDs;
                if (sids is null || !sids.Any(s => s.sidStar?.Name == sid)) return CommandResult.Fail("No SID " + sid + " for the departure runway.");
                Refl.Set(strip, "SID", sid);
                return CommandResult.Success();
            }

            case "setGlop":
                if (value is null) return CommandResult.Fail("No value given.");
                // DropDown.ShowGlopDropDown: real controllers only
                if (!Network.Me.IsRealATC) return CommandResult.Fail(NotControllerError);
                FDP2.SetGlobalOps(fdr, CleanText(value, 10, upper: true));
                return CommandResult.Success();

            case "queueUp":
            {
                // Bay.QueueUp, for this strip rather than the desktop's picked one
                if (!FindItem(bays, strip, out var bay, out var item)) return CommandResult.Fail("That strip is not in a bay.");
                Refl.Call(bay, "AddDivider", true);
                Refl.Call(bay, "ChangeStripPositionAbs", item, Refl.Int(bay, "DivPosition", 0));
                if (picked) Refl.Call(bm, "RemovePicked", true);
                return CommandResult.Success();
            }
        }

        return CommandResult.Fail("Unknown action.");
    }

    private const string NotControllerError = "Not connected as a controller.";

    /// <summary>OzStrips' own gate on changing flight plans: a real controller,
    /// or its debug build (MainForm.IsDebug).</summary>
    private static bool IsController()
    {
        try
        {
            if (Network.Me.IsRealATC) return true;
        }
        catch { }

        return Refl.GetStatic(Oz.TypeOf("MainForm"), "IsDebug") is true;
    }

    private static CommandResult SetAndSync(object strip, string property, string? value, int maxLength)
    {
        if (value is null) return CommandResult.Fail("No value given.");
        Refl.Set(strip, property, CleanText(value, maxLength, upper: true));
        Refl.Call(strip, "SyncStrip");
        return CommandResult.Success();
    }

    private static string? AlertName(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "route" => "ROUTE",
            "rfl" => "RFL",
            "ssr" => "SSR",
            "ready" => "READY",
            "sid" => "VFR_SID",
            "hdg" => "NO_HDG",
            _ => null,
        };
    }

    /// <summary>The bay showing the given StripBay (name or number), as the page names it.</summary>
    private static object? FindBay(object bayManager, string? bayArg, out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(bayArg))
        {
            error = "No bay given.";
            return null;
        }

        var byNumber = int.TryParse(bayArg, NumberStyles.Integer, Inv, out var number);
        foreach (var bay in Snapshot(Refl.Get(Refl.Get(bayManager, "BayRepository"), "Bays")))
        {
            foreach (var type in Snapshot(Refl.Get(bay, "BayTypes")))
            {
                if (byNumber ? Convert.ToInt32(type, Inv) == number : string.Equals(type.ToString(), bayArg, StringComparison.OrdinalIgnoreCase))
                    return bay;
            }
        }

        error = "That bay is not in the current view.";
        return null;
    }

    /// <summary>The bay holding a strip and the strip's list item in it.</summary>
    private static bool FindItem(object bayRepository, object strip, out object bay, out object item)
    {
        bay = Refl.Call(bayRepository, "FindBay", strip)!;
        item = bay is null ? null! : Refl.Call(bay, "GetListItem", strip)!;
        return bay is not null && item is not null;
    }

    /// <summary>BayManager.PositionToNextBar for any strip (the original moves
    /// only the desktop's picked one): step past a bar that is adjacent,
    /// otherwise travel to just short of the next bar.</summary>
    private static void MoveToNextBar(object bay, object item, int direction)
    {
        if (Refl.Get(bay, "Strips") is not IList strips) return;

        var index = strips.IndexOf(item);
        if (index < 0) return;

        var next = index + direction;
        var neighbour = next >= 0 && next < strips.Count ? strips[next] : null;
        if (neighbour is null || !IsStripItem(neighbour))
        {
            Refl.Call(bay, "ChangeStripPosition", item, direction);
            return;
        }

        if (direction > 0)
        {
            for (var i = index + 2; i < strips.Count; i++)
            {
                if (strips[i] is { } present && !IsStripItem(present))
                {
                    Refl.Call(bay, "ChangeStripPositionAbs", item, i - 1);
                    return;
                }
            }
        }
        else
        {
            for (var i = index - 2; i >= 0; i--)
            {
                if (strips[i] is { } present && !IsStripItem(present))
                {
                    Refl.Call(bay, "ChangeStripPositionAbs", item, i + 1);
                    return;
                }
            }
        }
    }

    private static bool IsStripItem(object listItem) => Refl.Get(listItem, "Type")?.ToString() == "STRIP";

    private static string NormaliseRunwayPair(string? pair)
    {
        var clean = (pair ?? "").Replace("/", "").Replace(" ", "").Trim().ToUpperInvariant();
        return clean.Length <= 8 && clean.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9') ? clean : "";
    }

    /// <summary>Text bound for a strip or bar: no control characters (OzStrips
    /// marks bars with BEL in its server messages), trimmed, length-limited.</summary>
    private static string CleanText(string? text, int maxLength, bool upper)
    {
        var sb = new StringBuilder();
        foreach (var c in text ?? "")
        {
            if (!char.IsControl(c)) sb.Append(c);
        }

        var clean = sb.ToString().Trim();
        if (upper) clean = clean.ToUpperInvariant();
        return clean.Length > maxLength ? clean.Substring(0, maxLength) : clean;
    }

    private static object Required(object? value, string what)
    {
        return value ?? throw new MissingMemberException("OzStrips " + what + " was not found (unsupported OzStrips version).");
    }

    // ------------------------------------------------------------------
    // Finding OzStrips
    // ------------------------------------------------------------------

    private static Type? _controllerType;
    private static long _lastTypeScan = -TypeScanIntervalMs;

    /// <summary>OzStrips' MainFormController, once its assembly is loaded.
    /// Safe from any thread; a miss is retried every couple of seconds.</summary>
    private static Type? FindControllerType()
    {
        var known = Volatile.Read(ref _controllerType);
        if (known is not null) return known;

        var now = Clock.ElapsedMilliseconds;
        if (now - Interlocked.Read(ref _lastTypeScan) < TypeScanIntervalMs) return null;
        Interlocked.Exchange(ref _lastTypeScan, now);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = null;
            try { type = assembly.GetType(GuiNamespace + ".MainFormController", false); }
            catch { }
            if (type is null) continue;

            Volatile.Write(ref _controllerType, type);
            return type;
        }

        return null;
    }

    /// <summary>The live OzStrips objects everything else starts from.</summary>
    private sealed class Oz
    {
        private static readonly ConcurrentDictionary<string, Type?> Types = new();

        private Oz(object controller, object bayManager)
        {
            Controller = controller;
            BayManager = bayManager;
        }

        public object Controller { get; }
        public object BayManager { get; }
        public Form? MainForm { get; private set; }
        public object? SocketConn { get; private set; }
        public object? AerodromeManager { get; private set; }
        /// <summary>OzStripsSettings.Default (an internal settings class).</summary>
        public object? Settings { get; private set; }

        /// <summary>A type in OzStrips' GUI assembly, by its name below the GUI namespace.</summary>
        public static Type? TypeOf(string name)
        {
            var controllerType = FindControllerType();
            if (controllerType is null) return null;

            return Types.GetOrAdd(name, n =>
            {
                try { return controllerType.Assembly.GetType(GuiNamespace + "." + n, false); }
                catch { return null; }
            });
        }

        /// <summary>One of MainForm's public ToolStripMenuItem properties.</summary>
        public ToolStripMenuItem? MenuItem(string property) => Refl.Get(MainForm, property) as ToolStripMenuItem;

        /// <summary>GUI thread only. Null (with a reason for the page) unless
        /// the OzStrips window is open and initialised.</summary>
        public static Oz? Resolve(out string reason)
        {
            var controllerType = FindControllerType();
            if (controllerType is null)
            {
                reason = NotInstalledReason;
                return null;
            }

            // MainFormController.Instance exists from the window's construction to its closing
            var controller = Refl.GetStatic(controllerType, "Instance");
            if (controller is null ||
                Refl.Get(controller, "IsDisposed") is true ||
                Refl.Get(controller, "Visible") is false)
            {
                reason = OpenWindowReason;
                return null;
            }

            var bayManager = Refl.Get(controller, "_bayManager"); // private field
            if (bayManager is null)
            {
                reason = "OzStrips is still starting";
                return null;
            }

            // MainForm.MainFormInstance is public but outlives a closed window,
            // so it only counts when it belongs to this controller
            var mainForm = Refl.GetStatic(TypeOf("MainForm"), "MainFormInstance") as Form;
            if (mainForm is null || !ReferenceEquals(Refl.Get(mainForm, "Controller"), controller))
                mainForm = Refl.Get(controller, "_mainForm") as Form ?? mainForm; // private field

            reason = "";
            return new Oz(controller, bayManager)
            {
                MainForm = mainForm,
                SocketConn = Refl.Get(controller, "_socketConn"), // private field
                AerodromeManager = Refl.Get(mainForm, "AerodromeManager"),
                Settings = Refl.GetStatic(TypeOf("Properties.OzStripsSettings"), "Default"),
            };
        }
    }

    // ------------------------------------------------------------------
    // Reflection
    // ------------------------------------------------------------------

    /// <summary>Late-bound access to OzStrips members of any visibility, with
    /// the lookups cached. Reads are forgiving (a missing member or a getter
    /// that throws reads as null); writes and calls throw, so a command reports
    /// what it could not do.</summary>
    private static class Refl
    {
        private const BindingFlags Declared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private sealed class Signature
        {
            public Signature(MethodInfo method)
            {
                Method = method;
                Parameters = method.GetParameters();
            }

            public MethodInfo Method { get; }
            public ParameterInfo[] Parameters { get; }
        }

        private static readonly ConcurrentDictionary<(Type, string), MemberInfo?> Members = new();
        private static readonly ConcurrentDictionary<(Type, string), Signature[]> Methods = new();

        public static object? Get(object? target, string name)
        {
            return target is null ? null : Read(Member(target.GetType(), name), target);
        }

        public static object? GetStatic(Type? type, string name)
        {
            return type is null ? null : Read(Member(type, name), null);
        }

        public static string Str(object? target, string name) => Get(target, name) as string ?? "";

        public static bool Bool(object? target, string name) => Get(target, name) is true;

        public static int Int(object? target, string name, int fallback) => Get(target, name) is int value ? value : fallback;

        public static void Set(object target, string name, object? value)
        {
            switch (Member(target.GetType(), name))
            {
                case PropertyInfo property when property.CanWrite:
                    property.SetValue(target, value, null);
                    break;
                case FieldInfo field:
                    field.SetValue(target, value);
                    break;
                default:
                    throw Missing(target.GetType(), name);
            }
        }

        public static object? Call(object target, string name, params object?[] args)
        {
            return Invoke(target.GetType(), target, name, args);
        }

        public static object? CallStatic(Type type, string name, params object?[] args)
        {
            return Invoke(type, null, name, args);
        }

        private static object? Read(MemberInfo? member, object? target)
        {
            try
            {
                return member switch
                {
                    PropertyInfo property when property.CanRead => property.GetValue(target, null),
                    FieldInfo field => field.GetValue(target),
                    _ => null,
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The property or field of that name, searching base classes
        /// too (their private members are not inherited through reflection).</summary>
        private static MemberInfo? Member(Type type, string name)
        {
            return Members.GetOrAdd((type, name), key =>
            {
                for (var t = key.Item1; t is not null; t = t.BaseType)
                {
                    try
                    {
                        var property = t.GetProperty(key.Item2, Declared);
                        if (property is not null && property.GetIndexParameters().Length == 0) return property;
                        var field = t.GetField(key.Item2, Declared);
                        if (field is not null) return field;
                    }
                    catch (AmbiguousMatchException) { }
                }
                return null;
            });
        }

        /// <summary>Calls the overload the arguments fit, filling in any
        /// trailing optional parameters with their defaults.</summary>
        private static object? Invoke(Type type, object? target, string name, object?[] args)
        {
            var candidates = Methods.GetOrAdd((type, name), key =>
            {
                var found = new List<Signature>();
                for (var t = key.Item1; t is not null; t = t.BaseType)
                {
                    foreach (var method in t.GetMethods(Declared))
                    {
                        if (method.Name == key.Item2 && !method.IsGenericMethodDefinition) found.Add(new Signature(method));
                    }
                }
                return found.ToArray();
            });

            foreach (var candidate in candidates)
            {
                if (candidate.Method.IsStatic != (target is null)) continue;

                var parameters = candidate.Parameters;
                if (parameters.Length < args.Length) continue;

                var full = new object?[parameters.Length];
                var fits = true;
                for (var i = 0; i < parameters.Length && fits; i++)
                {
                    if (i < args.Length)
                    {
                        fits = Fits(parameters[i].ParameterType, args[i]);
                        full[i] = args[i];
                    }
                    else
                    {
                        fits = parameters[i].HasDefaultValue;
                        if (fits) full[i] = parameters[i].DefaultValue;
                    }
                }

                if (fits) return candidate.Method.Invoke(target, full);
            }

            throw Missing(type, name + "()");
        }

        private static bool Fits(Type parameter, object? argument)
        {
            if (parameter.IsByRef) return false;
            var underlying = Nullable.GetUnderlyingType(parameter);
            if (argument is null) return !parameter.IsValueType || underlying is not null;
            return (underlying ?? parameter).IsInstanceOfType(argument);
        }

        private static Exception Missing(Type type, string member)
        {
            return new MissingMemberException("OzStrips " + type.Name + "." + member + " was not found (unsupported OzStrips version).");
        }
    }

    private static readonly ConcurrentDictionary<(Type, string), object> EnumValues = new();

    /// <summary>The named value of an OzStrips enum (throws if it has none).</summary>
    private static object EnumValue(Type enumType, string name)
    {
        return EnumValues.GetOrAdd((enumType, name), key => Enum.Parse(key.Item1, key.Item2, true));
    }

    /// <summary>Whether a [Flags] enum value has the flag of that name set.</summary>
    private static bool HasFlag(object? value, string flag)
    {
        try
        {
            if (value is null || !value.GetType().IsEnum) return false;
            var bit = Convert.ToInt64(EnumValue(value.GetType(), flag), Inv);
            return (Convert.ToInt64(value, Inv) & bit) == bit;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A copy of a collection's non-null items, so nothing OzStrips
    /// does while it is read (a getter can remove a strip) disturbs the loop.</summary>
    private static object[] Snapshot(object? collection)
    {
        if (collection is not IEnumerable items || collection is string) return Array.Empty<object>();

        var list = new List<object>();
        try
        {
            foreach (var item in items)
            {
                if (item is not null) list.Add(item);
            }
        }
        catch (InvalidOperationException) { }
        return list.ToArray();
    }

    private static List<string> Strings(object? collection)
    {
        return Snapshot(collection).Select(o => o.ToString() ?? "").Where(s => s.Length > 0).ToList();
    }

    private static string Describe(Exception ex)
    {
        while (ex is TargetInvocationException or AggregateException && ex.InnerException is not null) ex = ex.InnerException;
        var message = (ex.Message ?? "").Trim();
        return message.Length > 200 ? message.Substring(0, 200) : message.Length > 0 ? message : ex.GetType().Name;
    }

    // ------------------------------------------------------------------
    // What the page receives (serialised by JavaScriptSerializer: names as declared)
    // ------------------------------------------------------------------

    private sealed class CommandResult
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        /// <summary>Accepted but not finished when the reply was sent.</summary>
        public bool Queued { get; set; }

        public static CommandResult Success() => new() { Ok = true };
        public static CommandResult Pending() => new() { Ok = true, Queued = true };
        public static CommandResult Fail(string error) => new() { Ok = false, Error = error };
    }

    private sealed class StateDto
    {
        /// <summary>Clock ms when built; not serialised (internal).</summary>
        internal long BuiltAt;

        public bool Available { get; set; }
        public string Reason { get; set; } = "";
        /// <summary>Not running, and the "start" command can start it.</summary>
        public bool CanStart { get; set; }

        public string Aerodrome { get; set; } = "";
        public string Atis { get; set; } = "";
        public string Metar { get; set; } = "";
        public string UtcTime { get; set; } = "";
        /// <summary>DISCONNECTED, CONNECTED or RECONNECTING (SocketConn.ConnectionState).</summary>
        public string ConnectionState { get; set; } = "DISCONNECTED";
        public bool Connected { get; set; }
        public int ConnectionsCount { get; set; }
        public List<string> Connections { get; set; } = new();
        public int PendingPDCs { get; set; }
        public bool AutoFillAvailable { get; set; }
        public double StripScale { get; set; } = 1;
        public Dictionary<string, string> StripColours { get; set; } = new();
        public List<string> Aerodromes { get; set; } = new();
        public List<string> RunwayPairs { get; set; } = new();
        public int SmartResizeMode { get; set; }
        /// <summary>OzStrips' setting: a strip stays picked after it is acted on.</summary>
        public bool KeepStripPicked { get; set; }
        public bool CircuitActive { get; set; }
        public bool CoordinatorActive { get; set; }
        public bool CdmEnabled { get; set; }
        /// <summary>Filled while CDM is enabled; shown in the header of a bay with CdmDisplay.</summary>
        public List<CdmRateDto> CdmRates { get; set; } = new();
        public bool PdcSoundEnabled { get; set; }
        public bool GroundMapsInhibited { get; set; }
        public bool CircuitToggleAvailable { get; set; }
        public List<string> Layouts { get; set; } = new();
        public string CurrentLayout { get; set; } = "";
        public List<BayDto> Bays { get; set; } = new();

        public static StateDto Unavailable(string reason)
        {
            return new StateDto { Available = false, Reason = reason, BuiltAt = Clock.ElapsedMilliseconds };
        }
    }

    private sealed class BayDto
    {
        /// <summary>The bay's first StripBay, by name; what commands take as their Bay.</summary>
        public string Bay { get; set; } = "";
        public int BayId { get; set; }
        public string Name { get; set; } = "";
        public int Column { get; set; }
        /// <summary>The bay shows the CDM rates where the others have their queue buttons.</summary>
        public bool CdmDisplay { get; set; }
        /// <summary>Top to bottom, as displayed.</summary>
        public List<BayItemDto> Items { get; set; } = new();
    }

    private sealed class CdmRateDto
    {
        public int Minutes { get; set; }
        public int Count { get; set; }
        public int Target { get; set; }
        public string Colour { get; set; } = "";
    }

    private sealed class BayItemDto
    {
        /// <summary>STRIP, BAR or QUEUEBAR.</summary>
        public string ItemType { get; set; } = "STRIP";
        public string Text { get; set; } = "";
        public int Style { get; set; }
        public StripDto? Strip { get; set; }
    }

    private sealed class StripDto
    {
        public string Callsign { get; set; } = "";
        public string CLX { get; set; } = "";
        public string Gate { get; set; } = "";
        public string Remark { get; set; } = "";
        public string DepartureFreq { get; set; } = "";
        public string Runway { get; set; } = "";
        public int CockLevel { get; set; }
        public string Bay { get; set; } = "";
        public string StripType { get; set; } = "";
        public string Time { get; set; } = "";
        public string AircraftType { get; set; } = "";
        public string WTC { get; set; } = "";
        public string SSR { get; set; } = "";
        public string ADES { get; set; } = "";
        public string SID { get; set; } = "";
        public string FirstWpt { get; set; } = "";
        public string RFL { get; set; } = "";
        public string CFL { get; set; } = "";
        public string GLOP { get; set; } = "";
        public string FlightRules { get; set; } = "";
        public bool Ready { get; set; }
        public string RouteIndicator { get; set; } = "";
        public string AllocatedBay { get; set; } = "";
        public bool Crossing { get; set; }
        public string HDG { get; set; } = "";
        public string Tot { get; set; } = "00:00";
        public string PDCIndicator { get; set; } = "";
        public bool PdcRequested { get; set; }
        public bool PdcNeedsAck { get; set; }
        public bool SidTransition { get; set; }
        public bool RouteAlert { get; set; }
        public bool RflAlert { get; set; }
        public bool SsrAlert { get; set; }
        public bool ReadyAlert { get; set; }
        public bool NoHdgAlert { get; set; }
        public bool VfrSidAlert { get; set; }
        public bool CdmActive { get; set; }
        public bool CdmReadyToPush { get; set; }
        public bool CdmHasSlot { get; set; }
        public bool StandAutofilled { get; set; }
        public bool Picked { get; set; }
        public bool LastTransmit { get; set; }
        public bool WorldFlight { get; set; }
        /// <summary>The server lists the aircraft as planned to an invalid destination ("NO SLOT").</summary>
        public bool NoSlot { get; set; }
    }
}
