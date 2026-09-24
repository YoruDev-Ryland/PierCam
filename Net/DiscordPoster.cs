using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PierCam.Library;
using PierCam.Models;

namespace PierCam.Net;

/// <summary>What the poster is doing, for the one status line the config page shows.</summary>
internal enum DiscordPhase { Off, Idle, Working, Failed }

/// <summary>
/// Posts the night to a Discord channel through an incoming webhook.
///
/// A webhook is a plain HTTPS POST — no bot account, no gateway, no OAuth — so this works for
/// anyone who can paste a URL out of their channel's Integrations settings.
///
/// Everything here is subordinate to the recording. Posts are queued and sent by one background
/// worker, so a slow upload or a dead network cannot reach the capture loop; the queue is bounded
/// and drops its oldest entry rather than growing through a night; and every failure is a status
/// line and a log entry, never an exception that escapes.
///
/// The webhook URL is a password in URL form. It is never written to the log, never included in
/// the diagnostics text, and is masked by <see cref="Mask"/> anywhere it would otherwise appear.
/// </summary>
internal sealed class DiscordPoster : IDisposable
{
    /// <summary>
    /// Only Discord's own hosts are accepted. A webhook post carries pictures of the user's
    /// observatory, and a typo or a pasted link from somewhere else should fail loudly here
    /// rather than quietly ship frames to a stranger's server.
    /// </summary>
    private static readonly string[] AllowedHosts =
        { "discord.com", "discordapp.com", "ptb.discord.com", "canary.discord.com" };

    /// <summary>Discord's own cap on a message body.</summary>
    private const int MaxContent = 2000;

    /// <summary>Bounded so a night with no network cannot grow it without limit.</summary>
    private const int MaxQueue = 8;

    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _work = new(0);
    private readonly Queue<Post> _queue = new();
    private readonly object _gate = new();
    private readonly string _ffmpegPath;
    private Thread? _worker;

    /// <summary>One queued post: a message, and at most one file to go with it.</summary>
    private sealed record Post(string Content, string? FileName, string? FilePath, bool MayShrink, RawFrame? Frame);

    /// <summary>A copied frame awaiting encoding, which happens on the worker rather than the capture thread.</summary>
    private sealed record RawFrame(byte[] Rgb, int Width, int Height);

    public DiscordPoster(AppSettings settings, string ffmpegPath)
    {
        _settings = settings;
        _ffmpegPath = ffmpegPath;
    }

    public DiscordPhase Phase { get; private set; } = DiscordPhase.Off;
    public string Status { get; private set; } = string.Empty;
    public event Action? StatusChanged;

    private DiscordSettings S => _settings.Discord;

    /// <summary>Whether the settings name somewhere to post to.</summary>
    public bool IsConfigured => S.Enabled && IsWebhookUrl(S.WebhookUrl);

    /// <summary>
    /// Test seam: also accept a webhook on this machine, so the self-test can drive the poster
    /// against a stand-in for Discord. Loopback only — even with this set, a post can never leave
    /// for a host that is not Discord's.
    /// </summary>
    internal static bool AllowLocalStandIn { get; set; }

    /// <summary>
    /// A webhook URL, or not. Deliberately strict: https, a Discord host, and the
    /// /api/webhooks/{id}/{token} shape.
    /// </summary>
    public static bool IsWebhookUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;

