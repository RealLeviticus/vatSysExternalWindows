using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using vatsys;

namespace VatSysExternalWindows;

/// <summary>
/// Provides ground positions, map geometry, and live aircraft for the web UI.
/// Reads from whichever profile is currently loaded.
/// </summary>
public sealed class GroundDataService
{
    private readonly object _lock = new();
    private string _profileDir = "";
    private string _profileName = "";
    private List<GroundMapData.GroundPosition>? _positions;
    private readonly Dictionary<string, GroundMapData.AirportMap> _mapCache = new(StringComparer.OrdinalIgnoreCase);
    private GroundMapData.ColourSet? _colours;
    private List<GroundMapData.GroundMapMeta>? _groundMaps;

    // Position history: use vatSys RadarTrack.PositionHistory directly
    // Dot count controlled by MMI.HistoryDots user setting (default 5, max 9)

    /// <summary>Returns all ASMGCS positions from the profile.</summary>
    public List<GroundMapData.GroundPosition> GetPositions()
    {
        EnsureProfileDir();
        _positions ??= GroundMapData.LoadPositions(_profileDir);
        return _positions;
    }

    /// <summary>Returns the airport ICAO codes available.</summary>
    public List<string> GetAirportCodes()
    {
        return GetPositions().Select(p => p.Airport).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Gets the map for an airport, loading and caching on first access.</summary>
    public GroundMapData.AirportMap? GetMap(string icao)
    {
        EnsureProfileDir();

        lock (_lock)
        {
            if (_mapCache.TryGetValue(icao, out var cached))
                return cached;

            var pos = GetPositions().FirstOrDefault(p =>
                string.Equals(p.Airport, icao, StringComparison.OrdinalIgnoreCase));

            if (pos is null) return null;

            var map = GroundMapData.LoadAirportMap(_profileDir, pos);
            if (map is not null)
                _mapCache[icao] = map;

            return map;
        }
    }

    /// <summary>Returns the ground colour scheme.</summary>
    public GroundMapData.ColourSet GetColours()
    {
        EnsureProfileDir();
        _colours ??= GroundMapData.LoadColours(_profileDir);
        return _colours;
    }

    /// <summary>Returns metadata for ALL Ground-category maps across the profile (for dropdown).</summary>
    public List<GroundMapData.GroundMapMeta> GetGroundMaps()
    {
        EnsureProfileDir();
        _groundMaps ??= GroundMapData.ScanAllGroundMaps(_profileDir);
        return _groundMaps;
    }

    /// <summary>Loads geometry for specific map layers identified by "Folder/Name" keys.</summary>
    public List<GroundMapData.MapLayer> GetMapLayers(List<string> mapKeys)
    {
        EnsureProfileDir();
        return GroundMapData.LoadSpecificLayers(_profileDir, mapKeys);
    }

    /// <summary>Returns aircraft near the given airport.</summary>
    public List<AircraftDto> GetAircraft(string icao)
    {
        var result = new List<AircraftDto>();
        var map = GetMap(icao);
        if (map is null) return result;

        IList<RDP.RadarTrack>? tracks = null;
        try { tracks = RDP.RadarTracks; } catch { }
        if (tracks is null) return result;

        foreach (var track in tracks)
        {
            try
            {
                if (track.Cancelled) continue;
                var coord = track.LatLong;
                if (coord is null) continue;

                var lat = coord.Latitude;
                var lon = coord.Longitude;

                if (Math.Abs(lat - map.CenterLat) > 0.15 || Math.Abs(lon - map.CenterLon) > 0.15)
                    continue;

                var callsign = "";
                var heading = 0.0;
                var groundSpeed = 0.0;
                var onGround = false;

                try { onGround = track.OnGround; } catch { }
                // vatSys uses networkPilot.Heading (aircraft nose direction) for ground symbols,
                // NOT track.Heading (calculated track/movement direction). These differ during pushback.
                try
                {
                    var pilot = track.ActualAircraft;
                    if (pilot is not null)
                        heading = pilot.Heading;
                    else
                        heading = track.Heading;
                }
                catch { try { heading = track.Heading; } catch { } }
                try { groundSpeed = track.GroundSpeed; } catch { }

                try
                {
                    var fdr = track.CoupledFDR;
                    if (fdr is not null) callsign = fdr.Callsign ?? "";
                }
                catch { }

                if (string.IsNullOrEmpty(callsign))
                {
                    try
                    {
                        var pilot = track.ActualAircraft;
                        if (pilot is not null) callsign = pilot.Callsign ?? "";
                    }
                    catch { }
                }

                var category = "unknown";
                var wakeCat = "";
                var aircraftType = "";
                var runway = "";
                try
                {
                    // vatSys: ASMGCSCoupledFDR takes precedence over CoupledFDR for ground display
                    var fdr = track.ASMGCSCoupledFDR ?? track.CoupledFDR;
                    if (fdr is not null)
                    {
                        var dep = fdr.DepAirport ?? "";
                        var arr = fdr.DesAirport ?? "";
                        if (dep.Equals(icao, StringComparison.OrdinalIgnoreCase))
                            category = "departure";
                        else if (arr.Equals(icao, StringComparison.OrdinalIgnoreCase))
                            category = "arrival";
                        else
                            category = "local";

                        try { wakeCat = fdr.AircraftWake ?? ""; } catch { }
                        try { aircraftType = fdr.AircraftType ?? ""; } catch { }
                        try { runway = fdr.DepartureRunway?.Name ?? ""; } catch { }
                    }
                }
                catch { }

                // Get position history trail dots from vatSys RadarTrack.PositionHistory
                double[][]? history = null;
                var maxDots = MMI.HistoryDots; // user setting (0=off, 1-9)
                try
                {
                    if (maxDots > 0)
                    {
                        var posHist = track.PositionHistory?.ToArray();
                        if (posHist is not null && posHist.Length > 0)
                        {
                            var dots = new List<double[]>();
                            // Newest first (iterate backwards), limit to user setting
                            for (int i = posHist.Length - 1; i >= 0 && dots.Count < maxDots; i--)
                            {
                                var h = posHist[i];
                                if (h?.LatLong is null) continue;
                                dots.Add(new[] { Math.Round(h.LatLong.Latitude, 7), Math.Round(h.LatLong.Longitude, 7) });
                            }
                            if (dots.Count > 0) history = dots.ToArray();
                        }
                    }
                }
                catch { }

                // Get altitude for GroundAir label
                var altitude = -1;
                try { altitude = track.CorrectedAltitude; } catch { }

                result.Add(new AircraftDto
                {
                    Callsign = callsign,
                    Lat = Math.Round(lat, 7),
                    Lon = Math.Round(lon, 7),
                    Hdg = Math.Round(heading, 1),
                    Gs = Math.Round(groundSpeed, 0),
                    Alt = altitude,
                    OnGround = onGround,
                    Category = category,
                    WakeCat = wakeCat,
                    AircraftType = aircraftType,
                    Runway = runway,
                    History = history,
                });
            }
            catch { }
        }

        return result;
    }

    /// <summary>Returns the currently visible crossing/released map layer keys from vatSys Ground windows.</summary>
    public List<string> GetVisibleCrossingReleasedMaps()
    {
        var result = new List<string>();
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

                break; // Only need one Ground window
            }
        }
        catch { }
        return result;
    }

    private void EnsureProfileDir()
    {
        // Detect profile change
        try
        {
            var currentName = Profile.Name ?? "";
            if (!string.IsNullOrEmpty(_profileDir) && currentName == _profileName)
                return;
            if (!string.IsNullOrEmpty(_profileDir) && currentName != _profileName)
            {
                // Profile changed — clear everything
                _profileDir = "";
            }
        }
        catch
        {
            if (!string.IsNullOrEmpty(_profileDir)) return;
        }

        // Clear stale caches when re-resolving
        _positions = null;
        _mapCache.Clear();
        _colours = null;
        _groundMaps = null;

        try
        {
            var name = Profile.Name;
            if (!string.IsNullOrEmpty(name))
                {
                    // Check standard vatSys profile locations
                    var docDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    var candidates = new[]
                    {
                        Path.Combine(docDir, "vatSys Files", "Profiles", name),
                        Path.Combine(docDir, "vatSys", "Profiles", name),
                    };

                    foreach (var dir in candidates)
                    {
                        if (Directory.Exists(dir))
                        {
                            _profileDir = dir;
                            _profileName = name ?? "";
                            return;
                        }
                    }
                }
        }
        catch { }

        // Scan for any profile with a Positions.xml
        var profilesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "vatSys Files", "Profiles");

        if (Directory.Exists(profilesRoot))
        {
            foreach (var dir in Directory.GetDirectories(profilesRoot))
            {
                if (File.Exists(Path.Combine(dir, "Positions.xml")))
                {
                    _profileDir = dir;
                    return;
                }
            }
        }

        _profileDir = "";
    }
}

public sealed class AircraftDto
{
    public string Callsign { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double Hdg { get; set; }
    public double Gs { get; set; }
    public int Alt { get; set; } = -1;
    public bool OnGround { get; set; }
    public string Category { get; set; } = "unknown";
    public string WakeCat { get; set; } = "";
    public string AircraftType { get; set; } = "";
    public string Runway { get; set; } = "";
    public double[][]? History { get; set; }
}
