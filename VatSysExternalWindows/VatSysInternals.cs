using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using vatsys;
using vatsys.Plugin;

namespace VatSysExternalWindows;

/// <summary>An alert on a track (vatsys.Alert is internal, so its values are copied).</summary>
internal sealed class AlertInfo
{
    public AlertTypes Type;
    public AlertPriorities Priority;
    public bool Inhibited;
    public bool Cancelled;
}

/// <summary>vatSys's parsed Labels.xml (vatsys.Labels is internal), copied into
/// plain objects so the label builder can walk it like vatSys's PaintLabel does.</summary>
internal sealed class LabelDef
{
    public string Type = "";
    /// <summary>LabelBoxMode == Default: an alert box may be drawn around the label.</summary>
    public bool BoxDefault;
    /// <summary>LabelAnchorPosition as vatSys uses it numerically: Top = 0, Bottom = 1.</summary>
    public int AnchorOffset = 1;
    public int MaxWidth = 11;
    public bool Justified;
    public List<LabelLineDef> Lines = new();
}

internal sealed class LabelLineDef
{
    public string Id = "";
    /// <summary>"Default", "Toggle" or "Hidden".</summary>
    public string Visibility = "Default";
    public string SharedId = "";
    public bool Justified;
    public List<LabelItemDef> Items = new();
}

internal sealed class LabelItemDef
{
    /// <summary>Labels.LabelItemTypes name, e.g. "LABEL_ITEM_ACID"; "UNKNOWN" for plugin items.</summary>
    public string Type = "";
    public string UnknownType = "";
    public Colours.Identities Colour, BackColour, BorderColour;
    /// <summary>Alert-priority overrides, index 0..3 = P1..P4.</summary>
    public Colours.Identities[] PriorityColour = new Colours.Identities[4];
    public Colours.Identities[] PriorityBackColour = new Colours.Identities[4];
    public Colours.Identities[] PriorityBorderColour = new Colours.Identities[4];
    public int LeftPadding;
    public BorderFlags Border;
    public bool LeaderLineAnchor;
}

