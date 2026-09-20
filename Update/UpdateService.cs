using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PierCam.Library;
using PierCam.Models;

namespace PierCam.Update;

internal enum UpdatePhase { Off, UpToDate, Checking, Available, Downloading, Ready, Failed }

/// <summary>One GitHub release, reduced to what an update needs.</summary>
internal sealed record ReleaseInfo(Version Version, string Tag, string PageUrl,
    string AssetName, string AssetUrl, long Bytes, string Sha256);

/// <summary>
/// Watches the repository's releases and, when asked, installs one.
///
/// Three rules shape this, and they all come from what PierCam is for. It runs unattended on an
/// observatory machine, often remotely, and a night it is recording cannot be interrupted:
///
///   · Checking never blocks anything and never shows a dialog. A machine with no route to
///     GitHub, or a rate-limited one, simply stays on the version it has.
///   · Nothing is ever installed while a session is recording, or within an hour of one that is
///     due. Installing restarts the app, and a restart mid-night is a hole in the video.
///   · The download is checked against the SHA-256 that the GitHub API publishes for the asset
///     before it is allowed to run. A truncated or tampered installer is deleted, not executed.
///
/// The installer is per-user, so this raises no UAC prompt on a machine driven by a standard
/// account - which is the normal case for a remote observatory.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private const string Owner = "YoruDev-Ryland", Repo = "PierCam";

    /// <summary>
    /// The releases endpoint. A property rather than a constant so the self-test can point it at
    /// a local stand-in; it is internal, so nothing outside PierCam can redirect where updates
    /// come from.
    /// </summary>
    internal static string LatestApi { get; set; } = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
    public const string ReleasesPage = $"https://github.com/{Owner}/{Repo}/releases/latest";

    /// <summary>The installer's AppId; its uninstall key is how an installed copy is recognised.</summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F3C21B4-9D7E-4A16-B0C5-2E6F1A4D8B73}_is1";

    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);
    private static readonly TimeSpan TickEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FirstCheckAfter = TimeSpan.FromSeconds(10);

    /// <summary>How close to a scheduled session counts as "not idle".</summary>
    private static readonly TimeSpan QuietBeforeSession = TimeSpan.FromHours(1);

    private readonly AppSettings _settings;
    private readonly Func<bool> _isRecording;
    private readonly Func<DateTime?> _nextSession;
    private readonly Action<Action> _onUi;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _timer;
    private bool _busy;
    private bool _checkedThisRun;

    private readonly Func<bool> _isInstalledCopy;

    public UpdateService(AppSettings settings, Func<bool> isRecording, Func<DateTime?> nextSession,
        Action<Action> onUi, Func<bool>? isInstalledCopy = null)
    {
        _settings = settings;
        _isRecording = isRecording;
        _nextSession = nextSession;
        _onUi = onUi;
        _isInstalledCopy = isInstalledCopy ?? (() => IsInstalledCopy);
    }

    /// <summary>Whether this copy came from the installer, and so can be updated in place.</summary>
    public bool IsInstalled => _isInstalledCopy();

    public UpdatePhase Phase { get; private set; } = UpdatePhase.Off;
    public string Headline { get; private set; } = "Not checked yet";
    public string Detail { get; private set; } = string.Empty;

    /// <summary>The release found, once there is one worth having.</summary>
    public ReleaseInfo? Available { get; private set; }

    /// <summary>The verified installer on disk, waiting to be run.</summary>
    public string? DownloadedInstaller { get; private set; }

    public event Action? StatusChanged;

    /// <summary>Raised when the app should shut down because the installer is taking over.</summary>
    public event Action? ExitRequested;

    public static Version CurrentVersion => Normalise(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0));

    /// <summary>
    /// A zip copy is not installed, so there is nothing for the installer to upgrade: running it
    /// would leave a second, separate copy in the user's programs folder. Those get the notice
    /// and a link instead.
    /// </summary>
    public static bool IsInstalledCopy
    {
        get
        {
            var here = AppContext.BaseDirectory.TrimEnd('\\');
            foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                try
                {
                    using var key = root.OpenSubKey(UninstallKey);
                    var where = key?.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(where) &&
                        string.Equals(where.TrimEnd('\\'), here, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // No read on the key is the same as not being able to prove it is installed.
                }
            }
            return false;
        }
    }

    public void Start()
    {
        _timer?.Dispose();
        if (!_settings.Updates.CheckAutomatically)
        {
            SetStatus(UpdatePhase.Off, "Automatic checking is off", $"You are on {CurrentVersion.ToString(2)}.");
            return;
        }
        SetStatus(UpdatePhase.UpToDate, $"PierCam {CurrentVersion.ToString(2)}", LastCheckedLine());
        _timer = new Timer(_ => Tick(), null, FirstCheckAfter, TickEvery);
    }

    /// <summary>Applies a change to the two switches without restarting anything needlessly.</summary>
    public void Apply() => Start();

    private async void Tick()
    {
        try
        {
            if (_cts.IsCancellationRequested) return;
            // Always once per run, then daily. A machine that is restarted often would otherwise
            // keep starting up with no idea an update exists: what was found is not remembered
            // across runs, only when it was last asked.
            var due = !_checkedThisRun || _settings.Updates.LastCheckUtc is not { } last || DateTime.UtcNow - last > CheckEvery;
            _checkedThisRun = true;
            if (_settings.Updates.CheckAutomatically && due && Available is null) await CheckAsync().ConfigureAwait(false);
            await AutoInstallIfIdle().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.Log(ex, "Update tick");
        }
    }

    /// <summary>Asks GitHub what the newest release is. Never throws; failure is a status line.</summary>
    public async Task CheckAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            SetStatus(UpdatePhase.Checking, "Checking for updates…", string.Empty);
            var release = await FetchLatest(_cts.Token).ConfigureAwait(false);
            _settings.Updates.LastCheckUtc = DateTime.UtcNow;
            _onUi(() => _settings.Save());

            if (release is null)
            {
                SetStatus(UpdatePhase.Failed, $"PierCam {CurrentVersion.ToString(2)}",
                    "Couldn't reach GitHub to check for updates. It will try again later.");
                return;
            }
            if (release.Version <= CurrentVersion)
            {
                Available = null;
                SetStatus(UpdatePhase.UpToDate, $"PierCam {CurrentVersion.ToString(2)} — up to date", LastCheckedLine());
                return;
            }

            Available = release;
            SetStatus(UpdatePhase.Available, $"PierCam {release.Version.ToString(2)} is available",
                _isInstalledCopy()
                    ? $"You are on {CurrentVersion.ToString(2)}. {Format.Bytes(release.Bytes)} download."
                    : $"You are on {CurrentVersion.ToString(2)}. This copy was unzipped rather than installed, so download it from the releases page.");
        }
        catch (Exception ex)
        {
            App.Log(ex, "Update check");
            SetStatus(UpdatePhase.Failed, $"PierCam {CurrentVersion.ToString(2)}", "The update check failed — see the log.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<ReleaseInfo?> FetchLatest(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub requires a user agent. It carries the version and nothing else: no machine name,
        // no account, nothing that identifies who is asking.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"PierCam/{CurrentVersion.ToString(2)}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        try
        {
            // /releases/latest is the newest *stable* release: the rolling pre-release that every
            // push to main replaces is not returned here, which is exactly what is wanted.
            using var resp = await http.GetAsync(LatestApi, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!TryParseTag(tag, out var version)) return null;
            var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase)) continue;
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                // No digest, no install: there would be no way to tell a good download from a bad
                // one, and this thing runs an executable at the end of it.
                if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
                return new ReleaseInfo(version, tag, page, name,
                    asset.GetProperty("browser_download_url").GetString() ?? "",
                    asset.GetProperty("size").GetInt64(), digest["sha256:".Length..]);
            }
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>"v1.2" or "1.2.3" to a comparable version.</summary>
    private static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0);
        var text = tag.TrimStart('v', 'V').Trim();
        if (!Version.TryParse(text, out var v)) return false;
        version = Normalise(v);
        return true;
    }

    /// <summary>
    /// Version.Parse treats an absent field as -1, so 1.2 would compare as older than 1.2.0.0.
    /// Everything is levelled to three fields before any comparison.
    /// </summary>
    private static Version Normalise(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private string LastCheckedLine() =>
        _settings.Updates.LastCheckUtc is { } t
            ? $"Last checked {Format.Duration(DateTime.UtcNow - t)} ago."
            : "Checking shortly.";

    // ───────────────────────────── downloading ─────────────────────────────

    /// <summary>Fetches the installer and checks it against the release's SHA-256.</summary>
    public async Task<bool> DownloadAsync()
    {
        if (Available is not { } release || !await _gate.WaitAsync(0).ConfigureAwait(false)) return false;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PierCam", "updates");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, release.AssetName);
            var part = target + ".part";

            // An installer left from a previous attempt is reused only if it still matches.
            if (File.Exists(target) && await Sha256(target).ConfigureAwait(false) == release.Sha256.ToLowerInvariant())
            {
                DownloadedInstaller = target;
                SetStatus(UpdatePhase.Ready, $"PierCam {release.Version.ToString(2)} is ready to install", InstallWhenLine());
                return true;
            }

            SetStatus(UpdatePhase.Downloading, $"Downloading PierCam {release.Version.ToString(2)}", "0%");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"PierCam/{CurrentVersion.ToString(2)}");

            using (var resp = await http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, _cts.Token).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? release.Bytes;
                await using var source = await resp.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false);
                await using var file = File.Create(part);
                var buffer = new byte[128 * 1024];
                long done = 0;
                var lastReport = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, _cts.Token).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), _cts.Token).ConfigureAwait(false);
                    done += read;
                    var percent = total > 0 ? (int)(done * 100 / total) : 0;
                    if (percent != lastReport)
                    {
                        lastReport = percent;
                        SetStatus(UpdatePhase.Downloading, $"Downloading PierCam {release.Version.ToString(2)}",
                            $"{percent}% of {Format.Bytes(total)}");
                    }
                }
            }

            if (await Sha256(part).ConfigureAwait(false) != release.Sha256.ToLowerInvariant())
            {
                TryDelete(part);
                SetStatus(UpdatePhase.Failed, "That download didn't arrive intact",
                    "The installer did not match the checksum GitHub published for it, so it was deleted. Nothing was installed.");
                return false;
            }

            TryDelete(target);
            File.Move(part, target);
            DownloadedInstaller = target;
            SetStatus(UpdatePhase.Ready, $"PierCam {release.Version.ToString(2)} is ready to install", InstallWhenLine());
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex, "Update download");
            SetStatus(UpdatePhase.Failed, "The download failed", "It will be tried again later, or download it from the releases page.");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string> Sha256(string path)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ───────────────────────────── installing ─────────────────────────────

    /// <summary>Whether an install could start this second, and if not, why not.</summary>
    public bool CanInstallNow(out string why)
    {
        why = string.Empty;
        if (!_isInstalledCopy())
        {
            why = "This copy was unzipped rather than installed, so it updates by downloading the new zip.";
            return false;
        }
        if (_isRecording())
        {
            why = "A night is recording. Installing restarts PierCam, so it waits until the session is finished.";
            return false;
        }
        if (_nextSession() is { } next && next - DateTime.Now < QuietBeforeSession && next > DateTime.Now)
        {
            why = $"A session is due at {next:HH:mm}. Installing now would risk the start of the night.";
            return false;
        }
        return true;
    }

    private string InstallWhenLine() =>
        _settings.Updates.AutoInstallWhenIdle
            ? CanInstallNow(out var why) ? "Installing now." : why
            : "Press Install when it suits you. PierCam will restart.";

    /// <summary>
    /// Runs the installer and hands the app over to it.
    ///
    /// The installer replaces this executable, so it cannot be the thing that starts PierCam up
    /// again. A detached shell waits for it to finish and then launches whatever is there - which
    /// is the new version after a good install, and the old one if the install failed. Either way
    /// the camera comes back, which on an unattended machine is the part that matters.
    /// </summary>
    public bool InstallAndRestart()
    {
        if (DownloadedInstaller is not { } installer || !File.Exists(installer)) return false;
        if (!CanInstallNow(out _)) return false;

        var exe = Path.Combine(AppContext.BaseDirectory, "PierCam.exe");
        try
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                // /VERYSILENT: no windows at all. CloseApplications in the installer shuts this
                // process down if it is somehow still up by then; /NORESTART keeps it from ever
                // rebooting an observatory machine on its own.
                Arguments = $"/c \"\"{installer}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART && start \"\" \"{exe}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            App.Log(ex, "Update install");
            SetStatus(UpdatePhase.Failed, "Couldn't start the installer", "Try the downloaded installer by hand, or the releases page.");
            return false;
        }

        SetStatus(UpdatePhase.Ready, "Installing…", "PierCam will close and reopen on the new version.");
        _onUi(() => ExitRequested?.Invoke());
        return true;
    }

    /// <summary>The unattended path: only ever fires when nothing is recording or about to.</summary>
    private async Task AutoInstallIfIdle()
    {
        if (!_settings.Updates.AutoInstallWhenIdle || Available is null || _busy) return;
        if (!_isInstalledCopy() || !CanInstallNow(out _)) return;
        _busy = true;
        try
        {
            if (DownloadedInstaller is null && !await DownloadAsync().ConfigureAwait(false)) return;
            // Between downloading and installing, a session may have started.
            if (!CanInstallNow(out _)) return;
            _onUi(() => InstallAndRestart());
        }
        finally
        {
            _busy = false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void SetStatus(UpdatePhase phase, string headline, string detail)
    {
        Phase = phase;
        Headline = headline;
        Detail = detail;
        _onUi(() => StatusChanged?.Invoke());
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
        _cts.Dispose();
        _gate.Dispose();
    }
}
