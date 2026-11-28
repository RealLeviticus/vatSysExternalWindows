using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using vatsys;
using vatsys.Plugin;

namespace VatSysExternalWindows;

/// <summary>
/// Ports of ASDControlDX.PaintGroundTracks, PaintASDTrack and PaintASDLabel: walks
/// vatSys's display tracks in the order Render paints them and decides, for each,
/// what vatSys would paint. The page projects and draws the result.
/// </summary>
internal static class TrackBuilder
{
    // ---- Air window ----

    /// <summary>Every display track an ASD window paints, in paint order. Tracks
    /// too far from the view for any part of them (symbol, velocity vector, label)
    /// to reach it are left out.</summary>
    public static List<TrackDto> Asd(double centreLat, double centreLon, double rangeNm)
    {
        var result = new List<TrackDto>();
        var centre = new Coordinate(centreLat, centreLon);
        foreach (var tl in DisplayState.OrderedTracks())
        {
            try
            {
                var dto = AsdTrack(tl, centre, rangeNm);
                if (dto is not null) result.Add(dto);
            }
            catch { }
        }
        return result;
    }

    /// <summary>PaintASDTrack, then PaintASDLabel, for one track.</summary>
    private static TrackDto? AsdTrack(Track tl, Coordinate centre, double rangeNm)
    {
        if (tl.State == MMI.HMIStates.None) return null;

        var location = tl.GetLocation();
        if (location is null) return null;

        var fdr = tl.GetFDR();
        if (!tl.IsVisibleASD) return null;

        var colour = AsdColour(tl, out var isNonJurisdictionBrush);

        RDP.RadarTrack? radarTrack = null;
        NetworkPilot? pilot = null;
        if (tl.Type == Track.TrackTypes.TRACK_TYPE_RADAR)
        {
            radarTrack = tl.SourceData as RDP.RadarTrack;
            pilot = radarTrack?.ActualAircraft;
            if (radarTrack is null || pilot is null) return null;
        }
        else if (tl.Type != Track.TrackTypes.TRACK_TYPE_FP || fdr?.PredictedPosition is null)
        {
            return null;
        }

        var dto = new TrackDto
        {
            Id = tl.ToString(),
            Lat = location.Latitude,
            Lon = location.Longitude,
            State = (int)tl.State,
            Col = Rgba(colour),
        };
        if (double.IsNaN(dto.Lat) || double.IsNaN(dto.Lon)) return null;

        var velocityVectors = MMI.VelocityVectors;
        var vectorNm = 0.0;

        try { dto.Srp = ShortRouteProbe(tl, fdr, location, out var srpSymbol); dto.SrpSym = srpSymbol; } catch { }

        var selectable = true;
        if (radarTrack != null && pilot != null)
        {
            var code = pilot.TransponderCode;
            var heading = radarTrack.Heading;
            var types = radarTrack.RadarTypes;

            dto.History = History(radarTrack);
            dto.HistSsr = code;

            if (velocityVectors > 0 && radarTrack.GroundSpeed > 0.0 && !radarTrack.Cancelled && heading != -1.0)
            {
                dto.Vv = velocityVectors;
                dto.Gs = Number(radarTrack.GroundSpeed);
                dto.Trk = Number(heading);
                vectorNm = radarTrack.GroundSpeed / 60.0 * velocityVectors;
            }

            // Mode S, else Mode A for Mode C returns, else ADS-B, else primary
            var symbol = "Primary";
            if (types.HasFlag(RDP.RadarTypes.SSR_ModeS)) symbol = "ModeS";
            else if (types.HasFlag(RDP.RadarTypes.SSR_ModeC)) symbol = "ModeA";
            else if (types.HasFlag(RDP.RadarTypes.ADSB)) symbol = "ADSB";

            if (radarTrack.Cancelled)
            {
                dto.Syms.Add(Symbol("Cancelled", code, heading));
                selectable = false;
            }
            else if (radarTrack.Coasting)
            {
                dto.Syms.Add(Symbol(symbol == "ADSB" ? "CoastADSB" : "CoastRadar", code, heading));
            }
            else if (symbol != "Primary")
            {
                // An identing track is painted in the ident colour, on the flash phase only
                int[]? identColour = radarTrack.Identing ? VatColours.Rgb(VatColours.Dx(Colours.Identities.IdentFlash)) : null;
                var main = Symbol(symbol, code, heading);
                main.Ident = radarTrack.Identing;
                main.Col = identColour;
                dto.Syms.Add(main);
                if (types.HasFlag(RDP.RadarTypes.PRI))
                {
                    var primary = Symbol("Primary", code, heading);
                    primary.Ident = radarTrack.Identing;
                    primary.Col = identColour;
                    dto.Syms.Add(primary);
                }
            }
            else if (types.HasFlag(RDP.RadarTypes.PRI))
            {
                dto.Syms.Add(Symbol("Primary", -1, heading));
            }
        }
        else
        {
            dto.Syms.Add(Symbol("FlightPlan", -1, fdr!.PredictedPosition.Track));
        }

        // Nothing of a track this far outside the view can reach it (the short
        // route probe can, so a track showing one is always sent)
        if (dto.Srp is null)
        {
            var reach = rangeNm + vectorNm;
            if (Conversions.CalculateDistance(centre, location) > reach) return null;
        }

        if (selectable && ReferenceEquals(MMI.SelectedTrack, tl))
            dto.Syms.Add(Symbol("Selection", -1, -1));
        dto.NoClick = !selectable;

        try { dto.Label = AsdLabel(tl, radarTrack, pilot, colour, isNonJurisdictionBrush); } catch { }
        return dto;
    }

