using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using vatsys;

namespace VatSysExternalWindows;

/// <summary>
/// Data the windows need that is not a plain read of vatSys's public state: the
/// profile's track symbol geometry, and which crossing/release maps the vatSys
/// Ground window currently shows.
/// </summary>
public sealed class WindowDataService
{
    private readonly object _lock = new();
    private string _profileDir = "";
    private string _profileName = "";
    private List<TrackSymbolData.SymbolDef>? _symbols;

    /// <summary>Returns the profile's Tracks.xml symbol definitions (with DefaultTracks.xml backfill).</summary>
    public List<TrackSymbolData.SymbolDef> GetTrackSymbols()
    {
        lock (_lock)
        {
            EnsureProfileDir();
            return _symbols ??= TrackSymbolData.Load(_profileDir);
        }
    }

    private volatile List<string> _visibleMapsSnapshot = new();

    /// <summary>Returns the currently visible crossing/released map layer keys from vatSys Ground windows.</summary>
    public List<string> GetVisibleCrossingReleasedMaps()
    {
        // Application.OpenForms and the ASD control state belong to the GUI thread:
        // compute there and wait briefly, falling back to the last result if the
        // GUI is busy.
        GuiThread.Run(() => _visibleMapsSnapshot = ComputeVisibleCrossingReleasedMaps());
        return _visibleMapsSnapshot;
    }

    private static List<string> ComputeVisibleCrossingReleasedMaps()
    {
        // Prefer the live state of an open vatSys Ground window. When none is open,
        // derive the state from OzStrips' runway bay directly — OzStrips can only
        // push its crossing/release toggles into Ground windows that exist.
        return ComputeFromGroundWindow() ?? ComputeFromOzStrips() ?? new List<string>();
    }

