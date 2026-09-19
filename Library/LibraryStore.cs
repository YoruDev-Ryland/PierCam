using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PierCam.Models;

namespace PierCam.Library;

/// <summary>One timelapse as the library grid sees it.</summary>
internal sealed class TimelapseItem : INotifyPropertyChanged
{
    private ImageSource? _poster;
    private ImageSource? _markedPoster;
    private bool _isSelected;
    private bool _showMarked;

    public TimelapseManifest Manifest { get; }

    public TimelapseItem(TimelapseManifest manifest)
    {
        Manifest = manifest;
        HasMarkedCopy = manifest.MarkedVideoPath is { } marked && File.Exists(marked);
    }

    /// <summary>The night was also recorded with the target marker burned in.</summary>
    public bool HasMarkedCopy { get; private set; }

    /// <summary>
    /// Which of the night's two videos the card shows and plays. Only meaningful with a marked
    /// copy; the card's dots switch it.
    /// </summary>
    public bool ShowMarked
    {
        get => _showMarked && HasMarkedCopy;
        set
        {
            if (_showMarked == value) return;
            _showMarked = value;
            Raise();
            Raise(nameof(Poster));
        }
    }

    /// <summary>The video the card's PLAY opens: the marked copy when that is the one showing.</summary>
    public string PlayPath => ShowMarked ? Manifest.MarkedVideoPath! : Manifest.VideoPath;

    /// <summary>Ticked in the library grid, for batch operations like downscaling.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Raise();
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>Raised on any item's selection change so the toolbar can update.</summary>
    public static event Action? SelectionChanged;

    public string Title => Manifest.Title;
    public string DateLine => Manifest.StartedLocal.ToString("ddd d MMM yyyy · HH:mm").ToUpperInvariant();

    /// <summary>Position in the library, shown as a plate number on the card.</summary>
    public string IndexLabel { get; internal set; } = string.Empty;
    public string FolderPath => Manifest.FolderPath;
    public string VideoPath => Manifest.VideoPath;
    public bool VideoExists => File.Exists(Manifest.VideoPath);

    public string Summary
    {
        get
        {
            var covered = Manifest.CapturedSpan;
            var video = Manifest.VideoDuration;
            return $"{Manifest.FrameCount:N0} frames · covers {Format.Duration(covered)} · plays in {Format.Duration(video)}";
        }
    }

    /// <summary>How long the video runs. All the compact card keeps of <see cref="Summary"/>.</summary>
    public string PlayLine => $"PLAYS IN {Format.Duration(Manifest.VideoDuration).ToUpperInvariant()}";

    public string TechLine
    {
        get
        {
            var res = Manifest.WasDownscaled
                ? $"{Manifest.Width}×{Manifest.Height} (from {Manifest.OriginalWidth}×{Manifest.OriginalHeight})"
                : $"{Manifest.Width}×{Manifest.Height}";
            return $"{Manifest.ExposureSeconds:0.##}s @ gain {Manifest.Gain} · {res} @ {Manifest.Fps}fps";
        }
    }

    public string SizeLine
    {
        get
        {
            var saved = Manifest.EstimatedRawBytes > 0 && Manifest.VideoBytes > 0
                ? $" · saved {Format.Bytes(Manifest.EstimatedRawBytes - Manifest.VideoBytes)} vs raw frames"
                : string.Empty;
            var marked = HasMarkedCopy ? $" + {Format.Bytes(Manifest.MarkedVideoBytes)} marked copy" : string.Empty;
            return Format.Bytes(Manifest.VideoBytes) + marked + saved;
        }
    }

    /// <summary>Refreshes the strings that a downscale pass changes.</summary>
    public void NotifyVideoChanged()
    {
        Raise(nameof(TechLine));
        Raise(nameof(SizeLine));
    }

    public string StatusLine => Manifest.Status switch
    {
        SessionStatus.Recording => "Recording…",
        SessionStatus.Interrupted => "Interrupted" + (Manifest.Message is null ? "" : $" — {Manifest.Message}"),
        SessionStatus.Failed => "Failed" + (Manifest.Message is null ? "" : $" — {Manifest.Message}"),
        _ => string.Empty
    };