    /// <summary>PaintASDLabel: whether the track gets a label, which type, and the
    /// brush PaintLabel is given.</summary>
    private static LabelDto? AsdLabel(Track tl, RDP.RadarTrack? radarTrack, NetworkPilot? pilot,
        Color trackColour, bool isNonJurisdictionBrush)
    {
        FDP2.FDR? fdr = null;
        var state = tl.State;
        bool show;

        if (tl.Type == Track.TrackTypes.TRACK_TYPE_RADAR)
        {
            if (radarTrack is null) return null;
            fdr = radarTrack.CoupledFDR;

            show = false;
            var types = radarTrack.RadarTypes & ~(RDP.RadarTypes.SMR | RDP.RadarTypes.SMR_ADSB);
            if (!radarTrack.Cancelled && (types != RDP.RadarTypes.PRI || radarTrack.CoupledFDR != null || radarTrack.QuickTag != null))
            {
                if (MMI.QuicklookAll || tl.IQL || state != MMI.HMIStates.NonJurisdiction || VatSysInternals.GetAlerts(tl).Count != 0)
                {
                    show = true;
                }
                else if (radarTrack.CorrectedAltitude < MMI.UpperLevelFilter && radarTrack.CorrectedAltitude > MMI.LowerLevelFilter && !radarTrack.OnGround)
                {
                    show = pilot is null ||
                           (!VatSysInternals.SsrFilterMatches(MMI.SSRCodeFilter1, pilot.TransponderCode) &&
                            !VatSysInternals.SsrFilterMatches(MMI.SSRCodeFilter2, pilot.TransponderCode));
                }
            }
        }
        else if (tl.Type == Track.TrackTypes.TRACK_TYPE_FP)
        {
            fdr = tl.SourceData as FDP2.FDR;
            if (fdr is null) return null;
            var departure = fdr.DepAirport;
            show = MMI.QuicklookAll || tl.IQL || !(fdr.ATD == DateTime.MaxValue) ||
                   !MMI.GroundFilter.Any(a => a.ICAOName == departure);
        }
        else
        {
            return null;
        }

        if (!show) return null;

        string labelType;
        if (fdr is null) labelType = radarTrack?.QuickTag is null ? "Limited" : "Quicktag";
        else labelType = tl.ManualExtend || (MMI.ExtendedLabels && state > MMI.HMIStates.PostJurisdiction) ? "Extended" : "Normal";

        // Handover-in and post-jurisdiction labels are given a transparent brush on
        // the flash phase; non-jurisdiction labels use their own colour
        var blink = state == MMI.HMIStates.HandoverIn || (!tl.IQL && state == MMI.HMIStates.PostJurisdiction);
        var labelColour = trackColour;
        if (!tl.IQL && state == MMI.HMIStates.NonJurisdiction && isNonJurisdictionBrush)
            labelColour = VatColours.Dx(Colours.Identities.NonJurisdictionLabel);

        return TrackLabelBuilder.Build(false, tl, fdr, tl.GetQuickTag(), radarTrack, pilot, labelType, labelColour, blink);
    }