    private static List<string>? ComputeFromGroundWindow()
    {
        try
        {
            // Find ASMGCS Ground windows via Application.OpenForms
            foreach (Form form in Application.OpenForms)
            {
                if (form.GetType().Name != "ASMGCSWindow") continue;

                // Get the ASDControlDX field via reflection (it's a private field)
                var field = form.GetType().GetField("asmgcsControlDX1",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (field is null) continue;

                var asdControl = field.GetValue(form);
                if (asdControl is null) continue;

                // Call public IsMapVisible for each crossing/released map
                var isVisibleMethod = asdControl.GetType().GetMethod("IsMapVisible");
                if (isVisibleMethod is null) continue;

                var result = new List<string>();
                foreach (var map in DisplayMaps.Maps)
                {
                    if (map.Category != DisplayMaps.MapCategories.Ground) continue;
                    if (string.IsNullOrEmpty(map.CustomColourName)) continue;
                    if (!map.CustomColourName.Equals("Release", StringComparison.OrdinalIgnoreCase) &&
                        !map.CustomColourName.Equals("Crossing", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var visible = (bool)(isVisibleMethod.Invoke(asdControl, new object[] { map }) ?? false);
                    if (visible)
                    {
                        var key = string.IsNullOrEmpty(map.Folder) ? map.Name : map.Folder + "/" + map.Name;
                        result.Add(key);
                    }
                }

                return result; // Only need one Ground window
            }
        }
        catch { }
        return null;
    }

    /// <summary>Replicates OzStrips' MapManager.SetApplicableMaps decision by reading its
    /// runway-bay bar strips via reflection (MainFormController.Instance is public static).
    /// Returns null when OzStrips isn't running or its state can't be read.</summary>
    private static List<string>? ComputeFromOzStrips()
    {
        try
        {
            Type? ctrlType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { ctrlType = asm.GetType("MaxRumsey.OzStripsPlugin.GUI.MainFormController"); }
                catch { }
                if (ctrlType is not null) break;
            }
            if (ctrlType is null) return null;

            var controller = ctrlType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (controller is null) return null;

            // OzStrips skips map updates entirely while "Inhibit ground maps" is checked
            var inhibit = GetMember(controller, "InhibitGroundMaps");
            if (inhibit is not null &&
                GetMember(inhibit, "Checked") is bool inhibited && inhibited)
                return null;

            var mainForm = GetMember(controller, "_mainForm");
            var bayManager = GetMember(controller, "_bayManager");
            if (mainForm is null || bayManager is null) return null;

            var icao = GetMember(bayManager, "AerodromeName") as string ?? "";
            if (string.IsNullOrEmpty(icao)) return null;

            // Aerodrome full name = the Maps folder (AutoMapAerodromes ICAO→FullName)
            string? fullName = null;
            var settings = GetMember(GetMember(mainForm, "AerodromeManager"), "Settings");
            if (GetMember(settings, "AutoMapAerodromes") is System.Collections.IEnumerable autoMaps)
            {
                foreach (var am in autoMaps)
                {
                    if (string.Equals(GetMember(am, "ICAOCode") as string, icao, StringComparison.OrdinalIgnoreCase))
                    {
                        fullName = GetMember(am, "FullName") as string;
                        break;
                    }
                }
            }
            if (string.IsNullOrEmpty(fullName)) return null;

            // Bar texts from the first runway bay (OzStrips uses BAY_RUNWAY)
            var barNames = new List<string>();
            var foundBay = false;
            if (GetMember(GetMember(bayManager, "BayRepository"), "Bays") is System.Collections.IEnumerable bays)
            {
                foreach (var bay in bays)
                {
                    var isRunway = false;
                    if (GetMember(bay, "BayTypes") is System.Collections.IEnumerable types)
                    {
                        foreach (var t in types)
                        {
                            if (t?.ToString() == "BAY_RUNWAY") { isRunway = true; break; }
                        }
                    }
                    if (!isRunway) continue;

                    foundBay = true;
                    if (GetMember(bay, "Strips") is System.Collections.IEnumerable strips)
                    {
                        foreach (var s in strips)
                        {
                            if (GetMember(s, "Type")?.ToString() == "BAR")
                                barNames.Add(GetMember(s, "BarText") as string ?? "");
                        }
                    }
                    break;
                }
            }
            if (!foundBay) return null;

            // Same decision as OzStrips MapManager: map "Crossing_16L34R" is visible
            // when a bar "XXX CROSSING RWY 16L/34R…" exists; "Released_16L34R" when
            // a bar "Runway 16L/34R Released…" exists.
            var result = new List<string>();
            foreach (var map in DisplayMaps.Maps)
            {
                if (map.Category != DisplayMaps.MapCategories.Ground) continue;
                if (!string.Equals(map.Folder, fullName, StringComparison.OrdinalIgnoreCase)) continue;

                var crossing = map.Name.StartsWith("Crossing", StringComparison.OrdinalIgnoreCase);
                var released = map.Name.StartsWith("Released", StringComparison.OrdinalIgnoreCase);
                if (!crossing && !released) continue;

                var parts = map.Name.Split('_');
                if (parts.Length < 2 || parts[1].Length % 2 != 0) continue;
                var pair = parts[1];
                var r0 = pair.Substring(0, pair.Length / 2);
                var r1 = pair.Substring(pair.Length / 2);

                var wanted = crossing ? "XXX CROSSING RWY " + r0 + "/" + r1
                                      : "Runway " + r0 + "/" + r1 + " Released";
                var on = barNames.Any(b => b.StartsWith(wanted, StringComparison.Ordinal));
                if (on)
                {
                    var key = string.IsNullOrEmpty(map.Folder) ? map.Name : map.Folder + "/" + map.Name;
                    result.Add(key);
                }
            }
            return result;
        }
        catch { return null; }
    }

    private static object? GetMember(object? obj, string name)
    {
        if (obj is null) return null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var t = obj.GetType();
        var p = t.GetProperty(name, flags);
        if (p is not null) return p.GetValue(obj);
        return t.GetField(name, flags)?.GetValue(obj);
    }

    private void EnsureProfileDir()
    {
        string currentName;
        try { currentName = Profile.Name ?? ""; } catch { currentName = ""; }

        if (!string.IsNullOrEmpty(_profileDir) && currentName == _profileName)
            return;

        // First resolution or profile changed: clear the cache and re-resolve
        _symbols = null;
        _profileDir = ProfileLocator.Resolve();
        _profileName = currentName;
    }
}