    public bool HasStatus => Manifest.Status != SessionStatus.Complete;

    /// <summary>
    /// Poster art, decoded at thumbnail size and frozen. Decoding at 480px rather than 1920px
    /// keeps a library of a hundred nights to a few megabytes instead of hundreds, and freezing
    /// lets the file be deleted or replaced while the app is running.
    /// </summary>
    public ImageSource? Poster
    {
        get
        {
            // The marked copy's own poster when it is the one showing; the clean one otherwise,
            // or if the marked poster is missing.
            if (ShowMarked && Manifest.MarkedPosterPath is { } marked && (_markedPoster ??= LoadPoster(marked)) is { } m)
                return m;
            return _poster ??= LoadPoster(Manifest.PosterPath);
        }
    }

    private static ImageSource? LoadPoster(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 480;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }

    public void InvalidatePoster()
    {
        _poster = null;
        _markedPoster = null;
        Raise(nameof(Poster));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal static class Format
{
    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i <= 1 ? $"{v:0} {units[i]}" : $"{v:0.#} {units[i]}";
    }

    public static string Duration(TimeSpan t)
    {
        if (t.TotalSeconds < 1) return "0s";
        if (t.TotalMinutes < 1) return $"{t.TotalSeconds:0}s";
        if (t.TotalHours < 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{(int)t.TotalHours}h {t.Minutes}m";
    }
}

/// <summary>
/// The timelapse library.
///
/// There is no index file: the library is whatever session.json files exist under the root.
/// That means no database to get out of sync, and moving or deleting a folder in Explorer is a
/// perfectly valid way to manage the library.
/// </summary>
internal sealed class LibraryStore
{
    public ObservableCollection<TimelapseItem> Items { get; } = new();

    public long TotalVideoBytes { get; private set; }
    public long TotalEstimatedRawBytes { get; private set; }

    public void Refresh(string timelapseRoot)
    {
        var found = new List<TimelapseManifest>();
        try
        {
            if (Directory.Exists(timelapseRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(timelapseRoot))
                {
                    var m = TimelapseManifest.TryLoad(dir);
                    if (m is not null) found.Add(m);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        found.Sort((a, b) => b.StartedLocal.CompareTo(a.StartedLocal));

        // Reconcile in place so the grid keeps scroll position, cached posters and any ticked
        // selection across a refresh.
        //
        // Keyed by folder, not by session id. Copying a night's folder — to keep a full-size
        // original beside a downscaled one, say — is an ordinary thing to do, and it produces two
        // entries carrying the same id. A dictionary keyed on id refuses to build at all in that
        // case, so the whole library failed to load rather than simply listing both copies. The
        // folder is what actually identifies an entry: two of them are two timelapses, and each
        // keeps its own selection.
        var byFolder = new Dictionary<string, TimelapseItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items) byFolder[item.FolderPath] = item;

        Items.Clear();
        foreach (var m in found)
        {
            if (byFolder.TryGetValue(m.FolderPath, out var existing) && existing.Manifest.Status == m.Status)
            {
                Items.Add(existing);
            }
            else
            {
                var item = new TimelapseItem(m);
                if (byFolder.TryGetValue(m.FolderPath, out var prior)) item.IsSelected = prior.IsSelected;
                Items.Add(item);
            }
        }

        for (var i = 0; i < Items.Count; i++)
            Items[i].IndexLabel = (i + 1).ToString("D2") + " / " + Items.Count.ToString("D2");

        TotalVideoBytes = found.Sum(f => f.VideoBytes + (f.MarkedVideoFile is null ? 0 : f.MarkedVideoBytes));
        TotalEstimatedRawBytes = found.Sum(f => f.EstimatedRawBytes);
    }

    public bool Delete(TimelapseItem item, out string error)
    {
        error = string.Empty;
        try
        {
            if (Directory.Exists(item.FolderPath)) Directory.Delete(item.FolderPath, recursive: true);
            Items.Remove(item);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool Rename(TimelapseItem item, string newTitle, out string error)
    {
        error = string.Empty;
        try
        {
            item.Manifest.Title = newTitle;
            item.Manifest.Save(item.FolderPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }
}
