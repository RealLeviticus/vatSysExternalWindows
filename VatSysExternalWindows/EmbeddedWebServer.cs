using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using vatsys;

namespace VatSysExternalWindows;

public sealed class EmbeddedWebServer : IDisposable
{
    private const int MaxHeaderBytes = 32768;
    // The only request bodies are small JSON commands
    private const int MaxBodyBytes = 65536;
    // An action runs on vatSys's GUI thread; the page is told when that is busy
    private const int ActionWaitMs = 3000;
    // A request must arrive in full within this window, however slowly the
    // client trickles it — a stalled connection must not hold a worker.
    private const int RequestTimeoutMs = 10000;
    private const int SendTimeoutMs = 10000;
    // Each page polls a few times a second on short-lived connections; anything
    // far beyond that is not one of our clients.
    private const int MaxConcurrentConnections = 64;

    private static readonly byte[] HeaderEnd = { 13, 10, 13, 10 };
    private static readonly byte[] LandingBytes = Encoding.UTF8.GetBytes(LandingPage.Html);

    private readonly WindowDataService _data;
    private readonly JavaScriptSerializer _json;
    private readonly CancellationTokenSource _cts;
    private readonly int _port;
    private byte[]? _fontCache;
    private int _activeConnections;

    private readonly object _lock = new object();
    private TcpListener? _listener;
    private Task? _loop;
    private bool _disposed;

