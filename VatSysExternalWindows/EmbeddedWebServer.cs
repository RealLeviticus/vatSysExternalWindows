using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VatSysExternalWindows;

public sealed class EmbeddedWebServer : IDisposable
{
    private static readonly byte[] HeaderEnd = { 13, 10, 13, 10 };

    private readonly GroundDataService _data;
    private readonly AsdDataService _asd;
    private readonly JavaScriptSerializer _json;
    private readonly CancellationTokenSource _cts;
    private readonly int _port;
    private byte[]? _fontCache;

    private readonly object _lock = new object();
    private TcpListener? _listener;
    private Task? _loop;
    private bool _disposed;

    public EmbeddedWebServer(GroundDataService data, AsdDataService asd, int port)
    {
        _data = data;
        _asd = asd;
        _port = port;
        _json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        _cts = new CancellationTokenSource();
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EmbeddedWebServer));
            if (_listener is not null) return;
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            _loop = Task.Run(AcceptLoop);
        }
    }

    public IReadOnlyList<string> GetAdvertisedUrls()
    {
        var urls = new List<string> { "http://localhost:" + _port + "/" };

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                urls.Add("http://" + ua.Address + ":" + _port + "/");
            }
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();

        lock (_lock) { try { _listener?.Stop(); } catch { } _listener = null; }
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                var l = _listener;
                if (l is null) return;
                client = await l.AcceptTcpClientAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleClient(client));
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (_cts.IsCancellationRequested) return; }
            catch { try { client?.Dispose(); } catch { } }
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        {
            NetworkStream? stream = null;
            try
            {
                stream = client.GetStream();
                var req = ReadRequest(stream);
                if (req is not null) Route(stream, req);
            }
            catch
            {
                if (stream is not null)
                    try { WriteJson(stream, 500, new { Error = "Server error." }); } catch { }
            }
        }
    }

    private void Route(NetworkStream s, HttpReq req)
    {
        if (string.Equals(req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            WriteResponse(s, 204, "text/plain", Array.Empty<byte>());
            return;
        }

        if (req.Path == "/" || req.Path == "/index.html")
        {
            WriteResponse(s, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(LandingPage.Html));
            return;
        }

        if (req.Path == "/ground" || req.Path == "/ground/")
        {
            WriteResponse(s, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(WebUiPage.Html));
            return;
        }

        if (req.Path == "/asd" || req.Path == "/asd/")
        {
            WriteResponse(s, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(AsdUiPage.Html));
            return;
        }

        // Serve Terminus Bold font for consistent rendering on all devices (iPad etc.)
        if (req.Path == "/font.ttf")
        {
            var font = GetFontBytes();
            if (font is not null)
            {
                WriteFontResponse(s, font);
                return;
            }
        }

        // List available positions (with name for menu display)
        if (req.Path == "/api/airports")
        {
            var positions = _data.GetPositions();
            var list = positions.Select(p => new {
                p.Name, p.Airport, p.Group, p.MapRefs,
                p.CenterLat, p.CenterLon, p.DefaultRange, p.Rotation, p.MagneticVariation
            }).ToList();
            WriteJson(s, 200, list);
            return;
        }

        // All Ground map metadata + colours (for dropdown menu)
        if (req.Path == "/api/groundmaps")
        {
            WriteJson(s, 200, new { Maps = _data.GetGroundMaps(), Colours = _data.GetColours() });
            return;
        }

        // Load geometry for specific map layers (on-demand)
        if (req.Path == "/api/maplayers")
        {
            if (!req.Query.TryGetValue("maps", out var mapsParam) || string.IsNullOrWhiteSpace(mapsParam))
            {
                WriteJson(s, 400, new { Error = "maps parameter required (comma-separated Folder/Name keys)." });
                return;
            }

            var mapKeys = mapsParam.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim())
                .Where(k => k.Length > 0)
                .ToList();

            WriteJson(s, 200, _data.GetMapLayers(mapKeys));
            return;
        }

        // Get map geometry + colours for an airport (legacy, still useful)
        if (req.Path == "/api/map")
        {
            if (!req.Query.TryGetValue("icao", out var icao) || string.IsNullOrWhiteSpace(icao))
            {
                WriteJson(s, 400, new { Error = "icao required." });
                return;
            }

            var map = _data.GetMap(icao);
            if (map is null)
            {
                WriteJson(s, 404, new { Error = "No map data for " + icao });
                return;
            }

            WriteJson(s, 200, new { Map = map, Colours = _data.GetColours() });
            return;
        }

        // Get live aircraft near an airport
        if (req.Path == "/api/aircraft")
        {
            if (!req.Query.TryGetValue("icao", out var icao) || string.IsNullOrWhiteSpace(icao))
            {
                WriteJson(s, 400, new { Error = "icao required." });
                return;
            }

            WriteJson(s, 200, _data.GetAircraft(icao));
            return;
        }

        // Get currently visible crossing/released maps from vatSys Ground window
        if (req.Path == "/api/visiblemaps")
        {
            WriteJson(s, 200, _data.GetVisibleCrossingReleasedMaps());
            return;
        }

        // ---- ASD API routes ----

        if (req.Path == "/api/asd/positions")
        {
            var positions = _asd.GetPositions();
            var list = positions.Select(p => new { p.Name, p.Group, p.CenterLat, p.CenterLon, p.DefaultRange, p.MapRefs }).ToList();
            WriteJson(s, 200, list);
            return;
        }

        if (req.Path == "/api/asd/maps")
        {
            WriteJson(s, 200, new { Maps = _asd.GetAsdMaps(), Colours = _asd.GetColours(), VelocityVectors = _asd.GetVelocityVectors() });
            return;
        }

        if (req.Path == "/api/asd/maplayers")
        {
            if (!req.Query.TryGetValue("maps", out var mapsParam) || string.IsNullOrWhiteSpace(mapsParam))
            {
                WriteJson(s, 400, new { Error = "maps parameter required." });
                return;
            }
            var mapKeys = mapsParam.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
            WriteJson(s, 200, _asd.GetMapLayers(mapKeys));
            return;
        }

        if (req.Path == "/api/asd/aircraft")
        {
            if (!req.Query.TryGetValue("lat", out var latStr) ||
                !req.Query.TryGetValue("lon", out var lonStr) ||
                !req.Query.TryGetValue("range", out var rangeStr))
            {
                WriteJson(s, 400, new { Error = "lat, lon, range required." });
                return;
            }
            if (!double.TryParse(latStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(lonStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon) ||
                !double.TryParse(rangeStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var range))
            {
                WriteJson(s, 400, new { Error = "Invalid lat/lon/range." });
                return;
            }
            WriteJson(s, 200, _asd.GetAircraft(lat, lon, range));
            return;
        }

        WriteJson(s, 404, new { Error = "Not found." });
    }

    private byte[]? GetFontBytes()
    {
        if (_fontCache is not null) return _fontCache;

        // Look for Terminus Bold in standard font locations
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "TerminusTTF-Bold_B.ttf"),
            @"C:\Windows\Fonts\TerminusTTF-Bold_B.ttf",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts", "TerminusTTF-Bold_B.ttf"),
        };

        foreach (var path in candidates)
        {
            try
            {
                if (File.Exists(path))
                {
                    _fontCache = File.ReadAllBytes(path);
                    return _fontCache;
                }
            }
            catch { }
        }

        return null;
    }

    private static void WriteFontResponse(NetworkStream s, byte[] body)
    {
        var header =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: font/ttf\r\n" +
            "Content-Length: " + body.Length + "\r\n" +
            "Connection: close\r\n" +
            "Cache-Control: public, max-age=604800\r\n" +
            "Access-Control-Allow-Origin: *\r\n\r\n";

        var hdr = Encoding.ASCII.GetBytes(header);
        s.Write(hdr, 0, hdr.Length);
        s.Write(body, 0, body.Length);
    }

    private void WriteJson(NetworkStream s, int code, object payload)
    {
        WriteResponse(s, code, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_json.Serialize(payload)));
    }

    private static void WriteResponse(NetworkStream s, int code, string ct, byte[] body)
    {
        var status = code switch
        {
            200 => "OK", 204 => "No Content", 400 => "Bad Request",
            404 => "Not Found", 500 => "Internal Server Error", _ => "OK",
        };

        var header =
            "HTTP/1.1 " + code + " " + status + "\r\n" +
            "Content-Type: " + ct + "\r\n" +
            "Content-Length: " + body.Length + "\r\n" +
            "Connection: close\r\n" +
            "Cache-Control: no-store\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Headers: Content-Type\r\n" +
            "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n\r\n";

        var hdr = Encoding.ASCII.GetBytes(header);
        s.Write(hdr, 0, hdr.Length);
        if (body.Length > 0) s.Write(body, 0, body.Length);
    }

    private static HttpReq? ReadRequest(NetworkStream stream)
    {
        var buf = new List<byte>(2048);
        var one = new byte[1];
        var di = 0;

        while (buf.Count < 32768)
        {
            if (stream.Read(one, 0, 1) <= 0) return null;
            buf.Add(one[0]);
            if (one[0] == HeaderEnd[di]) { di++; if (di == HeaderEnd.Length) break; }
            else di = one[0] == HeaderEnd[0] ? 1 : 0;
        }

        var text = Encoding.ASCII.GetString(buf.ToArray(), 0, buf.Count - HeaderEnd.Length);
        var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
        if (lines.Length == 0) return null;

        var rl = lines[0].Split(' ');
        if (rl.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var sep = lines[i].IndexOf(':');
            if (sep > 0) headers[lines[i].Substring(0, sep).Trim()] = lines[i].Substring(sep + 1).Trim();
        }

        var body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var len) && len > 0 && len < 524288)
        {
            body = new byte[len];
            var off = 0;
            while (off < len) { var r = stream.Read(body, off, len - off); if (r <= 0) break; off += r; }
        }

        var rawPath = rl[1].Trim();
        var qi = rawPath.IndexOf('?');
        var path = qi < 0 ? rawPath : rawPath.Substring(0, qi);
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (qi >= 0)
        {
            foreach (var pair in rawPath.Substring(qi + 1).Split('&'))
            {
                if (string.IsNullOrWhiteSpace(pair)) continue;
                var eq = pair.IndexOf('=');
                if (eq <= 0) { query[Uri.UnescapeDataString(pair)] = ""; continue; }
                query[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1));
            }
        }

        return new HttpReq { Method = rl[0].Trim(), Path = path, Query = query, Body = body };
    }

    private sealed class HttpReq
    {
        public string Method { get; set; } = "";
        public string Path { get; set; } = "";
        public Dictionary<string, string> Query { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; set; } = Array.Empty<byte>();
    }
}
