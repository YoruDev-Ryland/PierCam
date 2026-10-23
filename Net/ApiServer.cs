using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PierCam.Capture;
using PierCam.Models;

namespace PierCam.Net;

/// <summary>What the API reads from the capture engine - plain values, so it can be tested without a camera.</summary>
internal interface IApiSource
{
    ApiSnapshot Snapshot();
    long FrameSequence { get; }
    int Width { get; }
    int Height { get; }
    bool CopyLatestFrame(Action<byte[]> consume);
}

internal sealed record ApiSnapshot(string State, string Message, string? CameraName, string? RecordingTitle,
    DateTime? RecordingStarted, long FramesRecorded, DateTime? NextFrameDue);

/// <summary>
/// A small, read-only HTTP API for other apps - a dashboard on another machine, over Tailscale or
/// a LAN: the live frame, what PierCam is doing, and the recorded nights with their videos.
///
/// A TcpListener rather than HttpListener, deliberately: HttpListener needs an administrator to
/// reserve the URL before it may answer anything but localhost, and observatory PCs are often run
/// without one. This only serves GET (and HEAD and the browser's OPTIONS) with one request per
/// connection, which is all a dashboard polling a frame or a video player seeking needs.
///
/// Nothing here changes anything: no settings, no camera, no recordings. Frames are the published
/// ones, after the blur mask, so nothing hidden in the app can be pulled from it.
/// </summary>
internal sealed class ApiServer : IDisposable
{
    private const int MaxConnections = 16;
    private const int MaxHeaderBytes = 16 * 1024;

    private readonly AppSettings _settings;
    private readonly IApiSource _engine;
    private readonly RoofMonitor _roof;
    private readonly Func<DateTime?> _nextSession;
    private readonly string _version;

    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _slots = new(MaxConnections);

    /// <summary>What the server is doing, for the settings panel.</summary>
    public string StatusLine { get; private set; } = "OFF.";
    public bool Listening { get; private set; }

    public ApiServer(AppSettings settings, IApiSource engine, RoofMonitor roof, Func<DateTime?> nextSession, string version)
    {
        _settings = settings;
        _engine = engine;
        _roof = roof;
        _nextSession = nextSession;
        _version = version;
    }

    /// <summary>Starts, restarts or stops to match the settings.</summary>
    public void Apply()
    {
        lock (_gate)
        {
            Stop();
            var s = _settings.Api;
            if (!s.Enabled) { StatusLine = "OFF."; return; }
            if (s.RequireKey && string.IsNullOrWhiteSpace(s.Key)) s.Key = NewKey();

            try
            {
                var listener = new TcpListener(s.AnyNetwork ? IPAddress.IPv6Any : IPAddress.Loopback, s.Port);
                if (s.AnyNetwork) listener.Server.DualMode = true;      // IPv4 and IPv6, one socket
                listener.Start(64);
                _listener = listener;
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                new Thread(() => AcceptLoop(listener, ct)) { IsBackground = true, Name = "PierCam API" }.Start();
                Listening = true;
                StatusLine = s.AnyNetwork
                    ? $"LISTENING ON PORT {s.Port}, ALL NETWORKS."
                    : $"LISTENING ON PORT {s.Port}, THIS PC ONLY.";
            }
            catch (SocketException ex)
            {
                Listening = false;
                StatusLine = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"PORT {s.Port} IS ALREADY IN USE BY ANOTHER PROGRAM - CHOOSE ANOTHER."
                    : $"COULD NOT LISTEN ON PORT {s.Port}: {ex.Message.ToUpperInvariant()}";
                App.Log(ex, "API");
            }
        }
    }

