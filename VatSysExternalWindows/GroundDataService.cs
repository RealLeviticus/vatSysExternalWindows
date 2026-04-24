using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                try { heading = track.Heading; } catch { }
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
                    var fdr = track.CoupledFDR ?? track.ASMGCSCoupledFDR;
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

                result.Add(new AircraftDto
                {
                    Callsign = callsign,
                    Lat = Math.Round(lat, 7),
                    Lon = Math.Round(lon, 7),
                    Hdg = Math.Round(heading, 1),
                    Gs = Math.Round(groundSpeed, 0),
                    OnGround = onGround,
                    Category = category,
                    WakeCat = wakeCat,
                    AircraftType = aircraftType,
                    Runway = runway,
                });
            }
            catch { }
        }

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
                            _profileName = name;
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
    public bool OnGround { get; set; }
    public string Category { get; set; } = "unknown";
    public string WakeCat { get; set; } = "";
    public string AircraftType { get; set; } = "";
    public string Runway { get; set; } = "";
}