/// <summary>
/// Reflection access to the parts of vatSys the label and track painting needs
/// but does not expose publicly: the parsed label definitions, track alerts,
/// and the plugin hooks for custom label items and track colours. Everything
/// here fails soft — a vatSys update that renames something degrades the
/// display rather than breaking the server.
/// </summary>
internal static class VatSysInternals
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly Assembly VatSys = typeof(MMI).Assembly;

    // ---- Labels ----

    private static readonly object LabelLock = new();
    private static List<LabelDef> _labels = new();
    private static DateTime _labelsLoadedUtc = DateTime.MinValue;

    /// <summary>The label definition for a Labels.LabelTypes name, or null if the
    /// profile doesn't define it.</summary>
    public static LabelDef? FindLabel(string labelType)
    {
        List<LabelDef> labels;
        lock (LabelLock)
        {
            // Labels are reloaded with the profile; a periodic re-read keeps up
            // without needing a change signal.
            if ((DateTime.UtcNow - _labelsLoadedUtc).TotalSeconds > 30)
            {
                var loaded = LoadLabels();
                if (loaded.Count > 0 || _labels.Count == 0) _labels = loaded;
                _labelsLoadedUtc = DateTime.UtcNow;
            }
            labels = _labels;
        }

        foreach (var label in labels)
        {
            if (label.Type == labelType) return label;
        }
        return null;
    }

    private static List<LabelDef> LoadLabels()
    {
        var result = new List<LabelDef>();
        try
        {
            var labelsType = VatSys.GetType("vatsys.Labels");
            if (labelsType?.GetField("LabelContainers", Any)?.GetValue(null) is not IEnumerable containers) return result;

            foreach (var container in ToList(containers))
            {
                if (container is null) continue;
                var def = new LabelDef
                {
                    Type = Get(container, "type")?.ToString() ?? "",
                    BoxDefault = Get(container, "LabelBoxMode")?.ToString() == "Default",
                    AnchorOffset = Get(container, "LabelAnchorPosition")?.ToString() == "Top" ? 0 : 1,
                    MaxWidth = Get(container, "MaxWidth") is int width ? width : 11,
                    Justified = Get(container, "Justified") is true,
                };

                if (Get(container, "LabelLines") is IEnumerable lines)
                {
                    foreach (var line in lines)
                    {
                        if (line is null) continue;
                        var lineDef = new LabelLineDef
                        {
                            Id = Get(line, "id") as string ?? "",
                            Visibility = Get(line, "Visibility")?.ToString() ?? "Default",
                            SharedId = Get(line, "SharedId") as string ?? "",
                            Justified = Get(line, "Justified") is true,
                        };

                        if (Get(line, "Items") is IEnumerable items)
                        {
                            foreach (var item in items)
                            {
                                if (item is null) continue;
                                var itemDef = new LabelItemDef
                                {
                                    Type = Get(item, "type")?.ToString() ?? "",
                                    UnknownType = Get(item, "unknownType") as string ?? "",
                                    Colour = Identity(item, "Colour"),
                                    BackColour = Identity(item, "BackColour"),
                                    BorderColour = Identity(item, "BorderColour"),
                                    LeftPadding = Get(item, "LeftPadding") is int padding ? padding : 0,
                                    Border = Get(item, "Border") is BorderFlags border ? border : BorderFlags.None,
                                    LeaderLineAnchor = Get(item, "LeaderLineAnchor") is true,
                                };
                                for (var p = 0; p < 4; p++)
                                {
                                    itemDef.PriorityColour[p] = Identity(item, "P" + (p + 1) + "Colour");
                                    itemDef.PriorityBackColour[p] = Identity(item, "P" + (p + 1) + "BackColour");
                                    itemDef.PriorityBorderColour[p] = Identity(item, "P" + (p + 1) + "BorderColour");
                                }
                                lineDef.Items.Add(itemDef);
                            }
                        }

                        def.Lines.Add(lineDef);
                    }
                }

                result.Add(def);
            }
        }
        catch { }
        return result;
    }

    private static ICollection ToList(IEnumerable source)
    {
        var list = new ArrayList();
        foreach (var item in source) list.Add(item);
        return list;
    }

    private static Colours.Identities Identity(object item, string field)
    {
        return Get(item, field) is Colours.Identities id ? id : Colours.Identities.Default;
    }

    private static object? Get(object target, string name)
    {
        var type = target.GetType();
        var field = type.GetField(name, Any);
        if (field is not null) return field.GetValue(target);
        return type.GetProperty(name, Any)?.GetValue(target);
    }

    // ---- Alerts ----

    private static bool _alertsResolved;
    private static FieldInfo? _alertsInstance;
    private static MethodInfo? _getAlerts;

    /// <summary>Alerts.Instance.GetAlerts(track), copied. Empty when there are none
    /// or the lookup is unavailable.</summary>
    public static List<AlertInfo> GetAlerts(Track track)
    {
        var result = new List<AlertInfo>();
        try
        {
            if (!_alertsResolved)
            {
                var alertsType = VatSys.GetType("vatsys.Alerts");
                _alertsInstance = alertsType?.GetField("Instance", Any);
                _getAlerts = alertsType?.GetMethod("GetAlerts", Any, null, new[] { typeof(Track) }, null);
                _alertsResolved = true;
            }

            var instance = _alertsInstance?.GetValue(null);
            if (instance is null || _getAlerts is null) return result;
            if (_getAlerts.Invoke(instance, new object[] { track }) is not IEnumerable alerts) return result;

            foreach (var alert in alerts)
            {
                if (alert is null) continue;
                result.Add(new AlertInfo
                {
                    Type = Get(alert, "Type") is AlertTypes type ? type : 0,
                    Priority = Get(alert, "Priority") is AlertPriorities priority ? priority : AlertPriorities.P4,
                    Inhibited = Get(alert, "Inhibited") is true,
                    Cancelled = Get(alert, "Cancelled") is true,
                });
            }
        }
        catch { }
        return result;
    }

    // ---- Plugin hooks ----

    private static bool _pluginsResolved;
    private static MethodInfo? _getCustomLabelItem;
    private static MethodInfo? _getCustomTrackColour;

    private static void ResolvePlugins()
    {
        if (_pluginsResolved) return;
        var pluginsType = VatSys.GetType("vatsys.Plugin.Plugins");
        _getCustomLabelItem = pluginsType?.GetMethod("GetCustomLabelItem", Any);
        _getCustomTrackColour = pluginsType?.GetMethod("GetCustomTrackColour", Any);
        _pluginsResolved = true;
    }

    /// <summary>Plugins.GetCustomLabelItem — label items supplied by other plugins
    /// (strip bays, events, ...).</summary>
    public static CustomLabelItem? GetCustomLabelItem(string itemType, Track track, FDP2.FDR? fdr, RDP.RadarTrack? radarTrack)
    {
        try
        {
            ResolvePlugins();
            return _getCustomLabelItem?.Invoke(null, new object?[] { itemType, track, fdr, radarTrack }) as CustomLabelItem;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Plugins.GetCustomTrackColour — a plugin's override of the track colour.</summary>
    public static CustomColour? GetCustomTrackColour(bool asd, Track track)
    {
        try
        {
            ResolvePlugins();
            return _getCustomTrackColour?.Invoke(null, new object[] { asd, track }) as CustomColour;
        }
        catch
        {
            return null;
        }
    }

    // ---- SSR code filters ----

    /// <summary>SSRCodeFilter.IsMatch (not public). A null filter matches nothing.</summary>
    public static bool SsrFilterMatches(object? filter, int code)
    {
        if (filter is null) return false;
        try
        {
            var method = filter.GetType().GetMethod("IsMatch", Any, null, new[] { typeof(int) }, null);
            return method?.Invoke(filter, new object[] { code }) is true;
        }
        catch
        {
            return false;
        }
    }

    // ---- Pilot engine type ----

    /// <summary>NetworkPilot.EngineType as its enum name ("None", "Piston", "Jet", ...).
    /// The enum lives in an assembly the plugin doesn't reference.</summary>
    public static string EngineTypeName(NetworkPilot pilot)
    {
        try
        {
            return Get(pilot, "EngineType")?.ToString() ?? "None";
        }
        catch
        {
            return "None";
        }
    }
}