    /// <summary>The short route probe PaintASDTrack draws ahead of a track: line
    /// segments [lat, lon, lat, lon] and markers [lat, lon, heading], in paint order.</summary>
    private static List<double[]>? ShortRouteProbe(Track tl, FDP2.FDR? fdr, Coordinate location, out string? markerSymbol)
    {
        markerSymbol = null;
        if (fdr is null) return null;
        if (!(tl.SRPVisible == MMI.SRPVisibilities.On ||
              (tl.SRPType == MMI.SRPTypes.Global && tl.SRPVisible == MMI.SRPVisibilities.Global && MMI.SRPDefaultOn)))
            return null;

        var ops = new List<double[]>();
        var list = fdr.ParsedRoute.ToList();
        var overflown = fdr.ParsedRoute.OverflownIndex;
        var markerHeading = fdr.PredictedPosition?.Track ?? -1.0;

        static double[] Line(Coordinate a, Coordinate b) => new[] { a.Latitude, a.Longitude, b.Latitude, b.Longitude };

        if (tl.SRPType == MMI.SRPTypes.Time || (tl.SRPType == MMI.SRPTypes.Global && MMI.SRPType == MMI.SRPTypes.Time))
        {
            markerSymbol = "SRPTimeMarker";
            var probeTime = DateTime.UtcNow + TimeSpan.FromMinutes(tl.SRPType == MMI.SRPTypes.Time ? tl.SRPValue : MMI.SRPValue);
            var last = list.FindLastIndex(s => s.ETO < probeTime);
            if (last != -1 && overflown != -1)
            {
                var clock = DateTime.MaxValue;
                for (var i = overflown; i < last + 1 && i < list.Count && i + 1 < list.Count; i++)
                {
                    var from = i != overflown ? list[i].Intersection.LatLong : location;
                    DateTime legStart;
                    if (i == overflown)
                    {
                        legStart = DateTime.UtcNow;
                        clock = legStart;
                    }
                    else
                    {
                        legStart = list[i].ETO;
                    }

                    var next = list[i + 1];
                    var legTime = next.ETO - legStart;
                    var remaining = probeTime - legStart;
                    var to = i + 1 == last + 1
                        ? Conversions.CalculateLLFromFractionOfRoute(from, next.Intersection.LatLong, remaining.TotalSeconds / legTime.TotalSeconds)
                        : next.Intersection.LatLong;

                    var steps = (int)(remaining.TotalMinutes / 5.0);
                    var marks = new List<Coordinate>();
                    for (var step = 0; step <= steps; step++)
                    {
                        if (clock + TimeSpan.FromMinutes(5.0) > next.ETO) break;
                        if (clock + TimeSpan.FromMinutes(5.0) > probeTime) break;
                        clock += TimeSpan.FromMinutes(5.0);
                        var fraction = (clock - legStart).TotalSeconds / legTime.TotalSeconds;
                        marks.Add(Conversions.CalculateLLFromFractionOfRoute(from, next.Intersection.LatLong, fraction));
                    }

                    var previous = from;
                    foreach (var mark in marks)
                    {
                        ops.Add(Line(previous, mark));
                        previous = mark;
                        ops.Add(new[] { mark.Latitude, mark.Longitude, markerHeading });
                    }
                    ops.Add(Line(previous, to));
                }
            }
        }
        else
        {
            markerSymbol = "SRPDistanceMarker";
            var covered = 0.0;
            var marked = 0.0;
            var probeNm = tl.SRPType != MMI.SRPTypes.Distance ? MMI.SRPValue : tl.SRPValue;
            const int MarkEveryNm = 20;
            for (var i = overflown; i + 1 < list.Count; i++)
            {
                if (!(covered <= probeNm)) break;

                var from = i != overflown ? list[i].Intersection.LatLong : location;
                var next = list[i + 1].Intersection.LatLong;
                var legNm = Conversions.CalculateDistance(from, next);
                var to = probeNm > covered + legNm
                    ? next
                    : Conversions.CalculateLLFromFractionOfRoute(from, next, (probeNm - covered) / legNm);

                var steps = (int)((probeNm - covered) / MarkEveryNm);
                var marks = new List<Coordinate>();
                for (var step = 0; step <= steps; step++)
                {
                    if (marked + MarkEveryNm > covered + legNm) break;
                    if (marked + MarkEveryNm > probeNm) break;
                    marked += MarkEveryNm;
                    marks.Add(Conversions.CalculateLLFromFractionOfRoute(from, next, (marked - covered) / legNm));
                }

                covered += legNm;
                ops.Add(Line(from, to));
                foreach (var mark in marks) ops.Add(new[] { mark.Latitude, mark.Longitude, markerHeading });
            }
        }

        // A degenerate leg (zero length or duration) yields NaN, which is not valid JSON
        ops.RemoveAll(op => op.Any(v => double.IsNaN(v) || double.IsInfinity(v)));
        return ops.Count > 0 ? ops : null;
    }

