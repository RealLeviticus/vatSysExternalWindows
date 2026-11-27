using System;
using System.ComponentModel.Composition;
using vatsys;
using vatsys.Plugin;

namespace VatSysExternalWindows;

[Export(typeof(IPlugin))]
public sealed class ExternalWindowsPlugin : IPlugin, IDisposable
{
    private const int WebPort = 5300;

    private readonly WindowDataService _data;
    private readonly EmbeddedWebServer _server;

    private bool _disposed;

    public ExternalWindowsPlugin()
    {
        _data = new WindowDataService();
        _server = new EmbeddedWebServer(_data, WebPort);

        try
        {
            _server.Start();
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }

        // Web UI is accessible from other devices on the network — no local browser launch.
    }

    public string Name => "External Windows";

    public void OnFDRUpdate(FDP2.FDR updated) { }

    public void OnRadarTrackUpdate(RDP.RadarTrack updated) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _server.Dispose();
    }

    private void ReportError(Exception ex)
    {
        try { Errors.Add(ex, Name); } catch { }
    }
}