    public EmbeddedWebServer(WindowDataService data, int port)
    {
        _data = data;
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

                if (Interlocked.Increment(ref _activeConnections) > MaxConcurrentConnections)
                {
                    Interlocked.Decrement(ref _activeConnections);
                    client.Dispose();
                    continue;
                }

                var accepted = client;
                _ = Task.Run(() =>
                {
                    try { HandleClient(accepted); }
                    finally { Interlocked.Decrement(ref _activeConnections); }
                });
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
            try
            {
                client.SendTimeout = SendTimeoutMs;
                var stream = client.GetStream();

                var req = ReadRequest(client, stream);
                if (req is null) return;

                // Build the whole response before writing anything, so a failure
                // while gathering data becomes a clean 500 rather than a second
                // response appended to a half-written one.
                Response response;
                try { response = Route(req); }
                catch { response = Json(500, new { Error = "Server error." }); }

                WriteResponse(stream, response, headOnly: req.Method == "HEAD");
            }
            catch
            {
                // Client went away or timed out — nothing useful to send.
            }
        }
    }

    private Response Route(HttpReq req)
    {
        if (req.Method == "POST") return RoutePost(req);
        if (req.Method != "GET" && req.Method != "HEAD")
            return Json(405, new { Error = "Method not allowed." });

        switch (req.Path)
        {
            case "/":
            case "/index.html":
                return Html(LandingBytes);

            // Both windows are the same page; it reads its type from the path,
            // as vatSys uses one ASDControlDX for both.
            case "/ground":
            case "/ground/":
            case "/asd":
            case "/asd/":
                return Asset("display.html", "text/html; charset=utf-8");

            case "/display.js":
            case "/display-ui.js":
            case "/display-popups.js":
            case "/ozstrips.js":
                return Asset(req.Path.Substring(1), "text/javascript; charset=utf-8");

            case "/display.css":
            case "/ozstrips.css":
                return Asset(req.Path.Substring(1), "text/css; charset=utf-8");

            // The OzStrips strip board (when the OzStrips plugin is loaded)
            case "/ozstrips":
            case "/ozstrips/":
                return Asset("ozstrips.html", "text/html; charset=utf-8");

            case "/api/ozstrips/state":
                return Json(200, OzStripsBridge.GetState());

            // Terminus Bold for consistent rendering on all devices (iPad etc.)
            case "/font.ttf":
            {
                var font = GetFontBytes();
                return font is null
                    ? Json(404, new { Error = "Font not installed." })
                    : new Response(200, "font/ttf", font) { CacheSeconds = 604800 };
            }

            // What a window loads once: its positions, the maps in its Maps menu
            // and the track symbol geometry
            case "/api/static":
            {
                var ground = IsGround(req);
                return Json(200, new
                {
                    Positions = PositionSource.List(ground ? LogicalPositions.PositionTypes.ASMGCS : LogicalPositions.PositionTypes.ASD),
                    Maps = MapSource.ListMaps(ground ? DisplayMaps.MapCategories.Ground : DisplayMaps.MapCategories.ASD),
                    Symbols = _data.GetTrackSymbols(),
                });
            }

            // What changes while vatSys runs, polled every couple of seconds: colours
            // (brightness), fonts, display settings, the runtime maps and the tool
            // graphics
            case "/api/state":
            {
                var ground = IsGround(req);
                return Json(200, new
                {
                    Colours = VatColours.All(),
                    Fonts = FontMeasurer.Get(),
                    Dynamic = DynamicMaps.Get(),
                    Overlays = DisplayState.GetOverlays(ground),
                    // Crossing/Release maps currently shown in the vatSys Ground window
                    VisibleMaps = ground ? _data.GetVisibleCrossingReleasedMaps() : null,
                });
            }

            // Geometry for specific map layers (on demand)
            case "/api/maplayers":
            {
                var keys = ParseMapKeys(req);
                return keys is null
                    ? Json(400, new { Error = "maps parameter required (comma-separated Folder/Name keys)." })
                    : Json(200, MapSource.GetLayers(keys));
            }

            // Live tracks: lat/lon = the window's screen centre, range = how far its
            // view reaches. A Ground window also says which airport it is for.
            case "/api/aircraft":
            {
                if (!req.Query.TryGetValue("lat", out var latText) || !TryParseNumber(latText, out var lat) ||
                    !req.Query.TryGetValue("lon", out var lonText) || !TryParseNumber(lonText, out var lon) ||
                    !req.Query.TryGetValue("range", out var rangeText) || !TryParseNumber(rangeText, out var range))
                {
                    return Json(400, new { Error = "lat, lon and range required." });
                }

                if (IsGround(req))
                {
                    req.Query.TryGetValue("icao", out var icao);
                    return Json(200, TrackBuilder.Ground(icao ?? "", lat, lon, range));
                }

                return Json(200, TrackBuilder.Asd(lat, lon, range));
            }

            default:
                return Json(404, new { Error = "Not found." });
        }
    }

    /// <summary>The requests that change something in vatSys. They are accepted
    /// only as JSON posted by the plugin's own pages: a form on another website
    /// cannot send that content type, and a script there is refused by the origin
    /// check (and gets no CORS approval for the preflight).</summary>
    private Response RoutePost(HttpReq req)
    {
        if (req.Path != "/api/action" && req.Path != "/api/ozstrips/command")
            return Json(405, new { Error = "Method not allowed." });

        if (!req.Header("Content-Type").StartsWith("application/json", StringComparison.OrdinalIgnoreCase) || !SameOrigin(req))
            return Json(403, new { Error = "Forbidden." });

        Dictionary<string, object?> body;
        try
        {
            if (_json.DeserializeObject(Encoding.UTF8.GetString(req.Body)) is not Dictionary<string, object?> parsed)
                return Json(400, new { Error = "A JSON object is required." });
            body = new Dictionary<string, object?>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return Json(400, new { Error = "Invalid JSON." });
        }

        string Text(string key) => body.TryGetValue(key, out var value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";
        int? Number(string key) => int.TryParse(Text(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

        if (req.Path == "/api/ozstrips/command")
        {
            var action = Text("Action");
            if (action.Length == 0) return Json(400, new { Error = "Action required." });
            string? Optional(string key) => body.TryGetValue(key, out var value) && value != null ? Text(key) : null;
            return Json(200, OzStripsBridge.Execute(action, Optional("Callsign"), Optional("Bay"), Optional("Value"), Number("Style")));
        }

        var request = new ActionRequest
        {
            Ground = string.Equals(Text("Type"), "ground", StringComparison.OrdinalIgnoreCase),
            Id = Text("Id"),
            Action = Text("Action"),
            Arg = Text("Arg"),
            Arg2 = Text("Arg2"),
            Ctx = Text("Ctx"),
            Button = Number("Button") ?? 0,
        };
        if (body.TryGetValue("Toggles", out var toggles) && toggles is System.Collections.IEnumerable names && toggles is not string)
        {
            foreach (var name in names)
            {
                if (name != null) request.Toggles.Add(Convert.ToString(name, CultureInfo.InvariantCulture) ?? "");
            }
        }
        if (request.Action.Length == 0) return Json(400, new { Error = "Action required." });

        var busy = new ActionResult { Ok = false, Error = "vatSys is busy; try again." };
        return Json(200, GuiThread.Call(() => TrackActions.Execute(request), ActionWaitMs, busy));
    }

    /// <summary>A browser names the page a POST comes from in Origin; it must be
    /// the address this server was reached at. Requests without one (not from a
    /// browser page) are let through.</summary>
    private static bool SameOrigin(HttpReq req)
    {
        var origin = req.Header("Origin");
        if (origin.Length == 0) return true;
        var host = req.Header("Host");
        return host.Length > 0 && Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Authority, host, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string>? ParseMapKeys(HttpReq req)
    {
        if (!req.Query.TryGetValue("maps", out var maps) || string.IsNullOrWhiteSpace(maps))
            return null;

        return maps.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(k => k.Trim())
            .Where(k => k.Length > 0)
            .ToList();
    }

    private static bool IsGround(HttpReq req)
    {
        return req.Query.TryGetValue("type", out var type) && string.Equals(type, "ground", StringComparison.OrdinalIgnoreCase);
    }

    private static Response Asset(string name, string contentType)
    {
        var bytes = WebAssets.Get(name);
        return bytes is null
            ? new Response(404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found."))
            : new Response(200, contentType, bytes);
    }

    private static bool TryParseNumber(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               !double.IsNaN(value) && !double.IsInfinity(value);
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
                    _fontCache = FontRepair.Fix(File.ReadAllBytes(path));
                    return _fontCache;
                }
            }
            catch { }
        }

        return null;
    }

    private static Response Html(byte[] body)
    {
        return new Response(200, "text/html; charset=utf-8", body);
    }

    private Response Json(int code, object payload)
    {
        return new Response(code, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_json.Serialize(payload)));
    }

    // The pages are served from this same origin, so no CORS headers are sent:
    // other websites have no business reading the API.
    private static void WriteResponse(NetworkStream s, Response r, bool headOnly)
    {
        var status = r.Code switch
        {
            200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found",
            405 => "Method Not Allowed", 500 => "Internal Server Error", _ => "OK",
        };

        var header =
            "HTTP/1.1 " + r.Code + " " + status + "\r\n" +
            "Content-Type: " + r.ContentType + "\r\n" +
            "Content-Length: " + r.Body.Length + "\r\n" +
            "Connection: close\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            (r.Code == 405 ? "Allow: GET, HEAD, POST\r\n" : "") +
            (r.CacheSeconds > 0 ? "Cache-Control: public, max-age=" + r.CacheSeconds : "Cache-Control: no-store") +
            "\r\n\r\n";

        var hdr = Encoding.ASCII.GetBytes(header);
        s.Write(hdr, 0, hdr.Length);
        if (!headOnly && r.Body.Length > 0) s.Write(r.Body, 0, r.Body.Length);
    }

    /// <summary>Reads the request line, the headers and, for a POST, the body.
    /// Returns null if the client disconnects, sends an oversized or malformed
    /// request, or doesn't finish within the request timeout.</summary>
    private static HttpReq? ReadRequest(TcpClient client, NetworkStream stream)
    {
        var buf = new byte[4096];
        var len = 0;
        var end = -1;
        var clock = Stopwatch.StartNew();

        while (end < 0)
        {
            var remaining = RequestTimeoutMs - (int)clock.ElapsedMilliseconds;
            if (remaining <= 0) return null;
            if (len == buf.Length)
            {
                if (buf.Length >= MaxHeaderBytes) return null;
                Array.Resize(ref buf, buf.Length * 2);
            }

            client.ReceiveTimeout = remaining;
            var read = stream.Read(buf, len, buf.Length - len);
            if (read <= 0) return null;

            // Only the new bytes (plus a 3-byte overlap) can complete the terminator
            var from = Math.Max(0, len - (HeaderEnd.Length - 1));
            len += read;
            end = IndexOf(buf, HeaderEnd, from, len);
        }

        var lines = Encoding.ASCII.GetString(buf, 0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        var rawPath = requestLine[1].Trim();
        var qi = rawPath.IndexOf('?');
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

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon > 0) headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
        }

        var method = requestLine[0].Trim().ToUpperInvariant();
        var body = Array.Empty<byte>();
        if (method == "POST")
        {
            if (!headers.TryGetValue("Content-Length", out var lengthText) ||
                !int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                length < 0 || length > MaxBodyBytes)
            {
                return null;
            }

            // Part of the body may have arrived with the headers
            body = new byte[length];
            var bodyStart = end + HeaderEnd.Length;
            var have = Math.Min(length, len - bodyStart);
            Array.Copy(buf, bodyStart, body, 0, have);
            while (have < length)
            {
                var remaining = RequestTimeoutMs - (int)clock.ElapsedMilliseconds;
                if (remaining <= 0) return null;
                client.ReceiveTimeout = remaining;
                var read = stream.Read(body, have, length - have);
                if (read <= 0) return null;
                have += read;
            }
        }

        return new HttpReq
        {
            Method = method,
            Path = qi < 0 ? rawPath : rawPath.Substring(0, qi),
            Query = query,
            Headers = headers,
            Body = body,
        };
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from, int length)
    {
        for (var i = from; i <= length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private sealed class HttpReq
    {
        public string Method { get; set; } = "";
        public string Path { get; set; } = "";
        public Dictionary<string, string> Query { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; set; } = Array.Empty<byte>();

        public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : "";
    }

    private sealed class Response
    {
        public Response(int code, string contentType, byte[] body)
        {
            Code = code;
            ContentType = contentType;
            Body = body;
        }

        public int Code { get; }
        public string ContentType { get; }
        public byte[] Body { get; }
        /// <summary>0 = no-store; otherwise a public max-age in seconds.</summary>
        public int CacheSeconds { get; set; }
    }
}