    // ---- Ground window ----

    /// <summary>Every track a Ground window showing <paramref name="icao"/> paints,
    /// in paint order. The window itself drops tracks outside its client area; the
    /// range only spares building labels for tracks nowhere near it.</summary>
    public static List<TrackDto> Ground(string icao, double centreLat, double centreLon, double rangeNm)
    {
        var result = new List<TrackDto>();
        var centre = new Coordinate(centreLat, centreLon);
        foreach (var tl in DisplayState.OrderedTracks())
        {
            try
            {
                var dto = GroundTrack(tl, icao, centre, rangeNm);
                if (dto is not null) result.Add(dto);
            }
            catch { }
        }
        return result;
    }

    /// <summary>PaintGroundTracks for one track.</summary>
    private static TrackDto? GroundTrack(Track tl, string icao, Coordinate centre, double rangeNm)
    {
        var radarTrack = tl.GetRadarTrack();
        var pilot = radarTrack?.ActualAircraft;
        if (radarTrack is null || radarTrack.Cancelled || pilot is null) return null;

        var location = radarTrack.LatLong;
        if (location is null || double.IsNaN(location.Latitude) || double.IsNaN(location.Longitude)) return null;
        if (Conversions.CalculateDistance(centre, location) > rangeNm) return null;

        // The symbol and label use the ASMGCS-coupled FDR first; the colour is
        // chosen from Track.GetFDR(includeASMGCS), which prefers the coupled FDR
        var fdr = radarTrack.ASMGCSCoupledFDR ?? radarTrack.CoupledFDR;
        var colour = GroundColour(tl, radarTrack.CoupledFDR ?? radarTrack.ASMGCSCoupledFDR, icao);

        var types = radarTrack.RadarTypes;
        var ssrAny = (types & (RDP.RadarTypes.SSR_ModeC | RDP.RadarTypes.SSR_ModeS | RDP.RadarTypes.ADSB | RDP.RadarTypes.SMR_ADSB)) != 0;
        var code = pilot.TransponderCode;
        // Ground symbols turn to the pilot's reported heading; airborne symbols
        // use the radar track's
        var aircraftHeading = pilot.Heading;

        var dto = new TrackDto
        {
            Id = pilot.Callsign ?? "",
            Lat = location.Latitude,
            Lon = location.Longitude,
            State = (int)tl.State,
            Col = Rgba(colour),
            History = History(radarTrack),
            HistSsr = -1,
        };

        // vatSys keeps the label flag on the Track; an airborne return with no SSR
        // source leaves whatever was there before
        var hasLabel = tl.HasASMGCSLabel;
        if (!radarTrack.OnGround)
        {
            if (types.HasFlag(RDP.RadarTypes.PRI) || types.HasFlag(RDP.RadarTypes.SMR))
                dto.Syms.Add(Symbol("Primary", -1, radarTrack.Heading));
            if (ssrAny)
            {
                dto.Syms.Add(Symbol("ModeS", code, radarTrack.Heading));
                hasLabel = true;
            }
        }
        else if (fdr != null || ssrAny)
        {
            hasLabel = true;

            // Wake category for the symbol: withheld when only Mode C is received or
            // the transponder is off on a primary/SMR return
            var modeCOnly = types.HasFlag(RDP.RadarTypes.SSR_ModeC) &&
                            (types & (RDP.RadarTypes.SSR_ModeS | RDP.RadarTypes.ADSB | RDP.RadarTypes.SMR_ADSB)) == 0;
            var transponderOff = !pilot.TransponderModeC &&
                                 (types & (RDP.RadarTypes.PRI | RDP.RadarTypes.SMR | RDP.RadarTypes.SMR_ADSB)) != 0;
            var wake = "";
            if (fdr != null && !transponderOff)
            {
                wake = fdr.AircraftWake ?? "";
            }
            else if (!modeCOnly && !transponderOff && !string.IsNullOrEmpty(pilot.AircraftType))
            {
                wake = Performance.GetAircraftFromType(pilot.AircraftType)?.WakeCategory ?? "";
            }
            else if (!modeCOnly && !transponderOff)
            {
                switch (VatSysInternals.EngineTypeName(pilot))
                {
                    case "Piston": wake = "L"; break;
                    case "Jet": wake = "M"; break;
                }
            }

            switch (wake)
            {
                case "L": dto.Syms.Add(Symbol("GroundLight", code, aircraftHeading)); break;
                case "M": dto.Syms.Add(Symbol("GroundMedium", code, aircraftHeading)); break;
                case "H": dto.Syms.Add(Symbol("GroundHeavy", code, aircraftHeading)); break;
                case "J": dto.Syms.Add(Symbol("GroundSuper", code, aircraftHeading)); break;
                default: dto.Syms.Add(Symbol("Ground", -1, aircraftHeading)); break;
            }
        }
        else
        {
            // Primary-only ground return: default symbol, no label
            hasLabel = false;
            dto.Syms.Add(Symbol("Ground", -1, aircraftHeading));
        }

        if (ReferenceEquals(MMI.SelectedGroundTrack, tl))
            dto.Syms.Add(Symbol("Selection", -1, aircraftHeading));

        if (hasLabel)
        {
            var labelType = "GroundLimited";
            if (fdr != null && !string.IsNullOrEmpty(icao))
            {
                if (radarTrack.OnGround && icao == fdr.DepAirport) labelType = "GroundDeparture";
                else if (radarTrack.OnGround && icao == fdr.DesAirport) labelType = "GroundArrival";
                else labelType = "GroundAir";
            }

            try { dto.Label = TrackLabelBuilder.Build(true, tl, fdr, tl.GetQuickTag(), radarTrack, pilot, labelType, colour); } catch { }
        }

        return dto;
    }