    private void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
        _cts = null;
        Listening = false;
    }

    public static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>The addresses another machine could use: this PC's IPv4 addresses, Tailscale's first.</summary>
    public static List<string> Addresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                        list.Add(a.Address.ToString());
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        // Tailscale hands out 100.64.0.0/10; those are the ones a remote dashboard will want.
        return list.OrderByDescending(a => a.StartsWith("100.", StringComparison.Ordinal)).ToList();
    }

    // ── connections ─────────────────────────────────────────────────────────────────────

    private void AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException) { return; }

            if (!_slots.Wait(0))
            {
                // Busy: say so rather than queue - a dashboard will simply try again.
                _ = Task.Run(() => { using (client) TryWrite(client.GetStream(), 503, "Busy", Json(new { error = "busy" }), "application/json"); });
                continue;
            }
            _ = Task.Run(() =>
            {
                try { using (client) Handle(client); }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
                catch (Exception ex) { App.Log(ex, "API request"); }
                finally { _slots.Release(); }
            });
        }
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers);

    private void Handle(TcpClient client)
    {
        client.ReceiveTimeout = 10000;
        client.SendTimeout = 30000;
        var stream = client.GetStream();
        var request = ReadRequest(stream);
        if (request is null) return;

        if (request.Method == "OPTIONS") { Write(stream, 204, "No Content", Array.Empty<byte>(), null, head: false); return; }
        if (request.Method is not ("GET" or "HEAD")) { Error(stream, 405, "Method Not Allowed", "only GET is supported"); return; }
        var head = request.Method == "HEAD";
        var parts = request.Path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        // The reference is open to anyone who can reach the port, key or no key: it says how to
        // use the API and nothing about this scope.
        switch (parts)
        {
            case ["docs"] or ["api", "v1", "docs"]:
                Write(stream, 200, "OK", Encoding.UTF8.GetBytes(ApiDocs.Page), "text/html; charset=utf-8", head);
                return;
            case ["api", "v1", "docs.md"]:
                Write(stream, 200, "OK", Encoding.UTF8.GetBytes(ApiDocs.Markdown), "text/markdown; charset=utf-8", head);
                return;
        }

        var s = _settings.Api;
        if (s.RequireKey)
        {
            var given = request.Headers.GetValueOrDefault("x-api-key") ?? request.Query.GetValueOrDefault("key") ?? "";
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(s.Key)))
            {
                Error(stream, 401, "Unauthorized", "an access key is required: X-Api-Key header or key= parameter");
                return;
            }
        }

        switch (parts)
        {
            case [] or ["api"] or ["api", "v1"]:
                Send(stream, head, Index());
                break;
            case ["api", "v1", "status"]:
                Send(stream, head, Status());
                break;
            case ["api", "v1", "frame.jpg"]:
                Frame(stream, request, head);
                break;
            case ["api", "v1", "sessions"]:
                Send(stream, head, Sessions().Select(Describe).ToList());
                break;
            case ["api", "v1", "sessions", var id]:
                if (FindSession(id) is { } m) Send(stream, head, Describe(m));
                else Error(stream, 404, "Not Found", "no such session");
                break;
            case ["api", "v1", "sessions", var id, var file]:
                SessionFile(stream, request, head, id, file);
                break;
            default:
                Error(stream, 404, "Not Found", "no such endpoint - see /api/v1");
                break;
        }
    }

    private static Request? ReadRequest(NetworkStream stream)
    {
        var buffer = new byte[MaxHeaderBytes];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) return null;
            read += n;
            if (IndexOf(buffer, read, "\r\n\r\n"u8) >= 0) break;
        }
        var text = Encoding.ASCII.GetString(buffer, 0, read);
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (end < 0) return null;
        var lines = text[..end].Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;

        var target = first[1];
        var q = target.IndexOf('?');
        var path = Uri.UnescapeDataString(q < 0 ? target : target[..q]);
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (q >= 0)
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
                query[key] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return new Request(first[0].ToUpperInvariant(), path, query, headers);
    }

    private static int IndexOf(byte[] buffer, int length, ReadOnlySpan<byte> needle) =>
        buffer.AsSpan(0, length).IndexOf(needle);

    // ── endpoints ───────────────────────────────────────────────────────────────────────

    private object Index() => new
    {
        name = "PierCam",
        version = _version,
        docs = "/api/v1/docs",
        endpoints = new[]
        {
            "/api/v1/docs",
            "/api/v1/docs.md",
            "/api/v1/status",
            "/api/v1/frame.jpg?width=1280&quality=85",
            "/api/v1/sessions",
            "/api/v1/sessions/{id}",
            "/api/v1/sessions/{id}/video.mp4",
            "/api/v1/sessions/{id}/marked.mp4",
            "/api/v1/sessions/{id}/poster.jpg",
            "/api/v1/sessions/{id}/marked-poster.jpg",
        },
    };

    private object Status()
    {
        var st = _engine.Snapshot();
        var roof = _roof.Status;
        return new
        {
            version = _version,
            time = DateTimeOffset.Now,
            state = st.State,
            message = st.Message,
            recording = st.RecordingStarted is null ? null : new
            {
                title = st.RecordingTitle,
                started = st.RecordingStarted,
                frames = st.FramesRecorded,
                nextFrame = st.NextFrameDue,
            },
            nextSession = _nextSession(),
            camera = st.CameraName is null ? null : new { name = st.CameraName, width = _engine.Width, height = _engine.Height },
            frame = new { sequence = _engine.FrameSequence, url = "/api/v1/frame.jpg" },
            roof = new
            {
                source = _settings.Session.RoofSource.ToString(),
                state = roof.State.ToString(),
                detail = roof.Detail,
                gating = _settings.Session.RequireRoofOpen,
            },
        };
    }

    // The live frame, encoded once per (frame, size, quality) however many dashboards ask for it.
    private readonly object _jpegGate = new();
    private (long seq, int width, int quality, byte[] bytes)? _jpeg;

    private void Frame(NetworkStream stream, Request request, bool head)
    {
        var seq = _engine.FrameSequence;
        int w = _engine.Width, h = _engine.Height;
        if (seq == 0 || w <= 0 || h <= 0) { Error(stream, 503, "Service Unavailable", "no frame yet - is the camera connected?"); return; }

        var width = int.TryParse(request.Query.GetValueOrDefault("width"), out var wq) ? Math.Clamp(wq, 64, w) : Math.Min(w, 1920);
        var quality = int.TryParse(request.Query.GetValueOrDefault("quality"), out var qq) ? Math.Clamp(qq, 30, 95) : 85;
        var etag = $"\"{seq}-{width}-{quality}\"";
        var extra = new Dictionary<string, string> { ["ETag"] = etag, ["X-Frame-Sequence"] = seq.ToString(CultureInfo.InvariantCulture), ["Cache-Control"] = "no-cache" };
        if (request.Headers.GetValueOrDefault("if-none-match") == etag) { Write(stream, 304, "Not Modified", Array.Empty<byte>(), null, head: true, extra); return; }

        byte[] jpeg;
        lock (_jpegGate)
        {
            if (_jpeg is { } c && c.seq == seq && c.width == width && c.quality == quality) jpeg = c.bytes;
            else
            {
                byte[]? copy = null;
                if (!_engine.CopyLatestFrame(rgb => copy = Shrink(rgb, w, h, width))) { Error(stream, 503, "Service Unavailable", "no frame yet"); return; }
                var oh = (int)Math.Round((double)h * width / w);
                jpeg = EncodeJpeg(copy!, width, oh, quality);
                _jpeg = (seq, width, quality, jpeg);
            }
        }
        Write(stream, 200, "OK", jpeg, "image/jpeg", head, extra);
    }

    /// <summary>A box-filtered copy at the requested width; the full frame when no smaller one is asked for.</summary>
    private static byte[] Shrink(byte[] rgb, int w, int h, int width)
    {
        if (width >= w) return (byte[])rgb.Clone();
        var height = (int)Math.Round((double)h * width / w);
        var o = new byte[width * height * 3];
        double sx = (double)w / width, sy = (double)h / height;
        for (var y = 0; y < height; y++)
        {
            int y0 = (int)(y * sy), y1 = Math.Max(y0 + 1, (int)((y + 1) * sy));
            for (var x = 0; x < width; x++)
            {
                int x0 = (int)(x * sx), x1 = Math.Max(x0 + 1, (int)((x + 1) * sx));
                int r = 0, g = 0, b = 0, n = 0;
                for (var yy = y0; yy < y1; yy += Math.Max(1, (y1 - y0) / 3))
                for (var xx = x0; xx < x1; xx += Math.Max(1, (x1 - x0) / 3))
                {
                    var i = (yy * w + xx) * 3;
                    r += rgb[i]; g += rgb[i + 1]; b += rgb[i + 2]; n++;
                }
                var k = (y * width + x) * 3;
                o[k] = (byte)(r / n); o[k + 1] = (byte)(g / n); o[k + 2] = (byte)(b / n);
            }
        }
        return o;
    }

    private static byte[] EncodeJpeg(byte[] rgb, int w, int h, int quality)
    {
        var source = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Rgb24, null, rgb, w * 3);
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private List<TimelapseManifest> Sessions()
    {
        var root = _settings.TimelapseRoot;
        if (!Directory.Exists(root)) return new();
        return Directory.EnumerateDirectories(root)
            .Select(TimelapseManifest.TryLoad)
            .Where(m => m is not null)
            .Select(m => m!)
            .OrderByDescending(m => m.StartedLocal)
            .ToList();
    }

    /// <summary>A session by its folder name - matched against the real folders, never used as a path.</summary>
    private TimelapseManifest? FindSession(string id)
    {
        var root = _settings.TimelapseRoot;
        if (!Directory.Exists(root)) return null;
        var folder = Directory.EnumerateDirectories(root).FirstOrDefault(d => string.Equals(Path.GetFileName(d), id, StringComparison.OrdinalIgnoreCase));
        return folder is null ? null : TimelapseManifest.TryLoad(folder);
    }

    private static object Describe(TimelapseManifest m)
    {
        var id = Path.GetFileName(m.FolderPath);
        var finished = m.Status != SessionStatus.Recording;
        var video = finished && File.Exists(m.VideoPath) ? new FileInfo(m.VideoPath) : null;
        var marked = finished && m.MarkedVideoPath is { } mp && File.Exists(mp) ? new FileInfo(mp) : null;
        var b = $"/api/v1/sessions/{Uri.EscapeDataString(id)}";
        return new
        {
            id,
            title = m.Title,
            status = m.Status.ToString(),
            started = m.StartedLocal,
            ended = m.EndedLocal,
            frames = m.FrameCount,
            fps = m.Fps,
            seconds = m.Fps > 0 ? Math.Round((double)m.FrameCount / m.Fps, 1) : 0,
            width = m.Width,
            height = m.Height,
            camera = m.CameraName,
            poster = File.Exists(m.PosterPath) ? $"{b}/poster.jpg" : null,
            video = video is null ? null : new { url = $"{b}/video.mp4", bytes = video.Length },
            marked = marked is null ? null : new
            {
                url = $"{b}/marked.mp4",
                bytes = marked.Length,
                poster = m.MarkedPosterPath is { } pp && File.Exists(pp) ? $"{b}/marked-poster.jpg" : null,
            },
        };
    }

    private void SessionFile(NetworkStream stream, Request request, bool head, string id, string file)
    {
        if (FindSession(id) is not { } m) { Error(stream, 404, "Not Found", "no such session"); return; }
        var finished = m.Status != SessionStatus.Recording;
        var (path, type) = file switch
        {
            "video.mp4" when finished => (m.VideoPath, "video/mp4"),
            "marked.mp4" when finished => (m.MarkedVideoPath, "video/mp4"),
            "poster.jpg" => (m.PosterPath, "image/jpeg"),
            "marked-poster.jpg" => (m.MarkedPosterPath, "image/jpeg"),
            "video.mp4" or "marked.mp4" => (null, ""),
            _ => (null, ""),
        };
        if (path is null || !File.Exists(path))
        {
            Error(stream, 404, "Not Found", file.EndsWith(".mp4", StringComparison.Ordinal) && !finished
                ? "still recording - the video is ready when the night is" : "no such file for this session");
            return;
        }
        ServeFile(stream, request, head, path, type);
    }

    /// <summary>A file, whole or the byte range asked for - what lets a video player seek.</summary>
    private static void ServeFile(NetworkStream stream, Request request, bool head, string path, string type)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
        var length = fs.Length;
        long start = 0, end = length - 1;
        var partial = false;
        if (request.Headers.GetValueOrDefault("range") is { } range && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = range[6..].Split(',')[0].Trim();
            var dash = spec.IndexOf('-');
            if (dash >= 0)
            {
                var a = spec[..dash]; var z = spec[(dash + 1)..];
                if (a.Length == 0 && long.TryParse(z, out var suffix)) { start = Math.Max(0, length - suffix); }
                else if (long.TryParse(a, out var s0))
                {
                    start = s0;
                    if (z.Length > 0 && long.TryParse(z, out var e0)) end = Math.Min(e0, length - 1);
                }
                if (start > end || start >= length)
                {
                    WriteHeaders(stream, 416, "Range Not Satisfiable", 0, null, new() { ["Content-Range"] = $"bytes */{length}" });
                    return;
                }
                partial = true;
            }
        }

        var count = end - start + 1;
        var extra = new Dictionary<string, string> { ["Accept-Ranges"] = "bytes" };
        if (partial) extra["Content-Range"] = $"bytes {start}-{end}/{length}";
        WriteHeaders(stream, partial ? 206 : 200, partial ? "Partial Content" : "OK", count, type, extra);
        if (head) return;

        fs.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n <= 0) break;
            stream.Write(buffer, 0, n);
            count -= n;
        }
    }

    // ── writing ─────────────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    private static void Send(NetworkStream stream, bool head, object value) =>
        Write(stream, 200, "OK", Json(value), "application/json; charset=utf-8", head);

    private static void Error(NetworkStream stream, int code, string reason, string message) =>
        Write(stream, code, reason, Json(new { error = message }), "application/json; charset=utf-8", head: false);

    private static void TryWrite(NetworkStream stream, int code, string reason, byte[] body, string type)
    {
        try { Write(stream, code, reason, body, type, head: false); }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
    }

    private static void Write(NetworkStream stream, int code, string reason, byte[] body, string? type, bool head,
        Dictionary<string, string>? extra = null)
    {
        WriteHeaders(stream, code, reason, body.Length, type, extra);
        if (!head && body.Length > 0) stream.Write(body, 0, body.Length);
    }

    private static void WriteHeaders(NetworkStream stream, int code, string reason, long length, string? type,
        Dictionary<string, string>? extra = null)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
        if (type is not null) sb.Append("Content-Type: ").Append(type).Append("\r\n");
        sb.Append("Content-Length: ").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        // A dashboard in a browser on another machine is a different origin; it may read this.
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Access-Control-Allow-Methods: GET, HEAD, OPTIONS\r\n");
        sb.Append("Access-Control-Allow-Headers: X-Api-Key, Range, If-None-Match\r\n");
        sb.Append("Access-Control-Expose-Headers: Content-Range, Accept-Ranges, ETag, X-Frame-Sequence\r\n");
        if (extra is not null) foreach (var (k, v) in extra) sb.Append(k).Append(": ").Append(v).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        stream.Write(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        lock (_gate) Stop();
    }
}