        var local = AllowLocalStandIn && uri.IsLoopback;
        if (uri.Scheme != Uri.UriSchemeHttps && !local) return false;
        if (!local && Array.IndexOf(AllowedHosts, uri.Host.ToLowerInvariant()) < 0) return false;

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        // api/webhooks/{id}/{token}, with an optional version segment: api/v10/webhooks/...
        var i = Array.IndexOf(parts, "webhooks");
        return i >= 0 && parts.Length >= i + 3
               && ulong.TryParse(parts[i + 1], out _)
               && parts[i + 2].Length >= 16;
    }

    /// <summary>The URL with its token hidden, for anything a human or a log file will see.</summary>
    public static string Mask(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "(not set)";
        if (!IsWebhookUrl(url)) return "(not a webhook URL)";
        var parts = url.Trim().TrimEnd('/').Split('/');
        return $"…/webhooks/{parts[^2]}/{new string('•', 8)}";
    }

    public void Start()
    {
        if (_worker is not null) return;
        _worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "PierCam Discord",
            // Below the capture thread, always. A night of frames outranks a chat message.
            Priority = ThreadPriority.BelowNormal
        };
        _worker.Start();
        Apply();
    }

    /// <summary>Re-reads the settings after the user changes them.</summary>
    public void Apply() =>
        SetStatus(S.Enabled ? (IsConfigured ? DiscordPhase.Idle : DiscordPhase.Failed) : DiscordPhase.Off,
            !S.Enabled ? "Off"
            : !IsWebhookUrl(S.WebhookUrl) ? "Paste the channel's webhook URL from Discord's Integrations settings."
            : $"Posting to {Mask(S.WebhookUrl)}.");

    // ───────────────────────────── what gets posted ─────────────────────────────

    /// <summary>True when a still is due: enabled, configured, and the interval has elapsed.</summary>
    public bool WantsStill(DateTime now) =>
        IsConfigured && S.PostStills &&
        (_lastStill is not { } last || now - last >= TimeSpan.FromMinutes(Math.Max(1, S.StillEveryMinutes)));

    private DateTime? _lastStill;

    /// <summary>
    /// Queues one frame. Called from the capture thread with the frame it just published, so it
    /// copies what it needs and returns: the JPEG encode happens on the worker.
    /// </summary>
    public void PostStill(byte[] rgb, int width, int height, string caption, DateTime now)
    {
        if (!WantsStill(now)) return;
        _lastStill = now;

        var copy = new byte[width * height * 3];
        Buffer.BlockCopy(rgb, 0, copy, 0, copy.Length);
        Enqueue(new Post(caption, $"piercam-{now:yyyyMMdd-HHmmss}.jpg", null, false,
            new RawFrame(copy, width, height)));
    }

    /// <summary>
    /// Queues one frame now, whatever the interval says. The buttons on the config page use this;
    /// pressing one is a decision, not a schedule.
    /// </summary>
    public void PostStillNow(byte[] rgb, int width, int height, string caption)
    {
        if (!IsConfigured) return;
        var copy = new byte[width * height * 3];
        Buffer.BlockCopy(rgb, 0, copy, 0, copy.Length);
        Enqueue(new Post(caption, $"piercam-{DateTime.Now:yyyyMMdd-HHmmss}.jpg", null, false,
            new RawFrame(copy, width, height)));
    }

    /// <summary>
    /// Queues the finished night: the video, and a summary of what it cost to make.
    /// </summary>
    /// <param name="force">A button press, which goes even when the nightly post is switched off.</param>
    public void PostNight(TimelapseManifest m, bool force = false)
    {
        if (!IsConfigured || (!S.PostTimelapse && !force)) return;
        if (!File.Exists(m.VideoPath)) return;

        var covered = Format.Duration(m.CapturedSpan);
        var plays = Format.Duration(m.VideoDuration);
        var saved = m.EstimatedRawBytes > m.VideoBytes && m.VideoBytes > 0
            ? $" · {Format.Bytes(m.EstimatedRawBytes - m.VideoBytes)} smaller than the frames it came from"
            : string.Empty;
        var content =
            $"**{m.Title}** — {m.FrameCount:N0} frames covering {covered}, plays in {plays}.\n" +
            $"{m.Width}×{m.Height} at {m.Fps} fps · {m.ExposureSeconds:0.##}s @ gain {m.Gain} · {Format.Bytes(m.VideoBytes)}{saved}";

        Enqueue(new Post(Trim(content), Path.GetFileName(m.VideoPath), m.VideoPath, S.ShrinkOversizeVideo, null));
    }

    /// <summary>A one-off message, for the Test button.</summary>
    public void PostTest() =>
        Enqueue(new Post("PierCam is connected to this channel. Stills and finished timelapses will appear here.",
            null, null, false, null));

    private static string Trim(string s) => s.Length <= MaxContent ? s : s[..(MaxContent - 1)] + "…";

    private void Enqueue(Post post)
    {
        lock (_gate)
        {
            // Oldest first: on a night where the network is down, the newest picture is the one
            // worth having when it comes back.
            while (_queue.Count >= MaxQueue) _queue.Dequeue();
            _queue.Enqueue(post);
        }
        try { _work.Release(); } catch (ObjectDisposedException) { }
    }

    // ───────────────────────────── the worker ─────────────────────────────

    private void Run()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PierCam (https://github.com/YoruDev-Ryland/PierCam)");
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                try { _work.Wait(_cts.Token); } catch (OperationCanceledException) { break; }

                Post? post;
                lock (_gate) post = _queue.Count > 0 ? _queue.Dequeue() : null;
                if (post is null) continue;

                try
                {
                    Send(http, post, _cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // Never the URL: App.Log writes to a file people paste into bug reports.
                    App.Log(ex, "Discord");
                    SetStatus(DiscordPhase.Failed, $"Couldn't post to Discord — {ex.Message}");
                }
            }
        }
        finally
        {
            http.Dispose();
        }
    }

    private void Send(HttpClient http, Post post, CancellationToken ct)
    {
        var url = S.WebhookUrl.Trim();
        if (!IsWebhookUrl(url)) return;

        var name = post.FileName;
        byte[]? bytes = null;
        if (post.Frame is { } frame) bytes = Jpeg(frame.Rgb, frame.Width, frame.Height);
        else if (post.FilePath is not null)
        {
            bytes = File.ReadAllBytes(post.FilePath);
            name = Path.GetFileName(post.FilePath);
        }

        var who = Sanitise(S.Username);
        SetStatus(DiscordPhase.Working, name is null ? "Posting…" : $"Posting {name}…");

        var result = Attempt(http, url, post.Content, name, bytes, who, ct);

        // Too large for this channel: re-encode a copy small enough, and say that is what it is.
        if (result == SendResult.TooLarge && post.MayShrink && post.FilePath is not null)
        {
            SetStatus(DiscordPhase.Working, "Too large for that channel — making a smaller copy…");
            var smaller = Shrink(post.FilePath, ct);
            if (smaller is not null)
            {
                try
                {
                    var note = Trim(post.Content + "\n*(reduced copy — the full-size video was over this channel's upload limit)*");
                    result = Attempt(http, url, note, Path.GetFileName(smaller), File.ReadAllBytes(smaller), who, ct);
                }
                finally
                {
                    TryDelete(smaller);
                }
            }
        }

        if (result == SendResult.TooLarge)
        {
            // Still no good: say so with the message alone, so the night is at least announced.
            var note = Trim(post.Content + $"\n*(the video was too large to upload here — it is in the library as {name})*");
            result = Attempt(http, url, note, null, null, who, ct);
        }

        SetStatus(result == SendResult.Ok ? DiscordPhase.Idle : DiscordPhase.Failed,
            result switch
            {
                SendResult.Ok => $"Last post {DateTime.Now:HH:mm}. Posting to {Mask(url)}.",
                SendResult.TooLarge => "Discord refused the upload as too large.",
                SendResult.Unauthorised => "Discord rejected the webhook — has it been deleted or the URL changed?",
                _ => "Discord could not be reached; it will try again with the next post."
            });
    }

    private enum SendResult { Ok, TooLarge, Unauthorised, Failed }

    /// <summary>One POST, honouring a 429 by waiting exactly as long as Discord asks.</summary>
    private static SendResult Attempt(HttpClient http, string url, string content, string? fileName, byte[]? bytes,
        string username, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var form = new MultipartFormDataContent();
            var payload = JsonSerializer.Serialize(new
            {
                content,
                username,
                // Nothing PierCam posts should ever ping anyone: these are pictures of the sky
                // arriving through the night, and a channel that pings @everyone at 3am is a
                // channel the webhook gets removed from.
                allowed_mentions = new { parse = Array.Empty<string>() }
            });
            form.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");

            if (bytes is not null && fileName is not null)
            {
                var file = new ByteArrayContent(bytes);
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ContentTypeFor(fileName));
                form.Add(file, "files[0]", fileName);
            }

            HttpResponseMessage resp;
            try
            {
                resp = http.PostAsync(url, form, ct).GetAwaiter().GetResult();
            }
            catch (HttpRequestException) { return SendResult.Failed; }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return SendResult.Failed; }

            using (resp)
            {
                if (resp.IsSuccessStatusCode) return SendResult.Ok;

                switch (resp.StatusCode)
                {
                    case HttpStatusCode.TooManyRequests:
                        // Discord states the wait in the body and the header; either will do.
                        var wait = resp.Headers.RetryAfter?.Delta
                                   ?? TimeSpan.FromSeconds(RetryAfterSeconds(resp) ?? 5);
                        if (wait > TimeSpan.FromMinutes(5)) return SendResult.Failed;
                        if (ct.WaitHandle.WaitOne(wait)) return SendResult.Failed;
                        continue;

                    case HttpStatusCode.RequestEntityTooLarge:
                        return SendResult.TooLarge;

                    case HttpStatusCode.Unauthorized:
                    case HttpStatusCode.Forbidden:
                    case HttpStatusCode.NotFound:
                        return SendResult.Unauthorised;

                    case HttpStatusCode.BadRequest:
                        // 40005 is "request entity too large" arriving as a 400 with a body.
                        return BodyMentionsTooLarge(resp) ? SendResult.TooLarge : SendResult.Failed;

                    default:
                        if ((int)resp.StatusCode < 500) return SendResult.Failed;
                        if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(3 * (attempt + 1)))) return SendResult.Failed;
                        continue;
                }
            }
        }
        return SendResult.Failed;
    }

    private static double? RetryAfterSeconds(HttpResponseMessage resp)
    {
        try
        {
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("retry_after", out var v) ? v.GetDouble() : null;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool BodyMentionsTooLarge(HttpResponseMessage resp)
    {
        try
        {
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return body.Contains("40005") || body.Contains("too large", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string Sanitise(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return "PierCam";
        // Discord rejects these outright in a webhook username.
        foreach (var bad in new[] { "discord", "@", "#", ":", "```" })
            n = n.Replace(bad, "", StringComparison.OrdinalIgnoreCase);
        n = n.Trim();
        return n.Length == 0 ? "PierCam" : n.Length > 80 ? n[..80] : n;
    }

    private static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".mp4" => "video/mp4",
        _ => "application/octet-stream"
    };

    // ───────────────────────────── media ─────────────────────────────

    private static byte[] Jpeg(byte[] rgb, int width, int height)
    {
        var source = System.Windows.Media.Imaging.BitmapSource.Create(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Rgb24, null, rgb, width * 3);
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 82 };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// A smaller copy of a finished video, made in the temp folder. The recording itself is never
    /// touched — this is a copy for one upload and is deleted immediately after.
    /// </summary>
    private string? Shrink(string source, CancellationToken ct)
    {
        if (!File.Exists(_ffmpegPath)) return null;
        var target = Path.Combine(Path.GetTempPath(), $"piercam-discord-{Guid.NewGuid():N}.mp4");
        try
        {
            // 960 wide at a softer quality: a night that was 90 MB lands in single figures, and
            // the point of the post is to see the night, not to archive it.
            var psi = new System.Diagnostics.ProcessStartInfo(_ffmpegPath,
                $"-hide_banner -nostdin -loglevel error -y -i \"{source}\" " +
                "-vf \"scale=960:-2:flags=lanczos\" -c:v libx264 -preset medium -crf 30 " +
                "-pix_fmt yuv420p -an -movflags +faststart \"" + target + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            // Below normal: this runs at the end of a night, when the next one may already be
            // getting ready.
            try { p.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch (Exception) { }
            p.StandardError.ReadToEnd();
            while (!p.WaitForExit(500))
            {
                if (!ct.IsCancellationRequested) continue;
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            return p.ExitCode == 0 && File.Exists(target) && new FileInfo(target).Length > 1024 ? target : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            App.Log(ex, "Discord shrink");
            TryDelete(target);
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void SetStatus(DiscordPhase phase, string status)
    {
        Phase = phase;
        Status = status;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _work.Release(); } catch (ObjectDisposedException) { }
        if (_worker is { IsAlive: true }) _worker.Join(TimeSpan.FromSeconds(3));
        _worker = null;
        _cts.Dispose();
        _work.Dispose();
    }
}