    // ---- Lookup ----

    /// <summary>The display track a window knows by <paramref name="id"/> (the
    /// TrackDto.Id it was sent), or null if it has gone.</summary>
    public static Track? Find(bool ground, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var tl in DisplayState.OrderedTracks())
        {
            try
            {
                if (ground)
                {
                    if (tl.GetRadarTrack()?.ActualAircraft?.Callsign == id) return tl;
                }
                else if (tl.ToString() == id)
                {
                    return tl;
                }
            }
            catch { }
        }
        return null;
    }

    // ---- Shared ----

    private static TrackSymbolDto Symbol(string type, int ssr, double heading)
    {
        return new TrackSymbolDto { Type = type, Ssr = ssr, Hdg = Number(heading) };
    }

    /// <summary>NaN and infinities would serialise as invalid JSON and lose the
    /// whole response, so they become 0.</summary>
    private static double Number(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? 0 : value;
    }

    /// <summary>[r, g, b], or [r, g, b, a] when the colour is not opaque (a plugin's
    /// custom track colour can carry alpha).</summary>
    public static int[] Rgba(Color c)
    {
        return c.A == 255 ? new[] { (int)c.R, c.G, c.B } : new[] { (int)c.R, c.G, c.B, c.A };
    }

    private static Color? PluginColour(bool asd, Track tl)
    {
        var custom = VatSysInternals.GetCustomTrackColour(asd, tl);
        if (custom is null) return null;
        return Color.FromArgb(custom.A, custom.R, custom.G, custom.B);
    }

    /// <summary>SelectASDBrush: plugin colour, else by HMI state (quick-look states
    /// use their own colours).</summary>
    private static Color AsdColour(Track tl, out bool isNonJurisdictionBrush)
    {
        isNonJurisdictionBrush = false;
        var custom = PluginColour(true, tl);
        if (custom.HasValue) return custom.Value;

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
        return VatColours.Dx(id);
    }

    /// <summary>SelectGroundBrush: plugin colour, else departure / arrival / local for
    /// the window's airport, else unknown.</summary>
    private static Color GroundColour(Track tl, FDP2.FDR? fdr, string icao)
    {
        var custom = PluginColour(false, tl);
        if (custom.HasValue) return custom.Value;

        if (fdr != null && !string.IsNullOrWhiteSpace(icao))
        {
            if (fdr.DepAirport == icao) return VatColours.Dx(Colours.Identities.GroundDeparture);
            if (fdr.DesAirport == icao) return VatColours.Dx(Colours.Identities.GroundArrival);
            return VatColours.Dx(Colours.Identities.GroundLocal);
        }

        return VatColours.Dx(Colours.Identities.GroundUnknown);
    }

    /// <summary>History markers: the newest MMI.HistoryDots positions, newest first,
    /// as [lat, lon, heading].</summary>
    private static double[][]? History(RDP.RadarTrack track)
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
                if (h?.LatLong is null || double.IsNaN(h.LatLong.Latitude) || double.IsNaN(h.LatLong.Longitude)) continue;
                dots.Add(new[] { h.LatLong.Latitude, h.LatLong.Longitude, Number(h.Heading) });
            }
            return dots.Count > 0 ? dots.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }
}
