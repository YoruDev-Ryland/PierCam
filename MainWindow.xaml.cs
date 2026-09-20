using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PierCam.Camera;
using PierCam.Capture;
using PierCam.Imaging;
using PierCam.Library;
using PierCam.Models;
using PierCam.Ui;
using PierCam.Ui.Controls;
using PierCam.Video;

namespace PierCam;

/// <summary>A labelled value for a ComboBox. ToString is the display text, so no DisplayMemberPath.</summary>
internal sealed record Option<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly CaptureEngine _engine;
    private readonly Sky.TargetMarkerService _marker;
    private readonly LibraryStore _library = new();
    private readonly NightlyScheduler _scheduler;
    private readonly Update.UpdateService _updates;
    private readonly RoofMonitor _roof = new();
    private readonly Process _self = Process.GetCurrentProcess();
    private readonly DateTime _startedAt = DateTime.Now;

    /// <summary>
    /// True when this launch came from the Run entry rather than someone double-clicking it.
    /// Start-minimised only applies to the former: opening PierCam yourself and having it
    /// disappear to the taskbar would be obnoxious.
    /// </summary>
    private static bool StartedByWindows =>
        Environment.GetCommandLineArgs().Any(a =>
            string.Equals(a, Ui.Startup.MinimisedSwitch, StringComparison.OrdinalIgnoreCase));

    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _scheduleTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private WriteableBitmap? _bitmap;
    private long _renderedSequence = -1;
    private bool _loading = true;
    private bool _seeking;
    private List<CameraDescriptor> _cameras = new();


    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _engine = new CaptureEngine(_settings);
        _marker = new Sky.TargetMarkerService(_settings,
            () => (_engine.CameraSerial, _engine.Width, _engine.Height),
            a => Dispatcher.BeginInvoke(a, DispatcherPriority.Background));
        _marker.StatusChanged += () => Dispatcher.BeginInvoke((Action)UpdateMarkerUi, DispatcherPriority.Background);
        _engine.Marker = _marker;
        _scheduler = new NightlyScheduler(_settings, () => _roof.Status);
        _updates = new Update.UpdateService(_settings, () => _engine.IsRecording,
            () => _scheduler.NextStart(DateTime.Now),
            a => Dispatcher.BeginInvoke(a, DispatcherPriority.Background));
        _updates.StatusChanged += UpdateUpdatesUi;
        _updates.ExitRequested += Close;
        _engine.SessionFinished += OnSessionFinished;
        ApplyRoofSettings();

        // First run: lift the observing site from NINA if it is installed, so dusk-to-dawn
        // scheduling works without anyone typing coordinates. Silent — if NINA is not here,
        // the fields simply stay blank.
        if (!_settings.Site.IsSet) TryImportSiteFromNina(out _);

        ThemeManager.Apply(_settings.ThemeId);
        BuildOptionLists();
        BuildThemeChips();
        WireSliders();
        LoadSettingsIntoUi();
        _loading = false;

        GridList.ItemsSource = _library.Items;
        CarouselList.ItemsSource = _library.Items;
        TimelapseItem.SelectionChanged += OnSelectionChanged;

        RefreshLibrary();
        UpdatePlan();
        UpdateDiagnostics();
        ApplyLibraryView();

        _renderTimer.Tick += OnRenderTick;
        _statusTimer.Tick += OnStatusTick;
        _scheduleTimer.Tick += OnScheduleTick;

        // Placement before the window is shown, or it flashes at the default position first.
        WindowPlacementService.Restore(this, _settings.Placement,
            _settings.Startup.StartMinimised && StartedByWindows);

        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
        PreviewKeyDown += OnWindowKeyDown;
        StateChanged += OnWindowStateChanged;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        SupportLink.Text = SupportLabel;

        WireMenu(NavMenuButton, NavPopup, NavPopupPlate);
        WireMenu(CameraMenuButton, CameraPopup, CameraPopupPlate);
        // The camera menu hangs off the right end of its plate, since that plate sits at the
        // right of the window and a left-aligned drop-down would run off the screen.
        CameraPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) => new[]
        {
            new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, targetSize.Height + 6),
                PopupPrimaryAxis.Horizontal)
        };

        SyncCaptionHeight();
        FitHeader();
        FitDiagnostics();
        FitStatusBar();
        HeaderBar.SizeChanged += (_, _) => SyncCaptionHeight();
        // Not StatusMiddle's own SizeChanged: it is sized by the text it contains, so that would
        // be a feedback loop. The plate's width is set by the window, and UpdateStatusBar calls
        // in as well, since the right-hand readout changes width as the numbers do.
        StatusPlate.SizeChanged += (_, _) => FitStatusBar();
        SizeChanged += (_, _) => { ApplyMaximisedPadding(); FitHeader(); FitDiagnostics(); FitStatusBar(); };
        LibraryStage.SizeChanged += (_, _) => SyncPlayerHeightToStage();
        OnWindowStateChanged(this, EventArgs.Empty);

        NavLive.IsChecked = true;
        RescanCameras(autoConnect: true);

        // Target marker: does nothing at all unless switched on.
        UpdateMarkerColor();
        _marker.Apply();
        UpdateMarkerUi();

        // The first update check is ten seconds out, so it can never be in the way of the camera
        // coming up.
        _updates.Start();
        UpdateUpdatesUi();

        _renderTimer.Start();
        _statusTimer.Start();
        _scheduleTimer.Start();
        UpdateStatusBar();

        // After the window is up and the camera has been claimed, so a long sweep never delays
        // either. It checks for itself whether one is due.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, RunHousekeepingIfDue);

        // The header drops in once; the live page itself is dealt in by StaggerLive, which the
        // NavLive check above has already triggered through OnNavChanged.
        if (_settings.Animations) Motion.Enter(HeaderBar, 0, -12);
    }

    /// <summary>
    /// Same treatment as the config plates: the viewport lands first, then the control column
    /// settles plate by plate from the top, so the assembly is seen coming together every time
    /// the tab is opened rather than only on the first launch.
    /// </summary>
    private void StaggerLive()
    {
        if (!_settings.Animations) return;

        Motion.Enter(ViewportPanel, 0, fromY: 0, duration: Motion.Slow);

        var delay = 80;
        foreach (var plate in new UIElement[] { CardCapture, CardSchedule, CardExposure, CardImage })
        {
            Motion.Enter(plate, delay, fromY: 16, duration: Motion.Slow);
            delay += 62;
        }
    }

    // ══════════════════════ setup ══════════════════════

    private void BuildOptionLists()
    {
        IntervalCombo.ItemsSource = new[]
        {
            new Option<int>("CONTINUOUS — BACK TO BACK", 0),
            new Option<int>("15 SECONDS", 15),
            new Option<int>("20 SECONDS", 20),
            new Option<int>("30 SECONDS", 30),
            new Option<int>("1 MINUTE", 60),
            new Option<int>("2 MINUTES", 120),
            new Option<int>("4 MINUTES", 240),
            new Option<int>("5 MINUTES", 300),
            new Option<int>("10 MINUTES", 600),
        };

        FpsCombo.ItemsSource = new[]
        {
            new Option<int>("24 FPS", 24), new Option<int>("30 FPS", 30), new Option<int>("60 FPS", 60),
        };

        QualityCombo.ItemsSource = new[]
        {
            new Option<int>("ARCHIVAL — VERY LARGE", 20),
            new Option<int>("HIGH", 23),
            new Option<int>("BALANCED — RECOMMENDED", 26),
            new Option<int>("SMALL", 28),
            new Option<int>("TINY", 31),
        };

        DenoiseCombo.ItemsSource = new[]
        {
            new Option<DenoiseLevel>("OFF", DenoiseLevel.Off),
            new Option<DenoiseLevel>("LIGHT", DenoiseLevel.Light),
            new Option<DenoiseLevel>("MEDIUM — RECOMMENDED", DenoiseLevel.Medium),
            new Option<DenoiseLevel>("STRONG", DenoiseLevel.Strong),
        };

        ResolutionCombo.ItemsSource = new[]
        {
            new Option<(int W, int H)>("1920 × 1080", (1920, 1080)),
            new Option<(int W, int H)>("1280 × 720", (1280, 720)),
            new Option<(int W, int H)>("960 × 540", (960, 540)),
        };

        PresetCombo.ItemsSource = new[]
        {
            new Option<string>("VERY FAST", "veryfast"),
            new Option<string>("FAST", "fast"),
            new Option<string>("MEDIUM", "medium"),
            new Option<string>("SLOW — SMALLEST", "slow"),
        };

        StretchModeCombo.ItemsSource = new[]
        {
            new Option<StretchMode>("SMOOTHED — BEST FOR TIMELAPSE", StretchMode.Smoothed),
            new Option<StretchMode>("AUTO — EVERY FRAME", StretchMode.Auto),
            new Option<StretchMode>("MANUAL", StretchMode.Manual),
        };

        TwilightCombo.ItemsSource = new[]
        {
            new Option<TwilightKind>("ASTRONOMICAL −18° — FULL DARK", TwilightKind.Astronomical),
            new Option<TwilightKind>("NAUTICAL −12°", TwilightKind.Nautical),
            new Option<TwilightKind>("CIVIL −6°", TwilightKind.Civil),
            new Option<TwilightKind>("SUNSET TO SUNRISE", TwilightKind.Sunset),
        };

        DownscaleCombo.ItemsSource = VideoTranscoder.Targets;
        AutoDownscaleTargetCombo.ItemsSource = VideoTranscoder.Targets;

        SpeedCombo.ItemsSource = new[]
        {
            new Option<double>("0.25×", 0.25), new Option<double>("0.5×", 0.5),
            new Option<double>("1×", 1.0), new Option<double>("2×", 2.0), new Option<double>("4×", 4.0),
        };

        ThemeCombo.ItemsSource = ThemeManager.Themes
            .Select(t => new Option<string>(t.Name.ToUpperInvariant(), t.Id)).ToList();
    }

    private readonly List<RadioButton> _themeChips = new();
    private bool _syncingTheme;

    /// <summary>Builds the palette picker from ThemeManager, so a new theme appears with no UI edits.</summary>
    private void BuildThemeChips()
    {
        var chips = _themeChips;
        chips.Clear();
        foreach (var theme in ThemeManager.Themes)
        {
            var name = new TextBlock { Text = theme.Name.ToUpperInvariant(), FontWeight = FontWeights.Bold, FontSize = 11 };
            var blurb = new TextBlock
            {
                Text = theme.Blurb,
                FontSize = 10,
                Margin = new Thickness(0, 4, 0, 8),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            };

            // Live swatches, read straight out of that theme's own dictionary.
            var swatches = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var brush in PeekSwatches(theme))
            {
                swatches.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = 22, Height = 10, Fill = brush, Margin = new Thickness(0, 0, 3, 0),
                    Stroke = ThemeManager.Brush("HairlineSoft"), StrokeThickness = 1
                });
            }

            var stack = new StackPanel();
            stack.Children.Add(name);
            stack.Children.Add(blurb);
            stack.Children.Add(swatches);

            var chip = new RadioButton
            {
                GroupName = "theme",
                Style = (Style)FindResource("ThemeChip"),
                Content = stack,
                Tag = theme.Id,
                IsChecked = string.Equals(theme.Id, _settings.ThemeId, StringComparison.OrdinalIgnoreCase)
            };
            chip.Checked += OnThemeChipChecked;
            chips.Add(chip);
        }
        ThemeChips.ItemsSource = chips;
    }

    /// <summary>Loads a theme dictionary off to the side purely to sample its colours.</summary>
    private static IEnumerable<Brush> PeekSwatches(ThemeDefinition theme)
    {
        var keys = new[] { "Bg", "Panel", "Text", "Accent", "Signal" };
        ResourceDictionary dict;
        try { dict = new ResourceDictionary { Source = new Uri(theme.Source, UriKind.Relative) }; }
        catch (Exception) { yield break; }

        foreach (var key in keys)
            if (dict[key] is Brush b)
                yield return b;
    }

    private void LoadSettingsIntoUi()
    {
        var c = _settings.Camera;
        var v = _settings.Video;
        var s = _settings.Session;
        var st = _settings.Stretch;

        ExposureSlider.Value = Math.Clamp(c.ExposureSeconds, ExposureSlider.Minimum, ExposureSlider.Maximum);
        GainSlider.Value = Math.Clamp(c.Gain, GainSlider.Minimum, GainSlider.Maximum);
        PreviewExposureSlider.Value = Math.Clamp(c.PreviewExposureSeconds,
            PreviewExposureSlider.Minimum, PreviewExposureSlider.Maximum);
        PreviewGainSlider.Value = Math.Clamp(c.PreviewGain,
            PreviewGainSlider.Minimum, PreviewGainSlider.Maximum);
        TargetBgSlider.Value = Math.Clamp(st.TargetBackground, TargetBgSlider.Minimum, TargetBgSlider.Maximum);

        SelectOption(IntervalCombo, s.IntervalSeconds);
        SelectOption(FpsCombo, v.Fps);
        SelectOption(QualityCombo, v.Crf);
        SelectOption(DenoiseCombo, v.Denoise);
        SelectOption(ResolutionCombo, (v.OutputWidth, v.OutputHeight));
        SelectOption(PresetCombo, v.Preset);
        SelectOption(StretchModeCombo, st.Mode);
        SelectOption(SpeedCombo, 1.0);
        SelectOption(ThemeCombo, ThemeManager.Resolve(_settings.ThemeId).Id);

        DownscaleCombo.SelectedItem =
            Array.Find(VideoTranscoder.Targets, t => t.Width == v.DownscaleWidth && t.Height == v.DownscaleHeight)
            ?? VideoTranscoder.Targets[1];

        NeutraliseCheck.IsChecked = st.NeutraliseBackground;
        TimestampCheck.IsChecked = v.BurnTimestamp;
        KeepRawCheck.IsChecked = s.KeepRawFrames;
        AutoExposureCheck.IsChecked = c.AutoExposure;
        PreviewAutoCheck.IsChecked = c.PreviewAutoExposure;
        AnimationsCheck.IsChecked = _settings.Animations;
        ChromeCheck.IsChecked = _settings.ViewportChrome;

        ScheduleManualRadio.IsChecked = s.Schedule == ScheduleMode.Manual;
        ScheduleNightlyRadio.IsChecked = s.Schedule == ScheduleMode.Nightly;
        ScheduleAstroRadio.IsChecked = s.Schedule == ScheduleMode.Astronomical;
        StartTimeBox.Text = FormatTime(s.StartTime);
        EndTimeBox.Text = FormatTime(s.EndTime);
        SelectOption(TwilightCombo, s.Twilight);

        RoofGateCheck.IsChecked = s.RequireRoofOpen;
        RoofUnknownCheck.IsChecked = s.RecordWhenRoofUnknown;
        RoofPathBox.Text = s.RoofStatusPath;
        RoofPollBox.Text = s.RoofPollSeconds.ToString(CultureInfo.InvariantCulture);
        RoofStaleBox.Text = s.RoofAbandonMinutes.ToString(CultureInfo.InvariantCulture);
        RoofLingerBox.Text = s.RoofLingerMinutes.ToString(CultureInfo.InvariantCulture);
        LoadSiteIntoUi();

        ViewCarouselRadio.IsChecked = _settings.LibraryView == Models.LibraryView.Carousel;
        ViewGridRadio.IsChecked = _settings.LibraryView == Models.LibraryView.Grid;

        LibraryRootBox.Text = _settings.LibraryRoot;
        MinFreeDiskBox.Text = s.MinFreeDiskGb.ToString("0.#", CultureInfo.InvariantCulture);
        FfmpegPathBox.Text = _settings.FfmpegPath ?? string.Empty;
        SessionTitleBox.Text = DateTime.Now.ToString("dddd d MMMM yyyy");

        LoadAdvancedIntoUi();
        LoadStartupIntoUi();
        LoadHousekeepingIntoUi();
        LoadMarkerIntoUi();
        LoadUpdatesIntoUi();
        UpdateSliderLabels();
        UpdateScheduleUi();
        UpdateAutoExposureHint();
        UpdatePreviewAutoUi();
        UpdateFfmpegStatus();
        UpdateViewportChrome();
    }

    private static void SelectOption<T>(Selector combo, T value)
    {
        if (combo.ItemsSource is null) return;
        foreach (var item in combo.ItemsSource.OfType<Option<T>>())
        {
            if (EqualityComparer<T>.Default.Equals(item.Value, value)) { combo.SelectedItem = item; return; }
        }
        combo.SelectedIndex = 0;
    }

    private static T Selected<T>(Selector combo, T fallback) =>
        combo.SelectedItem is Option<T> o ? o.Value : fallback;

    private static string FormatTime(TimeSpan t) => $"{t.Hours:00}:{t.Minutes:00}";

    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    // ══════════════════════ navigation ══════════════════════

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton rb || rb.Tag is not string tag) return;
        var index = int.Parse(tag, CultureInfo.InvariantCulture);

        // The collapsed header's menu button names the open page; picking a page closes the menu.
        NavMenuLabel.Text = $"{Ui.Controls.Nav.GetCode(rb)} {rb.Content}";
        NavPopup.IsOpen = false;

        var pages = new UIElement[] { LivePage, LibraryPage, ConfigPage };
        for (var i = 0; i < pages.Length; i++)
        {
            if (i == index)
            {
                pages[i].Visibility = Visibility.Visible;
                if (_settings.Animations) Motion.Enter(pages[i], 0, 10);
                else pages[i].Opacity = 1;
            }
            else
            {
                pages[i].Visibility = Visibility.Collapsed;
            }
        }

        if (index == 0) StaggerLive();
        if (index == 1) RefreshLibrary();
        if (index == 2) { UpdateDiagnostics(); StaggerPlates(); }
        if (index != 1) ClosePlayer();

        if (LayoutCheckEnabled) Dispatcher.BeginInvoke(RunLayoutCheck, DispatcherPriority.ContextIdle);
    }

    private static readonly bool LayoutCheckEnabled =
        Environment.GetEnvironmentVariable("PIERCAM_LAYOUTCHECK") == "1";

    /// <summary>
    /// Deals the config plates in one after another, so the page reads as an assembly coming
    /// together rather than a screenshot appearing. Walks the visual tree instead of naming
    /// every plate, so adding a section to the XAML needs no code change here.
    /// </summary>
    private void StaggerPlates()
    {
        if (ConfigPage.Content is not Grid grid) return;

        if (_settings.Animations)
        {
            // Unhurried enough to read as plates being laid down one at a time: seven of them
            // at 62ms apart finishes in a little under a second.
            var delay = 0;
            foreach (var child in grid.Children)
            {
                if (child is not ChamferPanel plate) continue;
                Motion.Enter(plate, delay, fromY: 16, duration: Motion.Slow);
                delay += 62;
            }
        }

    }

    /// <summary>
    /// Mechanical proof that nothing in a plate assembly hangs over a plate's cut edges. Every
    /// leaf element is tested at all four corners against its plate's actual outline geometry
    /// and the offenders are written to %AppData%\PierCam\layout-check-{page}.txt. Runs for
    /// whichever assemblies are on screen, and only when PIERCAM_LAYOUTCHECK=1, so it costs
    /// nothing in normal use.
    /// </summary>
    private void RunLayoutCheck()
    {
        if (ConfigPage.IsVisible && ConfigPage.Content is Panel config) RunLayoutCheck("config", config);
        if (LivePage.IsVisible) RunLayoutCheck("live", LiveStack);
        RunLayoutCheck("header", HeaderNav);
    }

    private void RunLayoutCheck(string page, Panel root)
    {
        var report = new System.Text.StringBuilder();
        var decalNamespace = typeof(ChamferPanel).Namespace;
        int total = 0, bad = 0;

        foreach (var child in root.Children)
        {
            if (child is not ChamferPanel plate) continue;
            var outline = plate.Outline;
            report.AppendLine($"[{plate.Name}] {plate.ActualWidth:0}x{plate.ActualHeight:0}");

            foreach (var el in Descendants(plate))
            {
                if (el is Panel or ScrollViewer or HeaderedContentControl) continue;
                if (el.Visibility != Visibility.Visible || el.ActualWidth <= 0 || el.ActualHeight <= 0) continue;
                var isLeaf = el is TextBlock or Control or System.Windows.Shapes.Shape
                             || el.GetType().Namespace == decalNamespace;
                if (!isLeaf) continue;
                if (Ui.Controls.LayoutCheck.GetIgnore(el)) continue;

                Rect r;
                try
                {
                    r = el.TransformToAncestor(plate)
                          .TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
                }
                catch (InvalidOperationException) { continue; }

                total++;
                var corners = new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft };
                var outside = corners.Where(p => !outline.FillContains(p)).ToList();
                if (outside.Count == 0) continue;
                bad++;
                report.AppendLine($"  OUT {Describe(el)} rect=({r.X:0},{r.Y:0} {r.Width:0}x{r.Height:0}) " +
                                  $"corners={string.Join(" ", outside.Select(p => $"({p.X:0},{p.Y:0})"))}");
            }
        }

        report.Insert(0, $"layout-check {page} {DateTime.Now:HH:mm:ss}  window={ActualWidth:0}x{ActualHeight:0}  " +
                         $"elements={total} outside={bad}\n");
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PierCam");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"layout-check-{page}.txt"), report.ToString());
        }
        catch (Exception ex) { App.Log(ex, "LayoutCheck"); }

        static string Describe(FrameworkElement el)
        {
            var text = el switch
            {
                TextBlock tb => tb.Text,
                Button b => b.Content?.ToString(),
                CheckBox cb => cb.Content?.ToString(),
                _ => null
            };
            if (text is { Length: > 28 }) text = text[..28] + "…";
            var name = string.IsNullOrEmpty(el.Name) ? "" : el.Name + " ";
            return $"{el.GetType().Name} {name}{(text is null ? "" : "\"" + text + "\"")}".TrimEnd();
        }
    }

    /// <summary>
    /// Walks the visual tree, not the logical one. Control templates put real marks on screen —
    /// the index tag and rule in a plate heading are template children — and a logical walk does
    /// not see any of them, which is exactly how a tag clipped by a corner cut got past this
    /// check once already.
    /// </summary>
    private static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe) yield return fe;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (LibraryPage.Visibility != Visibility.Visible) return;
        if (_settings.LibraryView != Models.LibraryView.Carousel) return;
        if (e.OriginalSource is TextBox) return;

        if (e.Key == Key.Left) { StepCarousel(-1); e.Handled = true; }
        else if (e.Key == Key.Right) { StepCarousel(1); e.Handled = true; }
    }

    // ══════════════════════ header collapse ══════════════════════
    //
    // The header holds the wordmark, three tabs, a clock strip, the camera row and the window
    // buttons: about 1450px laid out in full. Somebody driving the scope over a 480p remote
    // session needs the window to be half that, so the header gives things up in stages as it
    // runs short of room rather than clipping. Nothing is lost — the tabs and the camera row
    // move into drop-down menus and come back when there is space again.
    //
    // The decision is measured, not guessed: after each stage the header is laid out and its
    // children's desired widths are summed against the width it actually has. Widths for each
    // stage are remembered as they are passed through, so growing again can step back up a
    // stage only when the fuller layout is known to fit, with a little slack against flicker.

    private const int HeaderStages = 4;
    private int _headerStage;
    private bool _fittingHeader;
    private readonly double[] _headerNeed = new double[HeaderStages + 1];

    private void FitHeader()
    {
        if (!IsLoaded || _fittingHeader) return;

        // Not HeaderBar.ActualWidth: when content overflows its slot WPF arranges the element at
        // its full desired width and clips it, so that number never admits to being short of
        // room. The window's width is the one measurement that cannot stretch to fit.
        var available = ActualWidth
                        - RootGrid.Margin.Left - RootGrid.Margin.Right
                        - HeaderBar.Margin.Left - HeaderBar.Margin.Right;
        if (available <= 0) return;

        _fittingHeader = true;
        try
        {
            for (var guard = 0; guard < HeaderStages * 2; guard++)
            {
                HeaderBar.UpdateLayout();
                var need = 0.0;
                foreach (UIElement child in HeaderBar.Children) need += child.DesiredSize.Width;
                _headerNeed[_headerStage] = need;

                if (need > available && _headerStage < HeaderStages)
                {
                    ApplyHeaderStage(_headerStage + 1);
                    continue;
                }
                if (_headerStage > 0 && _headerNeed[_headerStage - 1] + 24 <= available)
                {
                    ApplyHeaderStage(_headerStage - 1);
                    continue;
                }
                break;
            }
        }
        finally
        {
            _fittingHeader = false;
        }
    }

    /// <summary>
    /// Stage 0 is the full header. Each stage above it gives up one more thing:
    /// 1 the clock strip; 2 the camera row, behind a CAMERA menu; 3 the tabs, behind a menu
    /// naming the open page, and the wordmark's sub-line; 4 the wordmark's text, leaving the tag.
    /// </summary>
    private void ApplyHeaderStage(int stage)
    {
        _headerStage = stage;

        HeaderCentre.Visibility = stage >= 1 ? Visibility.Collapsed : Visibility.Visible;

        var cameraCompact = stage >= 2;
        if (!cameraCompact) CameraPopup.IsOpen = false;
        MoveTo(CameraRow, cameraCompact ? CameraPopupHost : CameraHost);
        CameraMenuButton.Visibility = cameraCompact ? Visibility.Visible : Visibility.Collapsed;

        var navCompact = stage >= 3;
        if (!navCompact) NavPopup.IsOpen = false;
        NavTabs.Orientation = navCompact ? Orientation.Vertical : Orientation.Horizontal;
        MoveTo(NavTabs, navCompact ? NavPopupHost : NavHost);
        NavMenuButton.Visibility = navCompact ? Visibility.Visible : Visibility.Collapsed;
        WordmarkSub.Visibility = navCompact ? Visibility.Collapsed : Visibility.Visible;

        var tagOnly = stage >= 4;
        WordmarkText.Visibility = tagOnly ? Visibility.Collapsed : Visibility.Visible;
        WordmarkTag.Margin = tagOnly ? new Thickness(0) : new Thickness(0, 0, 11, 0);

        static void MoveTo(FrameworkElement element, Panel host)
        {
            if (ReferenceEquals(element.Parent, host)) return;
            if (element.Parent is Panel current) current.Children.Remove(element);
            host.Children.Add(element);
        }
    }

    /// <summary>
    /// Caps the status text so it ellipsises instead of shouldering the tip-jar link off the bar.
    ///
    /// The link sits beside the last reading rather than at a fixed point on the bar, which costs
    /// an Auto column, and an Auto column measures its child unbounded — so the cap has to be
    /// applied by hand. It has to be measured against things that do not themselves depend on the
    /// status text, or it is circular: StatusMiddle is sized *by* the text, so capping the text to
    /// StatusMiddle-minus-the-link is a no-op that agrees with itself at any width. The plate, the
    /// state pill and the right-hand readout are all independent of it.
    /// </summary>
    private void FitStatusBar()
    {
        if (!IsLoaded || StatusPlate.ActualWidth <= 0) return;

        var inner = StatusPlate.ActualWidth - StatusPlate.Padding.Left - StatusPlate.Padding.Right;
        var room = inner
                   - StatusState.ActualWidth
                   - StatusRight.ActualWidth
                   - StatusKofi.ActualWidth - StatusKofi.Margin.Left
                   - StatusGap;
        room = Math.Max(0, room);

        // Guard the assignment: it feeds back into the measure that raised the event.
        if (Math.Abs(StatusLeft.MaxWidth - room) < 0.5) return;
        StatusLeft.MaxWidth = room;
    }

    /// <summary>Clear space kept between the tip-jar link and the right-hand readout.</summary>
    private const double StatusGap = 24;

    /// <summary>
    /// A toggle that opens a light-dismiss popup. The one wrinkle: with StaysOpen off, a click
    /// on the toggle while its menu is open closes the popup on mouse-down and then re-opens it
    /// on the click. Making the toggle untouchable while the menu is open means that click only
    /// ever closes.
    /// </summary>
    private void WireMenu(ToggleButton button, Popup popup, UIElement plate)
    {
        button.Checked += (_, _) => popup.IsOpen = true;
        button.Unchecked += (_, _) => popup.IsOpen = false;
        popup.Opened += (_, _) =>
        {
            button.IsHitTestVisible = false;
            if (_settings.Animations) Motion.Enter(plate, 0, -6);
        };
        popup.Closed += (_, _) =>
        {
            button.IsChecked = false;
            button.IsHitTestVisible = true;
        };
    }

    /// <summary>
    /// The diagnostics plate's two arrangements. Below about 1100px of window there is no
    /// longer room for facts, paths and buttons side by side, so the paths drop under the facts
    /// and the buttons under both. Keyed off the window width for the same reason FitHeader is:
    /// an overflowing plate reports the width it wants, not the width it has.
    /// </summary>
    private bool? _diagnosticsNarrow;

    private void FitDiagnostics()
    {
        if (!IsLoaded) return;
        var narrow = ActualWidth < 1100;
        if (_diagnosticsNarrow == narrow) return;
        _diagnosticsNarrow = narrow;

        // Paths: beside the facts, or under them.
        DiagFactsCol.Width = narrow ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        DiagTextGutter.Width = new GridLength(narrow ? 0 : 36);
        DiagPathsCol.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(DiagnosticsPaths, narrow ? 1 : 0);
        Grid.SetColumn(DiagnosticsPaths, narrow ? 0 : 2);
        DiagnosticsPaths.Margin = narrow ? new Thickness(0, 12, 0, 0) : default;

        // Buttons: their own column, or a row across the foot of the plate.
        DiagGutter.Width = new GridLength(narrow ? 0 : 28);
        DiagRule.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(DiagButtons, narrow ? 1 : 0);
        Grid.SetColumn(DiagButtons, narrow ? 0 : 2);
        Grid.SetColumnSpan(DiagButtons, narrow ? 3 : 1);
        DiagButtons.Width = narrow ? double.NaN : 250;
        DiagButtons.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        DiagButtons.Margin = narrow ? new Thickness(0, 16, 48, 0) : new Thickness(0, 0, 48, 2);
    }

    // ══════════════════════ window frame ══════════════════════

    /// <summary>
    /// Makes the header strip the draggable caption, exactly as tall as the header measures.
    /// A hard-coded height goes wrong the moment a font or DPI setting differs from mine: too
    /// tall and the top of the page becomes a drag handle, too short and there is a dead strip
    /// above the header that nothing responds to.
    /// </summary>
    private void SyncCaptionHeight()
    {
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (chrome is null || HeaderBar.ActualHeight <= 0) return;
        chrome.CaptionHeight = Math.Max(0, HeaderBar.ActualHeight + HeaderBar.Margin.Top);
    }

    private void OnMinimiseWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximiseWindow(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        var maximised = WindowState == WindowState.Maximized;
        MaxGlyph.Data = Geometry.Parse(maximised
            ? "M3,0.5 H10.5 V8 M0.5,3 H8 V10.5 H0.5 Z"
            : "M0.5,0.5 H10.5 V10.5 H0.5 Z");
        MaximiseButton.ToolTip = maximised ? "Restore" : "Maximise";
        ApplyMaximisedPadding();
    }

    /// <summary>
    /// Windows sizes a maximised window to the work area *plus* the resize border on all four
    /// sides, and leaves the system frame to cover the overhang. This window draws its own frame,
    /// so without padding it back the outermost pixels of the interface — part of the status bar
    /// among them — sit off the edge of the screen. The overhang is measured against the monitor
    /// the window is actually on rather than assumed, so a second display at a different scale
    /// does not get it wrong.
    /// </summary>
    private void ApplyMaximisedPadding()
    {
        if (WindowState != WindowState.Maximized || CurrentMonitorWorkArea() is not { } work)
        {
            RootGrid.Margin = default;
            return;
        }

        var x = Math.Max(0, (ActualWidth - work.Width) / 2);
        var y = Math.Max(0, (ActualHeight - work.Height) / 2);
        RootGrid.Margin = new Thickness(x, y, x, y);
    }

    private Size? CurrentMonitorWorkArea()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return null;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref info)) return null;

        var device = new Point(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        var dip = transform is { } m ? m.Transform(device) : device;
        return new Size(dip.X, dip.Y);
    }

    private const int MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    // ══════════════════════ theme ══════════════════════

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _syncingTheme) return;
        ApplyTheme(Selected(ThemeCombo, "dark"));
    }

    private void OnThemeChipChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || _syncingTheme || sender is not RadioButton rb || rb.Tag is not string id) return;
        ApplyTheme(id);
    }

    private void ApplyTheme(string id)
    {
        if (string.Equals(id, _settings.ThemeId, StringComparison.OrdinalIgnoreCase)) return;

        _settings.ThemeId = ThemeManager.Resolve(id).Id;
        ThemeManager.Apply(_settings.ThemeId);
        _settings.Save();
        UpdateMarkerColor();

        // The palette can be changed from the header dropdown or from the config chips; keep
        // both in step whichever one was used, without letting the sync re-trigger the handlers.
        _syncingTheme = true;
        try
        {
            SelectOption(ThemeCombo, _settings.ThemeId);
            foreach (var chip in _themeChips)
                chip.IsChecked = string.Equals(chip.Tag as string, _settings.ThemeId,
                    StringComparison.OrdinalIgnoreCase);
        }
        finally { _syncingTheme = false; }

        // A brief wash over the whole window so the change reads as deliberate.
        if (_settings.Animations) Motion.FadeTo(this, 1, Motion.Normal);
        UpdateStatusBar();
        UpdateFfmpegStatus();
    }

    // ══════════════════════ camera ══════════════════════

    private void OnRescan(object sender, RoutedEventArgs e) => RescanCameras(autoConnect: false);

    private void RescanCameras(bool autoConnect)
    {
        try { _cameras = AsiCamera.Enumerate().ToList(); }
        catch (Exception ex) { App.Log(ex, "Enumerate"); _cameras = new List<CameraDescriptor>(); }

        CameraCombo.ItemsSource = _cameras
            .Select(c => new Option<int>($"{c.Name}  {c.MaxWidth}×{c.MaxHeight}", c.Id)).ToList();

        if (_cameras.Count == 0)
        {
            CameraCombo.ItemsSource = new[] { new Option<int>("NO CAMERA DETECTED", -1) };
            CameraCombo.SelectedIndex = 0;
            ConnectButton.IsEnabled = false;
            PreviewPlaceholder.Text = AsiSdk.ResolvedPath is null
                ? "ASICamera2.dll NOT FOUND"
                : "NO CAMERA — CLOSE SHARPCAP OR ASISTUDIO FIRST";
            PreviewPlaceholder.Visibility = Visibility.Visible;
            UpdateDiagnostics();
            return;
        }

        ConnectButton.IsEnabled = true;

        // Several ZWO cameras are often plugged into the same machine — a mono main camera,
        // a guider, and the all-sky colour camera this app is for. Grabbing the wrong one
        // would interrupt an imaging run, so only auto-connect when the choice is obvious.
        var remembered = _cameras.FindIndex(c => c.Id == _settings.LastCameraId);
        var colourOnly = _cameras.Count(c => c.IsColor) == 1 ? _cameras.FindIndex(c => c.IsColor) : -1;
        var preferred = remembered >= 0 ? remembered : colourOnly;
        CameraCombo.SelectedIndex = preferred >= 0 ? preferred : 0;

        if (autoConnect && !IsConnected && preferred >= 0) Connect();
        UpdateDiagnostics();
    }

    private bool IsConnected => _engine.ConnectedCamera is not null;

    private void OnConnectToggle(object sender, RoutedEventArgs e)
    {
        if (IsConnected) Disconnect(); else Connect();
    }

    private void Connect()
    {
        var id = Selected(CameraCombo, -1);
        var descriptor = _cameras.FirstOrDefault(c => c.Id == id);
        if (descriptor is null) return;

        try
        {
            _engine.Connect(descriptor);
            _bitmap = null;
            _renderedSequence = -1;
            PreviewPlaceholder.Text = "WAITING FOR FIRST FRAME";
            ConnectButton.Content = "DISCONNECT";
            CameraCombo.IsEnabled = false;
            RescanButton.IsEnabled = false;
            VpFormat.Text = $"{descriptor.MaxWidth} × {descriptor.MaxHeight}   RAW16   " +
                            (descriptor.IsColor ? $"BAYER {descriptor.Bayer}" : "MONO");
            
        }
        catch (Exception ex)
        {
            App.Log(ex, "Connect");
            var hint = ex is CameraException { Code: AsiError.CameraRemoved or AsiError.InvalidId }
                ? "\n\nIf SharpCap or ASIStudio is running, close it — only one program can hold the camera."
                : string.Empty;
            MessageBox.Show(this, ex.Message + hint, "Could not open camera",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateStatusBar();
        UpdateDiagnostics();
    }

    private void Disconnect()
    {
        _engine.Disconnect();
        PreviewImage.Source = null;
        _bitmap = null;
        _renderedSequence = -1;
        PreviewPlaceholder.Text = "NO SIGNAL";
        PreviewPlaceholder.Visibility = Visibility.Visible;
        ConnectButton.Content = "CONNECT";
        CameraCombo.IsEnabled = true;
        RescanButton.IsEnabled = true;
        
        UpdateStatusBar();
    }

    // ══════════════════════ live view ══════════════════════

    private void OnRenderTick(object? sender, EventArgs e)
    {
        // The player drives its own readout from the render tick while it is open; this timer
        // is only for the camera preview.
        var sequence = _engine.FrameSequence;
        if (sequence == _renderedSequence) return;

        var w = _engine.Width;
        var h = _engine.Height;
        if (w <= 0 || h <= 0) return;

        if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
        {
            // One bitmap for the life of the connection. Creating a BitmapSource per frame is
            // exactly the pattern that makes a viewer balloon over days of uptime.
            _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Rgb24, null);
            PreviewImage.Source = _bitmap;
        }

        var rect = new Int32Rect(0, 0, w, h);
        var stride = w * 3;
        if (!_engine.CopyLatestFrame(buffer => _bitmap.WritePixels(rect, buffer, stride, 0))) return;

        _renderedSequence = sequence;
        if (PreviewPlaceholder.Visibility == Visibility.Visible)
        {
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            if (_settings.Animations) Motion.Enter(PreviewImage, 0, 0);
        }
    }

    private void UpdateViewportChrome()
    {
        ViewportChrome.Visibility = _settings.ViewportChrome ? Visibility.Visible : Visibility.Collapsed;
    }

    // ══════════════════════ status ══════════════════════

    private void OnStatusTick(object? sender, EventArgs e) => UpdateStatusBar();

    private void UpdateStatusBar()
    {
        var status = _engine.Status;
        HeaderClock.Text = DateTime.Now.ToString("HH:mm:ss");

        StatePill.Text = status.State switch
        {
            EngineState.Disconnected => "OFFLINE",
            EngineState.Connecting => "LINKING",
            EngineState.Previewing => "LIVE",
            EngineState.Recording => "RECORDING",
            _ => "FAULT"
        };

        var chip = status.State switch
        {
            EngineState.Recording => "Danger",
            EngineState.Previewing => "Signal",
            EngineState.Error => "Danger",
            _ => "TextFaint"
        };
        StateChip.Fill = (Brush)FindResource(chip);
        StatePill.Foreground = (Brush)FindResource(chip);
        LinkLed.Fill = (Brush)FindResource(IsConnected ? "Signal" : "TextFaint");

        var recording = _engine.IsRecording;
        RecordButton.Content = recording ? "STOP RECORDING" : "START RECORDING";
        RecordButton.Style = (Style)FindResource(recording ? "BtnDanger" : "BtnPrimary");
        RecordButton.IsEnabled = IsConnected;

        if (recording && RecBadge.Visibility != Visibility.Visible)
        {
            RecBadge.Visibility = Visibility.Visible;
            BlinkRecDot(true);
        }
        else if (!recording && RecBadge.Visibility == Visibility.Visible)
        {
            RecBadge.Visibility = Visibility.Collapsed;
            BlinkRecDot(false);
        }

        if (recording)
        {
            var elapsed = DateTime.Now - (status.RecordingStarted ?? DateTime.Now);
            RecBadgeText.Text = $"REC {Format.Duration(elapsed)} · {status.FramesRecorded:N0}F";
        }

        VpSensor.Text = status.SensorTempC is { } t
            ? $"SENSOR {t:0.0}°C"
            : IsConnected ? "SENSOR ---" : "";

        UpdateExposureWarning(status);

        var left = new List<string> { status.Message.ToUpperInvariant() };
        if (status.FramesCaptured > 0) left.Add($"{status.FramesCaptured:N0} FRAMES");
        if (status.LastFrameSeconds > 0) left.Add($"LAST {status.LastFrameSeconds:0.0}S");
        if (status.AutoExposureSeconds is { } aeSec && status.AutoExposureGain is { } aeGain)
            left.Add($"AUTO {FormatExposure(aeSec)} @ {aeGain}");
        if (status.SkyLevel is { } sky && sky > 0) left.Add($"SKY {sky:P0}");
        if (status.ClippedFraction > 0.02) left.Add($"CLIP {status.ClippedFraction:P0}");
        if (status.NextCaptureDue is { } due) left.Add($"NEXT {due:HH:mm:ss}");
        if (recording && status.VideoBytesSoFar > 0)
        {
            left.Add($"VIDEO {Format.Bytes(status.VideoBytesSoFar)}");
            var saved = status.EstimatedRawBytes - status.VideoBytesSoFar;
            if (saved > 0) left.Add($"SAVED {Format.Bytes(saved)}");
        }
        StatusLeft.Text = string.Join("   ·   ", left);

        _self.Refresh();
        StatusRight.Text =
            $"MEM {Format.Bytes(_self.WorkingSet64)}   ·   UP {Format.Duration(DateTime.Now - _startedAt)}{FreeSpaceText()}";

        // The readout above just changed width, which is part of what the text cap is measured
        // against. Re-fit after it, not before, or the cap is a tick behind the numbers.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FitStatusBar);
    }

    /// <summary>
    /// Explains a featureless frame rather than leaving it looking like a dead camera.
    /// A blown-out frame and a black one both render as flat fields once the auto-stretch has
    /// done its best, and neither tells you which it is.
    /// </summary>
    private void UpdateExposureWarning(EngineStatus status)
    {
        if (!IsConnected || PreviewImage.Source is null)
        {
            ExposureWarning.Visibility = Visibility.Collapsed;
            return;
        }

        string? title = null, body = null;
        var ramping = status.AutoExposureSeconds is not null;

        if (status.ClippedFraction > 0.60)
        {
            title = "OVEREXPOSED";
            body = ramping
                ? "TOO MUCH LIGHT — SHORTENING EXPOSURE"
                : "TOO MUCH LIGHT. TURN ON AUTO EXPOSE LIVE VIEW,\nOR SHORTEN THE PREVIEW SUB.";
        }
        else if (status.SkyLevel is { } sky && sky < 0.0008 && status.ClippedFraction < 0.01)
        {
            title = "NO LIGHT";
            body = ramping
                ? "NOTHING REACHING THE SENSOR — LENGTHENING EXPOSURE"
                : "NOTHING REACHING THE SENSOR. CHECK THE CAP AND ROOF,\nOR LENGTHEN THE PREVIEW SUB.";
        }

        if (title is null)
        {
            if (ExposureWarning.Visibility == Visibility.Visible)
                ExposureWarning.Visibility = Visibility.Collapsed;
            return;
        }

        ExposureWarningTitle.Text = title;
        ExposureWarningBody.Text = body;
        if (ExposureWarning.Visibility != Visibility.Visible)
        {
            ExposureWarning.Visibility = Visibility.Visible;
            if (_settings.Animations) Motion.Enter(ExposureWarning, 0, 8);
        }
    }

    private void BlinkRecDot(bool on)
    {
        if (!on || !_settings.Animations)
        {
            RecDot.BeginAnimation(OpacityProperty, null);
            RecDot.Opacity = 1;
            return;
        }
        var anim = new DoubleAnimation(1, 0.15, new Duration(TimeSpan.FromMilliseconds(700)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        RecDot.BeginAnimation(OpacityProperty, anim);
    }

    private string FreeSpaceText()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_settings.LibraryRoot));
            if (root is null) return string.Empty;
            return $"   ·   {new DriveInfo(root).AvailableFreeSpace / (1024.0 * 1024 * 1024):0.#} GB FREE";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    // ══════════════════════ recording & schedule ══════════════════════

    private void OnRecordToggle(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRecording)
        {
            _engine.StopRecording("Stopped by hand");
            _scheduler.MarkHandled(DateTime.Now);
        }
        else
        {
            StartRecording(SessionTitleBox.Text);
            if (_settings.Animations) Motion.Pulse(RecordButton);
        }
        UpdateStatusBar();
    }

    private void StartRecording(string title)
    {
        try { _engine.StartRecording(title); }
        catch (FfmpegNotFoundException ex)
        {
            MessageBox.Show(this, ex.Message, "Encoder missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            NavConfig.IsChecked = true;
        }
        catch (Exception ex)
        {
            App.Log(ex, "StartRecording");
            MessageBox.Show(this, ex.Message, "Could not start recording",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnScheduleTick(object? sender, EventArgs e)
    {
        UpdateScheduleHint();
        if (!IsConnected) return;

        var now = DateTime.Now;
        switch (_scheduler.Evaluate(now, _engine.IsRecording, _engine.IsHeld))
        {
            case SchedulerAction.Start:
                StartRecording(now.ToString("dddd d MMMM yyyy"));
                UpdateStatusBar();
                break;

            case SchedulerAction.Stop:
                _engine.StopRecording("Scheduled stop");
                _scheduler.MarkHandled(now);
                UpdateStatusBar();
                break;

            case SchedulerAction.Hold:
                _scheduler.RoofPermits(now, out var why);
                _engine.SetHold(true, why);
                UpdateStatusBar();
                break;

            case SchedulerAction.Resume:
                _engine.SetHold(false, string.Empty);
                UpdateStatusBar();
                break;
        }
    }

    private void OnSessionFinished(TimelapseManifest manifest) =>
        Dispatcher.BeginInvoke(() =>
        {
            RefreshLibrary();
            UpdateStatusBar();
            // A machine that is only ever on overnight would otherwise never reach a sweep.
            RunHousekeepingIfDue();
        });

    private void OnScheduleModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Session.Schedule =
            ScheduleAstroRadio.IsChecked == true ? ScheduleMode.Astronomical :
            ScheduleNightlyRadio.IsChecked == true ? ScheduleMode.Nightly :
            ScheduleMode.Manual;
        _scheduler.Rearm();
        UpdateScheduleUi();
        UpdatePlan();
        _settings.Save();
    }

    private void OnTwilightChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Session.Twilight = Selected(TwilightCombo, TwilightKind.Astronomical);
        UpdateScheduleHint();
        UpdatePlan();
        _settings.Save();
    }

    private void OnRoofGateChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Session.RequireRoofOpen = RoofGateCheck.IsChecked == true;
        UpdateScheduleHint();
        _settings.Save();
    }

    private void OnRoofUnknownChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Session.RecordWhenRoofUnknown = RoofUnknownCheck.IsChecked == true;
        UpdateScheduleHint();
        _settings.Save();
    }

    private void OnRoofPathChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Session.RoofStatusPath = RoofPathBox.Text.Trim();
        _settings.Session.RoofPollSeconds = ReadInt(RoofPollBox, _settings.Session.RoofPollSeconds, 5, 600);
        _settings.Session.RoofAbandonMinutes = ReadInt(RoofStaleBox, _settings.Session.RoofAbandonMinutes, 1, 1440);
        _settings.Session.RoofLingerMinutes = ReadInt(RoofLingerBox, _settings.Session.RoofLingerMinutes, 0, 120);
        RoofPollBox.Text = _settings.Session.RoofPollSeconds.ToString(CultureInfo.InvariantCulture);
        RoofStaleBox.Text = _settings.Session.RoofAbandonMinutes.ToString(CultureInfo.InvariantCulture);
        RoofLingerBox.Text = _settings.Session.RoofLingerMinutes.ToString(CultureInfo.InvariantCulture);
        ApplyRoofSettings();
        _settings.Save();
    }

    private void ApplyRoofSettings()
    {
        var s = _settings.Session;
        _roof.Configure(string.IsNullOrWhiteSpace(s.RoofStatusPath) ? null : s.RoofStatusPath,
            s.RoofPollSeconds, s.RoofAbandonMinutes);
    }

    private void OnBrowseRoofFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Locate the roof status file",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        RoofPathBox.Text = dialog.FileName;
        OnRoofPathChanged(sender, e);
    }

    /// <summary>
    /// Looks for an SFRO-style roof file on any mapped drive or the site share. Saves hunting
    /// through \\server\share\roof\building-N by hand.
    /// </summary>
    private void OnDetectRoofFile(object sender, RoutedEventArgs e)
    {
        var found = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Network || !drive.IsReady) continue;
                var roofRoot = Path.Combine(drive.RootDirectory.FullName, "roof");
                if (!Directory.Exists(roofRoot)) continue;

                foreach (var dir in Directory.EnumerateDirectories(roofRoot))
                {
                    var candidate = Path.Combine(dir, "RoofStatusFile.txt");
                    if (File.Exists(candidate)) found.Add(candidate);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log(ex, "DetectRoof");
        }

        if (found.Count == 0)
        {
            MessageBox.Show(this,
                "No roof status file found on any mapped network drive.\n\n" +
                "Expected something like:\n  Z:\\roof\\<building>\\RoofStatusFile.txt",
                "Nothing found", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var chosen = found[0];
        if (found.Count > 1)
        {
            // Several buildings on the share — ask which one is theirs.
            var list = string.Join("\n", found.Select((f, i) => $"{i + 1}. {Path.GetFileName(Path.GetDirectoryName(f))}"));
            var answer = Dialogs.Prompt(this, "Which building?",
                $"Found {found.Count} roof files:\n{list}\n\nEnter the number:", "1");
            if (answer is null) return;
            if (int.TryParse(answer, out var pick) && pick >= 1 && pick <= found.Count)
                chosen = found[pick - 1];
        }

        RoofPathBox.Text = chosen;
        OnRoofPathChanged(sender, e);
        _roof.PollNow();
        UpdateScheduleHint();
    }

    private void OnSiteChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var site = _settings.Site;
        site.Latitude = ReadDouble(LatBox, site.Latitude, -90, 90);
        site.Longitude = ReadDouble(LonBox, site.Longitude, -180, 180);
        site.ElevationMetres = ReadDouble(ElevBox, site.ElevationMetres, -500, 9000);
        LoadSiteIntoUi();
        UpdateScheduleHint();
        UpdatePlan();
        _settings.Save();
    }

    /// <summary>
    /// Reads the observing site out of NINA's active profile. Opened shared and read-only —
    /// NINA keeps the file open while it runs, and it must not be disturbed.
    /// </summary>
    private void OnImportSiteFromNina(object sender, RoutedEventArgs e)
    {
        if (TryImportSiteFromNina(out var error))
        {
            LoadSiteIntoUi();
            UpdateScheduleHint();
            _settings.Save();
            if (_settings.Animations) Motion.Pulse(SiteHint);
        }
        else
        {
            MessageBox.Show(this, $"Could not read the site from NINA:\n{error}",
                "Import failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool TryImportSiteFromNina(out string error)
    {
        error = string.Empty;
        var profileDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "Profiles");

        if (!Directory.Exists(profileDir))
        {
            error = "No NINA profile folder on this machine.";
            return false;
        }

        try
        {
            var newest = new DirectoryInfo(profileDir).GetFiles("*.profile")
                .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            if (newest is null) throw new FileNotFoundException("no .profile files");

            string text;
            using (var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
                text = reader.ReadToEnd();

            double? Grab(string tag)
            {
                var m = System.Text.RegularExpressions.Regex.Match(text, $"<{tag}>([^<]+)</{tag}>");
                return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var v) ? v : null;
            }

            var lat = Grab("Latitude");
            var lon = Grab("Longitude");
            if (lat is null || lon is null) throw new InvalidDataException("no coordinates in profile");

            _settings.Site.Latitude = lat.Value;
            _settings.Site.Longitude = lon.Value;
            _settings.Site.ElevationMetres = Grab("Elevation") ?? _settings.Site.ElevationMetres;
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex, "ImportSite");
            error = ex.Message;
            return false;
        }
    }

    private void LoadSiteIntoUi()
    {
        var site = _settings.Site;
        LatBox.Text = site.Latitude.ToString("0.######", CultureInfo.InvariantCulture);
        LonBox.Text = site.Longitude.ToString("0.######", CultureInfo.InvariantCulture);
        ElevBox.Text = site.ElevationMetres.ToString("0.#", CultureInfo.InvariantCulture);

        if (!site.IsSet)
        {
            SiteHint.Text = "NOT SET — DUSK-TO-DAWN SCHEDULING NEEDS COORDINATES.";
            return;
        }

        var tonight = SunCalculator.NightOf(SunCalculator.NightDateFor(DateTime.Now),
            site.Latitude, site.Longitude, _settings.Session.Twilight);

        SiteHint.Text = tonight is { } w
            ? $"{Math.Abs(site.Latitude):0.####}°{(site.Latitude >= 0 ? "N" : "S")} " +
              $"{Math.Abs(site.Longitude):0.####}°{(site.Longitude >= 0 ? "E" : "W")}  ·  " +
              $"TONIGHT {w.Start:HH:mm} → {w.End:HH:mm}"
            : "SUN NEVER REACHES THAT DEPTH HERE TONIGHT — FIXED TIMES WILL BE USED.";
    }

    private void OnScheduleTimeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (TryParseTime(StartTimeBox.Text, out var start)) _settings.Session.StartTime = start;
        if (TryParseTime(EndTimeBox.Text, out var end)) _settings.Session.EndTime = end;
        StartTimeBox.Text = FormatTime(_settings.Session.StartTime);
        EndTimeBox.Text = FormatTime(_settings.Session.EndTime);
        _scheduler.Rearm();
        UpdateScheduleHint();
        UpdatePlan();
        _settings.Save();
    }

    private static bool TryParseTime(string text, out TimeSpan value)
    {
        value = default;
        text = text.Trim();
        if (TimeSpan.TryParseExact(text, @"h\:mm", CultureInfo.InvariantCulture, out value)) return true;
        if (TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out value)) return true;
        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt))
        {
            value = dt.TimeOfDay;
            return true;
        }
        return false;
    }

    private void UpdateScheduleUi()
    {
        var mode = _settings.Session.Schedule;
        ScheduleTimesGrid.Visibility = mode == ScheduleMode.Nightly ? Visibility.Visible : Visibility.Collapsed;
        ScheduleAstroGrid.Visibility = mode == ScheduleMode.Astronomical ? Visibility.Visible : Visibility.Collapsed;
        UpdateScheduleHint();
    }

    private void UpdateScheduleHint()
    {
        var view = _scheduler.Describe(DateTime.Now, _engine.IsRecording, _engine.IsHeld);
        ScheduleHint.Text = view.Summary;
        RoofHint.Text = view.RoofSummary;

        var roof = _roof.Status;
        RoofLed.Fill = (Brush)FindResource(
            !_settings.Session.RequireRoofOpen ? "TextFaint"
            : roof.State switch
            {
                RoofState.Open => "Signal",
                RoofState.Closed => "Warn",
                _ => "Danger"
            });

        if (RoofStatusLine is not null)
        {
            RoofStatusLine.Text = string.IsNullOrWhiteSpace(_settings.Session.RoofStatusPath)
                ? "NO ROOF FILE SET — PRESS DETECT, OR BROWSE TO IT."
                : $"{roof.State.ToString().ToUpperInvariant()} · {roof.Detail.ToUpperInvariant()}" +
                  (roof.ReportedLocal is { } r ? $" · FILE SAYS {r:yyyy-MM-dd HH:mm:ss}" : string.Empty);
        }
    }

    // ══════════════════════ plan estimate ══════════════════════

    private void OnPlanChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Session.IntervalSeconds = Selected(IntervalCombo, 60);
        _settings.Video.Fps = Selected(FpsCombo, 30);
        _settings.Video.Crf = Selected(QualityCombo, 26);
        _settings.Video.Denoise = Selected(DenoiseCombo, DenoiseLevel.Medium);
        var res = Selected(ResolutionCombo, (1920, 1080));
        _settings.Video.OutputWidth = res.Item1;
        _settings.Video.OutputHeight = res.Item2;
        UpdatePlan();
        _settings.Save();
    }

    private void UpdatePlan()
    {
        // Use the real window whenever one is scheduled — for dusk-to-dawn that is the actual
        // length of tonight's darkness, which is the whole point of the mode.
        var scheduled = _settings.Session.Schedule != ScheduleMode.Manual;
        var night = scheduled ? WindowLength() : TimeSpan.FromHours(10);

        var sensorW = _engine.Width > 0 ? _engine.Width : 1920;
        var sensorH = _engine.Height > 0 ? _engine.Height : 1080;

        var plan = CapturePlan.Compute(night, _settings.Camera.ExposureSeconds,
            _settings.Session.IntervalSeconds, _settings.Video.Fps, _settings.Video.Crf,
            _settings.Video.OutputWidth, _settings.Video.OutputHeight, sensorW, sensorH,
            _settings.Video.Denoise);

        var basis = _settings.Session.Schedule switch
        {
            ScheduleMode.Astronomical => "OF DARKNESS TONIGHT",
            ScheduleMode.Nightly => "SCHEDULED",
            _ => "10-HOUR NIGHT"
        };
        PlanFramesText.Text =
            $"{Format.Duration(night)} {basis}  →  {plan.Frames:N0} FRAMES  →  {Format.Duration(plan.VideoLength)} VIDEO";

        // A shorter night in winter means fewer frames; the readout should follow the season
        // rather than pretend every night is the same length.
        UpdateScheduleHint();

        var savings = plan.EstimatedRawBytes - plan.EstimatedVideoBytes;
        PlanSizeText.Text = plan.Frames == 0
            ? "NO FRAMES — CHECK EXPOSURE AND INTERVAL."
            : $"≈ {Format.Bytes(plan.EstimatedVideoBytes)} ON DISK · RAW WOULD BE ≈ {Format.Bytes(plan.EstimatedRawBytes)} · SAVES {Format.Bytes(savings)}";

        if (plan.Frames > 0 && plan.VideoLength.TotalSeconds < 15)
            PlanSizeText.Text += $"\n⚠ ONLY {Format.Duration(plan.VideoLength)} LONG — A SHORTER INTERVAL GIVES A LONGER VIDEO.";
    }

    private TimeSpan WindowLength()
    {
        // Use the real dark window when one is available, so the frame-count estimate tracks
        // the season instead of assuming a fixed night length.
        if (_scheduler.WindowFor(DateTime.Now) is { } w && w.Length > TimeSpan.Zero) return w.Length;

        var length = _settings.Session.EndTime - _settings.Session.StartTime;
        if (length <= TimeSpan.Zero) length += TimeSpan.FromDays(1);
        return length;
    }

    // ══════════════════════ camera controls ══════════════════════

    private void OnExposureChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _settings.Camera.ExposureSeconds = Math.Round(e.NewValue, 2);
        UpdateSliderLabels();
        UpdatePlan();
        _settings.Save();
    }

    private void OnGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _settings.Camera.Gain = (int)e.NewValue;
        UpdateSliderLabels();
        _settings.Save();
    }

    private void OnPreviewExposureChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _settings.Camera.PreviewExposureSeconds = Math.Round(e.NewValue, 4);
        UpdateSliderLabels();
        _settings.Save();
    }

    private void OnPreviewGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _settings.Camera.PreviewGain = (int)e.NewValue;
        UpdateSliderLabels();
        _settings.Save();
    }

    private void OnPreviewAutoChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Camera.PreviewAutoExposure = PreviewAutoCheck.IsChecked == true;
        UpdatePreviewAutoUi();
        _settings.Save();
    }

    private void UpdatePreviewAutoUi()
    {
        var auto = _settings.Camera.PreviewAutoExposure;
        PreviewManualGrid.IsEnabled = !auto;
        PreviewManualGrid.Opacity = auto ? 0.45 : 1.0;
        PreviewAutoHint.Text = auto
            ? "LIVE VIEW FINDS ITS OWN EXPOSURE, DAYLIGHT TO DARK SKY."
            : "FIXED PREVIEW EXPOSURE. FLAT GREY USUALLY MEANS THE FRAME IS SATURATED.";
    }

    private void OnTargetBgChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _settings.Stretch.TargetBackground = Math.Round(e.NewValue, 3);
        UpdateSliderLabels();
        _settings.Save();
    }

    /// <summary>A slider, the number beside it, and how that number is read and written.</summary>
    private sealed record SliderRow(
        Slider Slider, TextBox Box, double Default,
        Func<double, string> Format, Func<string, double?> Parse);

    private readonly List<SliderRow> _sliderRows = new();

    /// <summary>
    /// Gives every settings slider a double-click reset and makes its readout typeable.
    ///
    /// The defaults come from a fresh <see cref="AppSettings"/> rather than being written out
    /// again here, so "reset" always means the value the app actually ships with and the two
    /// cannot drift apart.
    /// </summary>
    private void WireSliders()
    {
        var d = new AppSettings();
        string Whole(double v) => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);

        Wire(ExposureSlider, ExposureValue, d.Camera.ExposureSeconds, FormatExposure, ParseSeconds);
        Wire(GainSlider, GainValue, d.Camera.Gain, Whole, ParsePlain);
        Wire(PreviewExposureSlider, PreviewExposureValue, d.Camera.PreviewExposureSeconds,
            FormatExposure, ParseSeconds);
        Wire(PreviewGainSlider, PreviewGainValue, d.Camera.PreviewGain, Whole, ParsePlain);
        Wire(TargetBgSlider, TargetBgValue, d.Stretch.TargetBackground,
            v => v.ToString("0.00", CultureInfo.InvariantCulture), ParsePlain);
    }

    private void Wire(Slider slider, TextBox box, double fallback,
        Func<double, string> format, Func<string, double?> parse)
    {
        var row = new SliderRow(slider, box, fallback, format, parse);
        _sliderRows.Add(row);

        // Preview rather than the bubbling event: the track is made of repeat buttons that
        // handle a click before it ever reaches the slider, so a bubbling double-click never
        // arrives. Handling it here also stops the second click jumping the value first.
        slider.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            slider.Value = Math.Clamp(row.Default, slider.Minimum, slider.Maximum);
            e.Handled = true;
            if (_settings.Animations) Motion.Pulse(box);
        };

        // A click anywhere in the number should put a caret in it, not require hitting a glyph.
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (box.IsKeyboardFocusWithin) return;
            box.Focus();
            e.Handled = true;
        };
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
        box.LostKeyboardFocus += (_, _) => Commit(row);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(row); Keyboard.ClearFocus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Show(row); Keyboard.ClearFocus(); e.Handled = true; }
        };
    }

    /// <summary>
    /// Applies whatever was typed. Anything unparseable or out of range simply loses — the box
    /// is rewritten from the slider, so a typo can never leave a number on screen that is not
    /// the one in effect.
    /// </summary>
    private void Commit(SliderRow row)
    {
        if (row.Parse(row.Box.Text) is { } v)
            row.Slider.Value = Math.Clamp(v, row.Slider.Minimum, row.Slider.Maximum);
        Show(row);
    }

    private static void Show(SliderRow row) => row.Box.Text = row.Format(row.Slider.Value);

    private void UpdateSliderLabels()
    {
        // Never overwrite a box that is being typed into.
        foreach (var row in _sliderRows)
            if (!row.Box.IsKeyboardFocusWithin) Show(row);
    }

    /// <summary>
    /// Reads an exposure typed as a bare number of seconds, or carrying the unit the readout
    /// shows — "500 ms" and "500ms" both work, because retyping what you can see should do
    /// what it says.
    /// </summary>
    private static double? ParseSeconds(string text)
    {
        text = text.Trim().ToUpperInvariant();
        var scale = 1.0;

        if (text.EndsWith("MS", StringComparison.Ordinal)) { scale = 0.001; text = text[..^2]; }
        else if (text.EndsWith("US", StringComparison.Ordinal)) { scale = 0.000001; text = text[..^2]; }
        else if (text.EndsWith("S", StringComparison.Ordinal)) { text = text[..^1]; }

        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v * scale : null;
    }

    private static double? ParsePlain(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : null;

    private static string FormatExposure(double seconds) => seconds switch
    {
        < 0.001 => $"{seconds * 1_000_000:0} US",
        < 1 => $"{seconds * 1000:0.#} MS",
        _ => $"{seconds:0.##} S"
    };

    private void OnStretchModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Stretch.Mode = Selected(StretchModeCombo, StretchMode.Smoothed);
        _settings.Save();
    }

    private void OnNeutraliseChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Stretch.NeutraliseBackground = NeutraliseCheck.IsChecked == true;
        _settings.Save();
    }

    private void OnTimestampChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Video.BurnTimestamp = TimestampCheck.IsChecked == true;
        _settings.Save();
    }

    private void OnAutoExposureChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Camera.AutoExposure = AutoExposureCheck.IsChecked == true;
        UpdateAutoExposureHint();
        _settings.Save();
    }

    private void UpdateAutoExposureHint()
    {
        var c = _settings.Camera;
        AutoExposureHint.Text = c.AutoExposure
            ? $"RAMPS {FormatExposure(c.AutoExposureMinSeconds)}–{FormatExposure(c.AutoExposureMaxSeconds)}, GAIN {c.AutoExposureMinGain}–{c.AutoExposureMaxGain}, MAX {c.AutoExposureMaxStep:P0}/FRAME."
            : "OFF — FIXED EXPOSURE. FINE UNTIL DAWN, THEN FRAMES SATURATE.";
    }

    // ══════════════════════ startup & housekeeping options ══════════════════════

    /// <summary>
    /// The registry is the record of truth for "start with Windows", not the settings file —
    /// the entry can be removed from Task Manager's startup tab at any time without coming back
    /// here, so the checkbox is set from the key rather than from the saved flag.
    /// </summary>
    private void LoadStartupIntoUi()
    {
        var registered = Ui.Startup.IsRegistered(out var command);
        _settings.Startup.RunAtLogin = registered;

        RunAtLoginCheck.IsChecked = registered;
        StartMinimisedCheck.IsChecked = _settings.Startup.StartMinimised;
        StartMinimisedCheck.IsEnabled = registered;
        UpdateStartupHint(command);
    }

    private void UpdateStartupHint(string? command)
    {
        StartupHint.Text = RunAtLoginCheck.IsChecked == true
            ? $"REGISTERED FOR THIS USER · {command ?? Ui.Startup.ExecutablePath}".ToUpperInvariant()
            : "PIERCAM WILL NOT LAUNCH ON ITS OWN. MOVING OR REBUILDING THE APP NEEDS THIS TICKED AGAIN.";
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var run = RunAtLoginCheck.IsChecked == true;
        var minimised = StartMinimisedCheck.IsChecked == true;

        if (!Ui.Startup.Apply(run, minimised, out var error))
        {
            MessageBox.Show(this, $"Could not change the startup setting:\n{error}",
                "Startup", MessageBoxButton.OK, MessageBoxImage.Warning);
            LoadStartupIntoUi();
            return;
        }

        _settings.Startup.RunAtLogin = run;
        _settings.Startup.StartMinimised = minimised;
        StartMinimisedCheck.IsEnabled = run;
        Ui.Startup.IsRegistered(out var command);
        UpdateStartupHint(command);
        _settings.Save();
    }

    private void LoadHousekeepingIntoUi()
    {
        var h = _settings.Housekeeping;
        AutoDownscaleCheck.IsChecked = h.AutoDownscale;
        AutoDownscaleDaysBox.Text = h.AfterDays.ToString(CultureInfo.InvariantCulture);
        AutoDownscaleTargetCombo.SelectedItem =
            Array.Find(VideoTranscoder.Targets, t => t.Width == h.TargetWidth && t.Height == h.TargetHeight)
            ?? VideoTranscoder.Targets[0];
        UpdateHousekeepingHint();
    }

    private void OnHousekeepingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var h = _settings.Housekeeping;

        // Switching this on is the only setting in the app that can destroy something, and it
        // does so later, unattended, when nobody is watching. It therefore has to be *asked for*
        // rather than merely toggled — a stray click, or a mis-aimed automation step, must not be
        // enough to start re-encoding a library.
        var turningOn = AutoDownscaleCheck.IsChecked == true && !h.AutoDownscale;
        if (turningOn && !ConfirmAutoDownscale())
        {
            AutoDownscaleCheck.IsChecked = false;
            return;
        }

        h.AutoDownscale = AutoDownscaleCheck.IsChecked == true;
        h.AfterDays = ReadInt(AutoDownscaleDaysBox, h.AfterDays, 1, 3650);
        AutoDownscaleDaysBox.Text = h.AfterDays.ToString(CultureInfo.InvariantCulture);

        if (AutoDownscaleTargetCombo.SelectedItem is DownscaleTarget t)
        {
            h.TargetWidth = t.Width;
            h.TargetHeight = t.Height;
        }

        UpdateHousekeepingHint();
        _settings.Save();
    }

    private void OnHousekeepingChanged(object sender, SelectionChangedEventArgs e) =>
        OnHousekeepingChanged(sender, (RoutedEventArgs)e);

    private bool ConfirmAutoDownscale()
    {
        var h = _settings.Housekeeping;
        _housekeeper ??= new Housekeeper(_settings, () => _engine.IsRecording);
        var due = _housekeeper.Due(_library.Items, DateTime.Now);

        var affected = due.Count == 0
            ? "Nothing in the library qualifies yet."
            : $"{due.Count} timelapse{(due.Count == 1 ? "" : "s")} already qualif{(due.Count == 1 ? "ies" : "y")} " +
              "and would be re-encoded the next time a sweep runs.";

        return Dialogs.Confirm(this, "Shrink old timelapses automatically",
            $"Timelapses older than {h.AfterDays} days will be re-encoded to " +
            $"{h.TargetWidth} × {h.TargetHeight}, on their own, when nobody is watching.\n\n" +
            $"{affected}\n\n" +
            "This replaces each video in place. The original resolution cannot be recovered.",
            confirmText: "TURN ON", danger: true);
    }

    /// <summary>Says what the sweep would do right now, so it is never a surprise.</summary>
    private void UpdateHousekeepingHint()
    {
        var h = _settings.Housekeeping;
        if (!h.AutoDownscale)
        {
            HousekeepingHint.Text = "OFF — TIMELAPSES ARE LEFT AT THE RESOLUTION THEY WERE RECORDED.";
            return;
        }

        _housekeeper ??= new Housekeeper(_settings, () => _engine.IsRecording);
        var due = _housekeeper.Due(_library.Items, DateTime.Now);
        var ran = h.LastRun is { } last ? $" · LAST RUN {last:d MMM HH:mm}".ToUpperInvariant() : string.Empty;

        HousekeepingHint.Text = due.Count == 0
            ? $"NOTHING IS OLDER THAN {h.AfterDays} DAYS AND STILL LARGER THAN {h.TargetWidth}×{h.TargetHeight}.{ran}"
            : $"{due.Count} TIMELAPSE{(due.Count == 1 ? "" : "S")} WOULD BE SHRUNK TO " +
              $"{h.TargetWidth}×{h.TargetHeight} ON THE NEXT SWEEP. THIS REPLACES THE VIDEO AND CANNOT BE UNDONE.{ran}";
    }

    private void OnAnimationsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Animations = AnimationsCheck.IsChecked == true;
        BlinkRecDot(_engine.IsRecording);
        _settings.Save();
    }

    private void OnChromeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.ViewportChrome = ChromeCheck.IsChecked == true;
        UpdateViewportChrome();
        _settings.Save();
    }

    // ══════════════════════ library ══════════════════════

    private void OnRefreshLibrary(object sender, RoutedEventArgs e) => RefreshLibrary();

    private void RefreshLibrary()
    {
        _library.Refresh(_settings.TimelapseRoot);

        // Items were replaced, so the old panel's event hook is gone with it.
        _flowHooked = false;

        var count = _library.Items.Count;
        LibraryCountText.Text = count switch
        {
            0 => "NO TIMELAPSES",
            1 => "1 TIMELAPSE",
            _ => $"{count} TIMELAPSES"
        };

        var saved = _library.TotalEstimatedRawBytes - _library.TotalVideoBytes;
        LibrarySavingsText.Text = count == 0
            ? _settings.TimelapseRoot.ToUpperInvariant()
            : $"{Format.Bytes(_library.TotalVideoBytes)} TOTAL" +
              (saved > 0 ? $" · {Format.Bytes(saved)} LESS THAN RAW FRAMES" : string.Empty);

        LibraryEmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateBatchBar();
        UpdateCarouselReadout();
    }

    private void OnLibraryViewChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.LibraryView = ViewGridRadio.IsChecked == true
            ? Models.LibraryView.Grid : Models.LibraryView.Carousel;
        _settings.Save();
        ApplyLibraryView();
    }

    private void ApplyLibraryView()
    {
        var carousel = _settings.LibraryView == Models.LibraryView.Carousel;

        // Only one view is realised at a time, so the hidden one costs no containers.
        if (carousel)
        {
            GridScroller.Visibility = Visibility.Collapsed;
            CarouselHost.Visibility = Visibility.Visible;
            if (_settings.Animations && IsLoaded) Motion.Enter(CarouselHost, 0, 0, 40);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => CenterCarousel(0, immediate: true));
        }
        else
        {
            CarouselHost.Visibility = Visibility.Collapsed;
            GridScroller.Visibility = Visibility.Visible;
            if (_settings.Animations && IsLoaded) Motion.Enter(GridScroller, 0, 12);
        }
        UpdateCarouselReadout();
    }

    private bool _flowHooked;

    private CoverFlowPanel? Flow
    {
        get
        {
            var flow = FindDescendant<CoverFlowPanel>(CarouselList);
            if (flow is not null && !_flowHooked)
            {
                // The panel knows when the fan has actually settled on a card. Driving the
                // readout from that rather than from the target index keeps the number in step
                // with the card under it however the carousel was moved — buttons, wheel,
                // keyboard or a click on a side card.
                flow.CenterChanged += OnCarouselCenterChanged;
                _flowHooked = true;
            }
            return flow;
        }
    }

    private void OnCarouselCenterChanged(int index)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ShowCarouselIndex(index)); return; }
        ShowCarouselIndex(index);
    }

    private void StepCarousel(int delta)
    {
        var flow = Flow;
        if (flow is null) return;
        // Counted from where the shelf is heading, not from where it currently is, so holding an
        // arrow key advances a card per press instead of repeatedly re-aiming at the same one.
        CenterCarousel(flow.TargetIndex + delta, immediate: false);
    }

    private void CenterCarousel(int index, bool immediate)
    {
        var flow = Flow;
        if (flow is null) return;

        index = Math.Clamp(index, 0, Math.Max(0, _library.Items.Count - 1));
        flow.AnimateTo(index, immediate || !_settings.Animations);

        // Show the destination straight away; CenterChanged will confirm it as the fan settles.
        ShowCarouselIndex(index);
    }

    private void OnCarouselPrev(object sender, RoutedEventArgs e) => StepCarousel(-1);
    private void OnCarouselNext(object sender, RoutedEventArgs e) => StepCarousel(1);

    /// <summary>
    /// Clicking a card that is not in focus brings it to the centre. Clicks on the focused card
    /// fall through untouched so its own buttons still work.
    /// </summary>
    private void OnCarouselClick(object sender, MouseButtonEventArgs e)
    {
        var flow = Flow;
        if (flow is null) return;

        var container = FindAncestorChildOf(e.OriginalSource as DependencyObject, flow);
        if (container is null) return;

        var index = flow.Children.IndexOf(container);
        if (index < 0 || index == flow.NearestIndex) return;

        CenterCarousel(index, immediate: false);
        e.Handled = true;
    }

    /// <summary>Walks up from a hit-test result to the direct child of <paramref name="parent"/>.</summary>
    private static UIElement? FindAncestorChildOf(DependencyObject? node, Panel parent)
    {
        while (node is not null)
        {
            var p = VisualTreeHelper.GetParent(node);
            if (ReferenceEquals(p, parent)) return node as UIElement;
            node = p;
        }
        return null;
    }

    private void OnCarouselWheel(object sender, MouseWheelEventArgs e)
    {
        var flow = Flow;
        if (flow is null) return;

        // Proportional, and deliberately not rate-limited. Throttling wheel input was what made
        // scrolling hard through the library feel like it was hanging: most of the scrolling was
        // being discarded, and every notch that did survive restarted the glide from a standstill.
        // One notch is one card; a trackpad's smaller deltas move it a fraction of a card.
        flow.ScrollBy(-e.Delta / 120.0, immediate: !_settings.Animations);
        e.Handled = true;
    }

    private void UpdateCarouselReadout() => ShowCarouselIndex(Flow?.NearestIndex ?? 0);

    private void ShowCarouselIndex(int index)
    {
        var count = _library.Items.Count;
        CarouselIndex.Text = count == 0 ? "-- / --" : $"{Math.Clamp(index + 1, 1, count):D2} / {count:D2}";
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var found = FindDescendant<T>(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    private static TimelapseItem? ItemFrom(object sender) =>
        (sender as FrameworkElement)?.Tag as TimelapseItem;

    // ══════════════════════ batch downscale ══════════════════════

    private CancellationTokenSource? _batchCts;

    /// <summary>Set while the confirm dialog is up, before _batchCts exists.</summary>
    private bool _batchStarting;

    private List<TimelapseItem> SelectedItems() => _library.Items.Where(i => i.IsSelected).ToList();

    private void OnSelectionChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(UpdateBatchBar); return; }
        UpdateBatchBar();
    }

    private void UpdateBatchBar()
    {
        var selected = SelectedItems();
        var show = selected.Count > 0;

        if (show && BatchBar.Visibility != Visibility.Visible)
        {
            BatchBar.Visibility = Visibility.Visible;
            if (_settings.Animations) Motion.Enter(BatchBar, 0, -8);
        }
        else if (!show)
        {
            BatchBar.Visibility = Visibility.Collapsed;
            return;
        }

        SelectionCountText.Text = selected.Count == 1 ? "1 SELECTED" : $"{selected.Count} SELECTED";

        var target = DownscaleCombo.SelectedItem as DownscaleTarget ?? VideoTranscoder.Targets[1];
        long currentBytes = 0;
        double projected = 0;
        var eligible = 0;

        foreach (var item in selected)
        {
            var m = item.Manifest;
            currentBytes += m.VideoBytes;
            if (m.Width <= target.Width && m.Height <= target.Height) { projected += m.VideoBytes; continue; }
            eligible++;
            // Fewer pixels also means less noise survives, so the saving beats the pixel ratio.
            // Same exponent the capture-size estimator was fitted with; checked against real
            // downscales of a 1080p night, which came out within a percent at every size.
            var ratio = target.PixelFractionOf(m.Width, m.Height);
            projected += m.VideoBytes * Math.Pow(ratio, CapturePlan.ResolutionExponent);
        }

        var saved = (long)Math.Max(0, currentBytes - projected);
        SelectionEstimateText.Text = eligible == 0
            ? $"ALL SELECTED ARE ALREADY {target.Width}×{target.Height} OR SMALLER."
            : $"{Format.Bytes(currentBytes)} → ≈ {Format.Bytes((long)projected)} · SAVES ≈ {Format.Bytes(saved)}";

        DownscaleButton.IsEnabled = eligible > 0 && _batchCts is null;
    }

    private void OnDownscaleTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DownscaleCombo.SelectedItem is not DownscaleTarget t) return;
        _settings.Video.DownscaleWidth = t.Width;
        _settings.Video.DownscaleHeight = t.Height;
        _settings.Save();
        UpdateBatchBar();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        var all = _library.Items.ToList();
        var makeSelected = all.Any(i => !i.IsSelected);
        foreach (var i in all) i.IsSelected = makeSelected;
        UpdateBatchBar();
    }

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        foreach (var i in _library.Items.ToList()) i.IsSelected = false;
        UpdateBatchBar();
    }

    private async void OnDownscaleSelected(object sender, RoutedEventArgs e)
    {
        // The confirm below is modal, so a second click cannot normally get in — but the guard
        // has to be set before it, not after, or two batches could be started against the same
        // files and race over the same output paths.
        if (_batchCts is not null || _batchStarting) return;

        var items = SelectedItems();
        if (items.Count == 0) return;

        DownscaleTarget target;
        string? ffmpeg;
        _batchStarting = true;
        try
        {
            if (_engine.IsRecording)
            {
                MessageBox.Show(this,
                    "A timelapse is recording right now. Re-encoding uses every core and could make the " +
                    "camera drop frames, so downscaling waits until the session is finished.",
                    "Recording in progress", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            target = DownscaleCombo.SelectedItem as DownscaleTarget ?? VideoTranscoder.Targets[1];
            ffmpeg = FfmpegEncoder.Locate(_settings.FfmpegPath);
            if (ffmpeg is null)
            {
                MessageBox.Show(this, "ffmpeg.exe was not found — set its path in Config.",
                    "Encoder missing", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!Dialogs.Confirm(this, "Downscale",
                    $"Downscale {items.Count} timelapse{(items.Count == 1 ? "" : "s")} to " +
                    $"{target.Width} × {target.Height}?\n\n" +
                    "This replaces each video in place and cannot be undone — the original " +
                    "resolution cannot be recovered afterwards.",
                    confirmText: "DOWNSCALE", danger: true))
                return;

            _batchCts = new CancellationTokenSource();
        }
        finally { _batchStarting = false; }

        BatchProgressBar.Visibility = Visibility.Visible;
        if (_settings.Animations) Motion.Enter(BatchProgressBar, 0, -8);
        DownscaleButton.IsEnabled = false;
        var errors = new List<string>();

        var progress = new Progress<TranscodeProgress>(p =>
        {
            if (p.Finished)
            {
                BatchStatusText.Text = $"DONE — {Format.Bytes(p.BytesSaved)} SAVED";
                BatchProgress.Value = 1;
                return;
            }
            if (p.Error is not null) errors.Add($"{p.Title}: {p.Error}");
            BatchStatusText.Text = $"DOWNSCALING {p.ItemIndex + 1}/{p.ItemCount} — {p.Title.ToUpperInvariant()}";
            BatchProgress.Value = (p.ItemIndex + p.Fraction) / Math.Max(1, p.ItemCount);
        });

        try
        {
            await new VideoTranscoder(ffmpeg)
                .RunAsync(items, target, _settings.Video.DownscaleCrf, progress, _batchCts.Token);

            foreach (var i in items) i.NotifyVideoChanged();
            RefreshLibrary();

            if (errors.Count > 0)
                MessageBox.Show(this, "Some items were skipped:\n\n" + string.Join("\n", errors),
                    "Downscale finished with skips", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            BatchStatusText.Text = "CANCELLED — CURRENT VIDEO LEFT UNTOUCHED";
            RefreshLibrary();
        }
        catch (Exception ex)
        {
            App.Log(ex, "Downscale");
            MessageBox.Show(this, ex.Message, "Downscale failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _batchCts?.Dispose();
            _batchCts = null;
            UpdateBatchBar();
            await Task.Delay(2500);
            if (_batchCts is null) BatchProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCancelBatch(object sender, RoutedEventArgs e) => _batchCts?.Cancel();

    // ══════════════════════ housekeeping ══════════════════════

    private Housekeeper? _housekeeper;
    private CancellationTokenSource? _housekeepingCts;

    /// <summary>
    /// Runs the auto-downscale sweep if one is due. Called a little after startup and again when
    /// a night finishes, which between them covers a machine left running for months and one that
    /// is only ever on overnight.
    /// </summary>
    private async void RunHousekeepingIfDue()
    {
        _housekeeper ??= new Housekeeper(_settings, () => _engine.IsRecording);
        if (!_housekeeper.IsDue(DateTime.Now)) return;
        if (_housekeepingCts is not null) return;

        _housekeepingCts = new CancellationTokenSource();
        try
        {
            var result = await _housekeeper.RunAsync(_library.Items.ToList(), DateTime.Now,
                _housekeepingCts.Token);
            _settings.Save();

            if (result.Downscaled > 0)
            {
                RefreshLibrary();
                UpdateStorageSummary();
            }
            if (result.Downscaled > 0 || result.Error is not null)
                App.Note($"considered {result.Considered}, downscaled {result.Downscaled}, " +
                         $"saved {Format.Bytes(result.BytesSaved)}" +
                         $"{(result.Error is null ? "" : $" — {result.Error}")}", "Housekeeping");

            UpdateHousekeepingHint();
        }
        catch (Exception ex) { App.Log(ex, "Housekeeping"); }
        finally
        {
            _housekeepingCts?.Dispose();
            _housekeepingCts = null;
        }
    }

    // ══════════════════════ library item actions ══════════════════════

    private void OnOpenLibraryFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.TimelapseRoot);
        OpenInShell(_settings.TimelapseRoot);
    }

    private void OnOpenItemFolder(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is { } item) OpenInShell(item.FolderPath);
    }

    private void OnRenameItem(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item) return;
        var name = Dialogs.Prompt(this, "Rename timelapse", "Name", item.Title);
        if (string.IsNullOrWhiteSpace(name)) return;

        if (_library.Rename(item, name, out var error)) RefreshLibrary();
        else MessageBox.Show(this, error, "Could not rename", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnDeleteItem(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item) return;

        if (!Dialogs.Confirm(this, "Delete timelapse",
                $"Delete “{item.Title}” and its video permanently?\n\n{item.FolderPath}",
                confirmText: "DELETE", danger: true))
            return;

        // By folder, not video: either of a night's two videos may be the one playing.
        if (_pendingVideoPath is not null && _playingItem is not null &&
            item.FolderPath.Equals(_playingItem.FolderPath, StringComparison.OrdinalIgnoreCase))
            ClosePlayer();

        if (_library.Delete(item, out var error)) RefreshLibrary();
        else MessageBox.Show(this, error, "Could not delete", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void OpenInShell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { App.Log(ex, "OpenInShell"); }
    }

    // ══════════════════════ player ══════════════════════

    /// <summary>
    /// Playback decodes through ffmpeg into a bitmap this window owns — see <see cref="FramePlayer"/>
    /// for why, in short: MediaElement is a shell over Windows Media Player, which is not installed
    /// on this machine and is optional on Windows 11 generally. Drawing the frames ourselves also
    /// means the player is an ordinary plate in the page rather than a black rectangle on top of it.
    /// </summary>
    private FramePlayer? _player;
    private WriteableBitmap? _playerBitmap;
    private long _playerRendered = -1;
    private string? _pendingVideoPath;
    private bool _playerDriving;

    /// <summary>Aspect of the clip on screen, for sizing the frame plate to it.</summary>
    private double _playerAspect = 16.0 / 9.0;

    /// <summary>Card metrics for the two library modes, as the carousel lays them out.</summary>
    private const double FullCardWidth = 470, FullCardHeight = 440;
    private const double CompactCardWidth = 440, CompactCardHeight = 92;

    // ── the card transform ──────────────────────────────────────────────────
    //
    // Four animated numbers drive the whole change of library mode, and every card follows them
    // by binding rather than being touched individually — the same trick the carousel uses to
    // move fourteen cards from one animated double.
    //
    // The box animates size while the two card bodies cross-dissolve *inside* it. That is what
    // makes it read as a card changing shape rather than one card being replaced by another: the
    // outline never jumps, it travels, and the detail resolves away over the top of it. A plain
    // crossfade had nothing continuous for the eye to follow, which is exactly what looked wrong.

    public static readonly DependencyProperty CardBoxWidthProperty = DependencyProperty.Register(
        nameof(CardBoxWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(FullCardWidth));

    public static readonly DependencyProperty CardBoxHeightProperty = DependencyProperty.Register(
        nameof(CardBoxHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(FullCardHeight));

    /// <summary>Opacity of the detailed card body. 1 at full size, 0 when compact.</summary>
    public static readonly DependencyProperty FullBlendProperty = DependencyProperty.Register(
        nameof(FullBlend), typeof(double), typeof(MainWindow), new PropertyMetadata(1.0));

    public static readonly DependencyProperty CompactBlendProperty = DependencyProperty.Register(
        nameof(CompactBlend), typeof(double), typeof(MainWindow), new PropertyMetadata(0.0));

    /// <summary>
    /// Which body takes clicks. An element at zero opacity is still hit-testable in WPF, so
    /// without these the invisible card would keep swallowing presses meant for the visible one.
    /// Both are false mid-transform, which also stops a click landing on a card that is still moving.
    /// </summary>
    public static readonly DependencyProperty FullActiveProperty = DependencyProperty.Register(
        nameof(FullActive), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));

    public static readonly DependencyProperty CompactActiveProperty = DependencyProperty.Register(
        nameof(CompactActive), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public double CardBoxWidth { get => (double)GetValue(CardBoxWidthProperty); set => SetValue(CardBoxWidthProperty, value); }
    public double CardBoxHeight { get => (double)GetValue(CardBoxHeightProperty); set => SetValue(CardBoxHeightProperty, value); }
    public double FullBlend { get => (double)GetValue(FullBlendProperty); set => SetValue(FullBlendProperty, value); }
    public double CompactBlend { get => (double)GetValue(CompactBlendProperty); set => SetValue(CompactBlendProperty, value); }
    public bool FullActive { get => (bool)GetValue(FullActiveProperty); set => SetValue(FullActiveProperty, value); }
    public bool CompactActive { get => (bool)GetValue(CompactActiveProperty); set => SetValue(CompactActiveProperty, value); }

    /// <summary>Caps the compact library so a long grid scrolls instead of pushing the player out.</summary>
    private const double CompactBandHeight = 210;

    private bool _libraryCompact;

    private void OnPlayTimelapse(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is { } item) PlayItem(item);
    }

    /// <summary>The night the player has open, so its dots can swap the video in place.</summary>
    private TimelapseItem? _playingItem;

    private void OnShowOriginal(object sender, RoutedEventArgs e) => ShowVariant(sender, marked: false);
    private void OnShowMarked(object sender, RoutedEventArgs e) => ShowVariant(sender, marked: true);

    /// <summary>
    /// Switches a night between its clean video and its marked copy. If that night is playing, the
    /// other video takes over at the same moment, paused or playing as it was.
    /// </summary>
    private void ShowVariant(object sender, bool marked)
    {
        if (ItemFrom(sender) is not { } item || item.ShowMarked == marked) return;
        item.ShowMarked = marked;
        if (ReferenceEquals(item, _playingItem) && _player is { IsOpen: true } player
            && PlayerPanel.Visibility == Visibility.Visible)
            PlayItem(item, player.Progress, player.IsPlaying);
    }

    private void PlayItem(TimelapseItem item, double startAt = 0, bool play = true)
    {
        var path = item.PlayPath;
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "The video file is missing from this folder.", "Nothing to play",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ffmpeg = FfmpegEncoder.Locate(_settings.FfmpegPath);
        if (ffmpeg is null)
        {
            // No decoder, so no in-app playback. Hand it to whatever plays MP4 instead of
            // pretending the button did nothing.
            OpenInShell(path);
            return;
        }

        var m = item.Manifest;
        try
        {
            _player ??= new FramePlayer(ffmpeg);
            _player.Speed = Selected(SpeedCombo, 1.0);
            _player.Open(path, m.Width, m.Height, m.FrameCount, m.Fps);
            if (startAt > 0) _player.SeekTo(startAt);
            if (!play) _player.Pause();
        }
        catch (Exception ex)
        {
            App.Log(ex, "OpenPlayer");
            OpenInShell(path);
            return;
        }

        _playingItem = item;
        _pendingVideoPath = path;
        // The bitmap is kept between clips and only rebuilt when the decode size changes, so
        // flicking through the library does not allocate a new one per night.
        _playerRendered = -1;
        _playerAspect = m.Width > 0 && m.Height > 0 ? m.Width / (double)m.Height : 16.0 / 9.0;
        PlayerStatus.Text = "DECODING…";
        PlayerTitle.Text = item.Title.ToUpperInvariant();
        PlayerCode.Text = $"{m.FrameCount:N0}F · {m.Fps:0}FPS" + (item.ShowMarked ? " · MARKED" : "");
        PlayPauseButton.Content = play ? "❚❚" : "▶";
        PlayerSeek.Value = startAt;

        OpenPlayerStage();
        StartPlayerRendering();
    }

    /// <summary>
    /// Switches the library between its full cards and the compact ones it wears while the player
    /// is open, and hands the freed height to the player's row.
    ///
    /// Not a scaled-down version of the same card: a card shrunk far enough to leave the player
    /// room is a card you cannot read, which defeats the point of keeping the library on screen.
    /// The compact card keeps what identifies a night and what you would click, and drops the
    /// rest. The index row underneath is left out of it entirely — it is a control rather than
    /// content, so it stays the size it has always been and simply ends up at the bottom of a
    /// shorter library.
    /// </summary>
    /// <summary>
    /// Changes the library between its full cards and its compact ones by animating the card box
    /// from one shape to the other while the two bodies cross-dissolve inside it.
    ///
    /// The library's row is <c>Auto</c> throughout, so as the box shrinks the row shrinks with it
    /// and the player's row grows to match — the whole rearrangement falls out of the one size
    /// animation rather than being staged. Nothing is scaled: the cards are really that size at
    /// every frame, so the text stays sharp the whole way down instead of being a shrunken bitmap.
    /// </summary>
    private void SetLibraryCompact(bool compact, Action? then = null)
    {
        if (_libraryCompact == compact) { then?.Invoke(); return; }
        _libraryCompact = compact;

        // Nothing takes a click while it is moving.
        FullActive = false;
        CompactActive = false;

        void Settle()
        {
            FullActive = !compact;
            CompactActive = compact;
            // The fan's spacing changed with the box; put the focused night back under the centre.
            CenterCarousel(Flow?.TargetIndex ?? 0, immediate: true);
            then?.Invoke();
        }

        if (!_settings.Animations)
        {
            CardBoxWidth = compact ? CompactCardWidth : FullCardWidth;
            CardBoxHeight = compact ? CompactCardHeight : FullCardHeight;
            FullBlend = compact ? 0 : 1;
            CompactBlend = compact ? 1 : 0;
            Settle();
            return;
        }

        var box = new Duration(TimeSpan.FromMilliseconds(460));
        Animate(CardBoxWidthProperty, compact ? CompactCardWidth : FullCardWidth, box);
        Animate(CardBoxHeightProperty, compact ? CompactCardHeight : FullCardHeight, box, Settle);

        // The detail resolves away early on the way down and arrives late on the way back, so the
        // body you are reading is always the one the box is closest to fitting.
        var dissolve = new Duration(TimeSpan.FromMilliseconds(260));
        Animate(FullBlendProperty, compact ? 0 : 1, dissolve);
        Animate(CompactBlendProperty, compact ? 1 : 0, dissolve,
            beginAt: TimeSpan.FromMilliseconds(compact ? 160 : 0));
    }

    private void Animate(DependencyProperty property, double to, Duration duration,
        Action? onDone = null, TimeSpan? beginAt = null, UIElement? owner = null)
    {
        var anim = new DoubleAnimation(to, duration)
        {
            EasingFunction = Motion.EaseInOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        // Only when a delay is actually wanted: BeginTime defaults to zero, but assigning null to
        // it means "never begins", so passing the nullable straight through silently stops the
        // animation from ever running.
        if (beginAt is { } delay) anim.BeginTime = delay;

        if (onDone is not null) anim.Completed += (_, _) => onDone();
        (owner ?? (UIElement)this).BeginAnimation(property, anim);
    }

    /// <summary>
    /// How much of the stage the compact library needs: the card box the carousel lays out at,
    /// plus its index row and margins. The player takes everything else.
    /// </summary>
    private double CompactLibraryHeight => CompactCardHeight * 1.25 + 78;

    /// <summary>All of the stage except what the compact library strip needs.</summary>
    private double PlayerTargetHeight =>
        Math.Max(220, LibraryStage.ActualHeight - CompactLibraryHeight - 10);

    /// <summary>True while the player is opening or closing, when its height is being animated.</summary>
    private bool _playerTransition;

    /// <summary>
    /// Keeps an open player filling the stage when the window is resized or maximised.
    ///
    /// Its height is an absolute number rather than a proportion — that is what makes the open
    /// and close animations smooth — so it has to be recomputed when the stage changes size, or
    /// the player keeps the height it was given when it opened and leaves the new space empty.
    /// </summary>
    private void SyncPlayerHeightToStage()
    {
        if (PlayerPanel.Visibility != Visibility.Visible || _playerTransition) return;

        var target = PlayerTargetHeight;
        if (Math.Abs(PlayerPanel.Height - target) < 0.5) return;

        // A held animation outranks a local value, so the open animation's hold has to be
        // released before the new height will take.
        PlayerPanel.BeginAnimation(HeightProperty, null);
        PlayerPanel.Height = target;
        FitPlayerFrame();
    }

    private void OpenPlayerStage()
    {
        PlayerPanel.Visibility = Visibility.Visible;
        if (double.IsNaN(PlayerPanel.Height)) PlayerPanel.Height = 0;
        var target = PlayerTargetHeight;

        SetLibraryCompact(true);

        if (!_settings.Animations)
        {
            PlayerPanel.Height = target;
            StageSlide.Y = 0;
            PlayerSlide.Y = 0;
            PlayerPanel.Opacity = 1;
            return;
        }

        // The player's height is what drives the whole rearrangement: the library's star row
        // gives up exactly as much as the player takes, frame by frame, so there is nothing left
        // to reconcile when the animation ends.
        _playerTransition = true;
        Animate(HeightProperty, target, new Duration(TimeSpan.FromMilliseconds(460)),
            onDone: () => _playerTransition = false, owner: PlayerPanel);
        Motion.SlideTo(PlayerSlide, 0, Motion.Slow);
        Motion.FadeTo(PlayerPanel, 1, Motion.Normal);
    }

    private void OnClosePlayer(object sender, RoutedEventArgs e) => ClosePlayer();

    private void ClosePlayer()
    {
        if (PlayerPanel.Visibility != Visibility.Visible) return;

        StopPlayerRendering();
        _player?.Close();
        _pendingVideoPath = null;
        _playingItem = null;

        SetLibraryCompact(false);

        if (!_settings.Animations)
        {
            PlayerPanel.Height = 0;
            StageSlide.Y = 0;
            PlayerPanel.Opacity = 0;
            PlayerPanel.Visibility = Visibility.Collapsed;
            return;
        }

        // Height back to nothing over the same span the cards take to grow, so the library
        // reclaims the stage continuously instead of being handed the rest at the end.
        _playerTransition = true;
        Animate(HeightProperty, 0, new Duration(TimeSpan.FromMilliseconds(460)),
            onDone: () =>
            {
                PlayerPanel.Visibility = Visibility.Collapsed;
                PlayerPanel.Height = 0;
                _playerTransition = false;
            },
            owner: PlayerPanel);
        Motion.SlideTo(PlayerSlide, -28, Motion.Normal);
        Motion.FadeTo(PlayerPanel, 0, Motion.Slow);
    }

    /// <summary>
    /// Frames are pulled on the render tick rather than a timer, so a frame is blitted at most
    /// once per displayed frame, and the clock stops dead when playback does.
    /// </summary>
    private void StartPlayerRendering()
    {
        if (_playerDriving) return;
        _playerDriving = true;
        CompositionTarget.Rendering += OnPlayerFrame;
    }

    private void StopPlayerRendering()
    {
        if (!_playerDriving) return;
        _playerDriving = false;
        CompositionTarget.Rendering -= OnPlayerFrame;
    }

    private void OnPlayerFrame(object? sender, EventArgs e)
    {
        var player = _player;
        if (player is null || !player.IsOpen) return;

        if (_playerBitmap is null || _playerBitmap.PixelWidth != player.Width)
        {
            if (player.Width <= 0 || player.Height <= 0) return;
            _playerBitmap = new WriteableBitmap(player.Width, player.Height, 96, 96, PixelFormats.Bgr24, null);
            PlayerImage.Source = _playerBitmap;
        }

        var rect = new Int32Rect(0, 0, player.Width, player.Height);
        var stride = player.Width * 3;
        if (player.CopyLatestFrame(_playerRendered, buffer => _playerBitmap.WritePixels(rect, buffer, stride, 0)))
        {
            _playerRendered = player.FrameSequence;
            if (PlayerStatus.Text.Length > 0) PlayerStatus.Text = string.Empty;
        }

        UpdatePlayerProgress();
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_player is null || !_player.IsOpen) return;
        _player.TogglePlay();
        PlayPauseButton.Content = _player.IsPlaying ? "❚❚" : "▶";
    }

    private void UpdatePlayerProgress()
    {
        var player = _player;
        if (player is null || !player.IsOpen || _seeking) return;
        PlayerSeek.Value = player.Progress;
        PlayerTime.Text = $"{player.Position:mm\\:ss} / {player.Duration:mm\\:ss}";
    }

    private void OnSeekStart(object sender, DragStartedEventArgs e) => _seeking = true;

    private void OnSeekEnd(object sender, DragCompletedEventArgs e)
    {
        _seeking = false;
        _player?.SeekTo(PlayerSeek.Value);
    }

    /// <summary>Clicking the track jumps there; dragging is handled on release.</summary>
    private void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seeking || _player is null || !_player.IsOpen) return;
        if (Math.Abs(e.NewValue - _player.Progress) > 0.02) _player.SeekTo(e.NewValue);
    }

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _player is null) return;
        _player.Speed = Selected(SpeedCombo, 1.0);
    }

    /// <summary>
    /// Any change in either direction, not just height: a window that gets wider without getting
    /// taller still changes how much room the picture has to spread into.
    /// </summary>
    private void OnPlayerFrameResized(object sender, SizeChangedEventArgs e) => FitPlayerFrame();

    /// <summary>
    /// Sizes the frame plate to the clip's aspect. The row gives it its height; this gives it the
    /// matching width, so the picture meets the chamfered edge on all four sides instead of
    /// sitting in a letterbox with a third of the plate black.
    /// </summary>
    private void FitPlayerFrame()
    {
        var available = PlayerPanel.ActualWidth - PlayerPanel.Padding.Left - PlayerPanel.Padding.Right;
        if (PlayerFrame.ActualHeight <= 0 || available <= 0) return;

        // Only when it actually differs — setting Width raises SizeChanged again, and writing the
        // same value back every time would keep the layout pass bouncing.
        var width = Math.Min(available, PlayerFrame.ActualHeight * _playerAspect);
        if (Math.Abs(PlayerFrame.Width - width) > 0.5) PlayerFrame.Width = width;
    }

    // ══════════════════════ settings ══════════════════════

    private void LoadAdvancedIntoUi()
    {
        var c = _settings.Camera;
        var st = _settings.Stretch;

        AeMinExpBox.Text = Num(c.AutoExposureMinSeconds);
        AeMaxExpBox.Text = Num(c.AutoExposureMaxSeconds);
        AeMinGainBox.Text = c.AutoExposureMinGain.ToString(CultureInfo.InvariantCulture);
        AeMaxGainBox.Text = c.AutoExposureMaxGain.ToString(CultureInfo.InvariantCulture);
        AeTargetBox.Text = Num(c.AutoExposureTarget);
        AeStepBox.Text = Num(c.AutoExposureMaxStep);

        ShadowClipBox.Text = Num(st.ShadowClip);
        ShadowDepthBox.Text = Num(st.ShadowDepth);
        SmoothingBox.Text = Num(st.SmoothingFrames);
        OffsetBox.Text = c.Offset.ToString(CultureInfo.InvariantCulture);
        WbRedBox.Text = c.WhiteBalanceRed.ToString(CultureInfo.InvariantCulture);
        WbBlueBox.Text = c.WhiteBalanceBlue.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads the advanced boxes back into settings. Anything unparseable or out of range keeps
    /// its previous value and the box is rewritten, so a typo can never persist a broken config.
    /// </summary>
    private void OnAdvancedChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var c = _settings.Camera;
        var st = _settings.Stretch;

        c.AutoExposureMinSeconds = ReadDouble(AeMinExpBox, c.AutoExposureMinSeconds, 0.00005, 600);
        c.AutoExposureMaxSeconds = ReadDouble(AeMaxExpBox, c.AutoExposureMaxSeconds, c.AutoExposureMinSeconds, 600);
        c.AutoExposureMinGain = ReadInt(AeMinGainBox, c.AutoExposureMinGain, 0, 600);
        c.AutoExposureMaxGain = ReadInt(AeMaxGainBox, c.AutoExposureMaxGain, c.AutoExposureMinGain, 600);
        c.AutoExposureTarget = ReadDouble(AeTargetBox, c.AutoExposureTarget, 0.01, 0.6);
        c.AutoExposureMaxStep = ReadDouble(AeStepBox, c.AutoExposureMaxStep, 0.01, 1.0);

        st.ShadowClip = ReadDouble(ShadowClipBox, st.ShadowClip, 0, 20);
        st.ShadowDepth = ReadDouble(ShadowDepthBox, st.ShadowDepth, 0, 0.95);
        st.SmoothingFrames = ReadDouble(SmoothingBox, st.SmoothingFrames, 1, 500);
        c.Offset = ReadInt(OffsetBox, c.Offset, 0, 600);
        c.WhiteBalanceRed = ReadInt(WbRedBox, c.WhiteBalanceRed, 0, 100);
        c.WhiteBalanceBlue = ReadInt(WbBlueBox, c.WhiteBalanceBlue, 0, 100);

        LoadAdvancedIntoUi();
        UpdateAutoExposureHint();
        _settings.Save();
    }

    private static double ReadDouble(TextBox box, double fallback, double min, double max) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, min, max) : fallback;

    private static int ReadInt(TextBox box, int fallback, int min, int max) =>
        int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, min, max) : fallback;

    private void OnRestoreDefaults(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm(this, "Restore defaults",
                "Reset every setting to its default?\n\nYour timelapses and library folder are not affected.",
                confirmText: "RESTORE"))
            return;

        var fresh = new AppSettings();
        _settings.Camera = fresh.Camera;
        _settings.Stretch = fresh.Stretch;
        _settings.Video = fresh.Video;
        _settings.Session = fresh.Session;
        _settings.Animations = fresh.Animations;
        _settings.ViewportChrome = fresh.ViewportChrome;
        _settings.LibraryView = fresh.LibraryView;

        _loading = true;
        LoadSettingsIntoUi();
        _loading = false;

        _scheduler.Rearm();
        UpdatePlan();
        ApplyLibraryView();
        
        _settings.Save();
    }

    private void OnOpenSettingsFile(object sender, RoutedEventArgs e)
    {
        _settings.Save();
        OpenInShell(AppSettings.SettingsPath);
    }

    private void OnBrowseLibrary(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where timelapses are stored",
            InitialDirectory = Directory.Exists(_settings.LibraryRoot) ? _settings.LibraryRoot : string.Empty
        };
        if (dialog.ShowDialog(this) != true) return;
        LibraryRootBox.Text = dialog.FolderName;
        ApplyLibraryRoot();
    }

    private void OnLibraryRootChanged(object sender, RoutedEventArgs e) => ApplyLibraryRoot();

    private void ApplyLibraryRoot()
    {
        if (_loading) return;
        var path = LibraryRootBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path)) path = AppSettings.DefaultLibraryRoot();

        if (_engine.IsRecording)
        {
            MessageBox.Show(this, "The library folder cannot be moved while a recording is running.",
                "Recording in progress", MessageBoxButton.OK, MessageBoxImage.Information);
            LibraryRootBox.Text = _settings.LibraryRoot;
            return;
        }

        _settings.LibraryRoot = path;
        _settings.Save();
        RefreshLibrary();
        UpdateStatusBar();
    }

    private void OnMinFreeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (double.TryParse(MinFreeDiskBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) && gb >= 0)
            _settings.Session.MinFreeDiskGb = gb;
        MinFreeDiskBox.Text = _settings.Session.MinFreeDiskGb.ToString("0.#", CultureInfo.InvariantCulture);
        _settings.Save();
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Video.Preset = Selected(PresetCombo, "medium");
        _settings.Save();
    }

    private void OnKeepRawChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Session.KeepRawFrames = KeepRawCheck.IsChecked == true;
        _settings.Save();
    }

    private void OnBrowseFfmpeg(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Locate ffmpeg.exe",
            Filter = "ffmpeg|ffmpeg.exe|Programs (*.exe)|*.exe"
        };
        if (dialog.ShowDialog(this) != true) return;
        FfmpegPathBox.Text = dialog.FileName;
        ApplyFfmpegPath();
    }

    private void OnFfmpegPathChanged(object sender, RoutedEventArgs e) => ApplyFfmpegPath();

    private void ApplyFfmpegPath()
    {
        if (_loading) return;
        var text = FfmpegPathBox.Text.Trim();
        _settings.FfmpegPath = string.IsNullOrWhiteSpace(text) ? null : text;
        _settings.Save();
        UpdateFfmpegStatus();
        UpdateDiagnostics();
    }

    private void UpdateFfmpegStatus()
    {
        var resolved = FfmpegEncoder.Locate(_settings.FfmpegPath);
        FfmpegStatus.Text = resolved is null
            ? "NOT FOUND — PUT FFMPEG.EXE IN THE TOOLS FOLDER NEXT TO PIERCAM.EXE, OR BROWSE TO IT ABOVE."
            : $"USING {resolved.ToUpperInvariant()}";
        FfmpegStatus.Foreground = (Brush)FindResource(resolved is null ? "Danger" : "Signal");
    }

    private void UpdateDiagnostics()
    {
        var facts = new List<string>
        {
            // From the assembly, never a literal: this is the line people paste into a bug
            // report, so a stale hand-typed number here is worse than no number at all.
            $"PIERCAM            {VersionText}",
            $"THEME              {ThemeManager.Current.Name}",
            $"ASI SDK            {(AsiSdk.ResolvedPath is null ? "not loaded" : AsiSdk.GetSdkVersion())}",
            $"CAMERAS DETECTED   {_cameras.Count}",
        };

        if (_engine.ConnectedCamera is { } cam)
        {
            facts.Add($"CONNECTED          {cam.Name}");
            facts.Add($"SENSOR             {cam.MaxWidth}×{cam.MaxHeight}, {cam.BitDepth}-bit, {cam.PixelSizeUm:0.##} µm");
            facts.Add($"COLOUR             {(cam.IsColor ? $"yes, Bayer {cam.Bayer}" : "mono")}");
        }

        var site = _settings.Site;
        if (site.IsSet)
            facts.Add($"SITE               {site.Latitude:0.####}, {site.Longitude:0.####}, {site.ElevationMetres:0} m");
        facts.Add($"ROOF               {_roof.Status.State} — {_roof.Status.Detail}");

        DiagnosticsText.Text = string.Join(Environment.NewLine, facts);

        DiagnosticsPaths.Text = string.Join(Environment.NewLine, new[]
        {
            $"ASICAMERA2  {AsiSdk.ResolvedPath ?? "not found"}",
            $"FFMPEG      {FfmpegEncoder.Locate(_settings.FfmpegPath) ?? "not found"}",
            $"LIBRARY     {_settings.LibraryRoot}",
            $"SETTINGS    {AppSettings.SettingsPath}",
            $"LOG         {App.CrashLogPath}",
        });

        AppearanceStamp.Text = $"PIERCAM · {ThemeManager.Current.Name.ToUpperInvariant()} · " +
                               $"{ThemeManager.Themes.Count} PALETTES";

        UpdateStorageSummary();
    }

    /// <summary>Fills the storage plate with what the library actually costs right now.</summary>
    private void UpdateStorageSummary()
    {
        var count = _library.Items.Count;
        var saved = _library.TotalEstimatedRawBytes - _library.TotalVideoBytes;

        var lines = new List<string>
        {
            count == 0 ? "LIBRARY EMPTY" : $"{count} TIMELAPSE{(count == 1 ? "" : "S")} · {Format.Bytes(_library.TotalVideoBytes)}"
        };
        if (saved > 0) lines.Add($"{Format.Bytes(saved)} LESS THAN KEEPING RAW FRAMES");

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_settings.LibraryRoot));
            if (root is not null)
            {
                var drive = new DriveInfo(root);
                lines.Add($"{Format.Bytes(drive.AvailableFreeSpace)} FREE ON {root.TrimEnd('\\')}");
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }

        StorageSummary.Text = string.Join(Environment.NewLine, lines);
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(App.CrashLogPath);
        if (dir is null) return;
        Directory.CreateDirectory(dir);
        OpenInShell(dir);
    }

    private void OnCopyDiagnostics(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(DiagnosticsText.Text); }
        catch (Exception ex) { App.Log(ex, "Clipboard"); }
    }

    // ══════════════════════ about ══════════════════════

    /// <summary>
    /// The tip jar. PierCam is given away whole, so this is named once and reached from exactly
    /// two quiet places: the wordmark, and a stamp at the foot of the diagnostics plate.
    /// </summary>
    private const string SupportUrl = "https://ko-fi.com/rkremeier";
    private const string SupportLabel = "KO-FI.COM/RKREMEIER";

    private static string VersionText
    {
        get
        {
            var v = typeof(MainWindow).Assembly.GetName().Version;
            return v is null ? "1.0" : $"{v.Major}.{v.Minor}";
        }
    }

    private void OnAbout(object sender, RoutedEventArgs e) =>
        Dialogs.About(this, VersionText, SupportLabel, () => OpenInShell(SupportUrl));

    private void OnSupport(object sender, MouseButtonEventArgs e) => OpenInShell(SupportUrl);

    // ══════════════════════ shutdown ══════════════════════

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_engine.IsRecording)
        {
            var answer = MessageBox.Show(this,
                "A timelapse is still recording. Close PierCam and finish the video now?",
                "Recording in progress", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }

        WindowPlacementService.Save(this, _settings.Placement);

        _renderTimer.Stop();
        _statusTimer.Stop();
        _scheduleTimer.Stop();
        _housekeepingCts?.Cancel();

        TimelapseItem.SelectionChanged -= OnSelectionChanged;
        _batchCts?.Cancel();
        StopPlayerRendering();
        _player?.Dispose();
        _player = null;

        // The marker first: once the engine no longer calls into it, it can shut down its threads.
        _engine.Marker = null;
        _engine.StopRecording("PierCam closed");
        _engine.Dispose();
        _marker.Dispose();
        _updates.Dispose();
        _settings.Save();
    }

    // ══════════════════════ updates ══════════════════════

    private void LoadUpdatesIntoUi()
    {
        UpdateAutoCheck.IsChecked = _settings.Updates.CheckAutomatically;
        UpdateAutoInstall.IsChecked = _settings.Updates.AutoInstallWhenIdle;
    }

    private void OnUpdateSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Updates.CheckAutomatically = UpdateAutoCheck.IsChecked == true;
        _settings.Updates.AutoInstallWhenIdle = UpdateAutoInstall.IsChecked == true;
        _settings.Save();
        _updates.Apply();
        UpdateUpdatesUi();
    }

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        UpdateCheckButton.IsEnabled = false;
        try { await _updates.CheckAsync(); }
        finally { UpdateCheckButton.IsEnabled = true; }
        UpdateUpdatesUi();
    }

    /// <summary>
    /// Downloads if it has not already, then hands over to the installer. The confirmation is
    /// worth having even though the button is disabled while recording: this closes the app, and
    /// on a machine someone is watching remotely that should not be a surprise.
    /// </summary>
    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_updates.Available is not { } release) return;
        if (!_updates.CanInstallNow(out var why))
        {
            MessageBox.Show(this, why, "Not now", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!Dialogs.Confirm(this, $"Install PierCam {release.Version.ToString(2)}",
                $"PierCam will download {Library.Format.Bytes(release.Bytes)}, close, install the new version and reopen.\n\n" +
                "The library and every setting are left alone.", confirmText: "INSTALL"))
            return;

        UpdateInstallButton.IsEnabled = false;
        try
        {
            if (_updates.DownloadedInstaller is null && !await _updates.DownloadAsync()) return;
            _updates.InstallAndRestart();
        }
        finally
        {
            UpdateInstallButton.IsEnabled = true;
            UpdateUpdatesUi();
        }
    }

    private void OnOpenReleasesPage(object sender, RoutedEventArgs e) =>
        OpenInShell(_updates.Available?.PageUrl ?? Update.UpdateService.ReleasesPage);

    private void OnStatusUpdateClicked(object sender, MouseButtonEventArgs e)
    {
        NavConfig.IsChecked = true;
        Dispatcher.BeginInvoke(() => ConfigPage.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    private void UpdateUpdatesUi()
    {
        UpdateHeadline.Text = _updates.Headline;
        UpdateDetail.Text = _updates.Detail;
        UpdateDetail.Visibility = _updates.Detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateAutoInstall.IsEnabled = _settings.Updates.CheckAutomatically && _updates.IsInstalled;

        var offer = _updates.Available;
        var installable = offer is not null && _updates.IsInstalled;
        UpdateInstallButton.Visibility = installable ? Visibility.Visible : Visibility.Collapsed;
        UpdateInstallButton.IsEnabled = installable && _updates.CanInstallNow(out _);
        UpdatePageButton.Visibility = offer is not null && !installable ? Visibility.Visible : Visibility.Collapsed;

        UpdateLed.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, _updates.Phase switch
        {
            Update.UpdatePhase.Available or Update.UpdatePhase.Ready or Update.UpdatePhase.Downloading => "Accent",
            Update.UpdatePhase.UpToDate => "Signal",
            Update.UpdatePhase.Failed => "Warn",
            _ => "TextFaint",
        });

        // The badge in the status bar is the only part of this that appears anywhere but Config,
        // and only when there is genuinely something newer.
        StatusUpdate.Text = offer is null ? string.Empty : $"UPDATE {offer.Version.ToString(2)}";
        StatusUpdate.Visibility = offer is null ? Visibility.Collapsed : Visibility.Visible;
        FitStatusBar();
    }

    // ══════════════════════ target marker ══════════════════════

    private void LoadMarkerIntoUi()
    {
        var m = _settings.TargetMarker;
        MarkerEnabledCheck.IsChecked = m.Enabled;
        MarkerBurnCheck.IsChecked = m.BurnIntoRecordings;
        MarkerKeepOriginalCheck.IsChecked = m.KeepUnmarkedOriginal;
        MarkerApiBox.Text = m.NinaApiUrl;
    }

    private void OnMarkerSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var m = _settings.TargetMarker;
        var wasEnabled = m.Enabled;
        var url = MarkerApiBox.Text.Trim();
        if (url.Length == 0) { url = new Models.TargetMarkerSettings().NinaApiUrl; MarkerApiBox.Text = url; }

        m.Enabled = MarkerEnabledCheck.IsChecked == true;
        m.BurnIntoRecordings = MarkerBurnCheck.IsChecked == true;
        m.KeepUnmarkedOriginal = MarkerKeepOriginalCheck.IsChecked == true;
        m.NinaApiUrl = url;
        _settings.Save();

        if (m.Enabled != wasEnabled) _marker.Apply();
        UpdateMarkerUi();
    }

    private void OnMarkerRecalibrate(object sender, RoutedEventArgs e)
    {
        _marker.Recalibrate();
        UpdateMarkerUi();
    }

    /// <summary>The marker is drawn in the palette's accent, so it matches the rest of the app.</summary>
    private void UpdateMarkerColor()
    {
        if (TryFindResource("Accent") is SolidColorBrush b) _marker.SetColor(b.Color.R, b.Color.G, b.Color.B);
    }

    private void UpdateMarkerUi()
    {
        var phase = _marker.Phase;
        MarkerHeadline.Text = _marker.Headline;
        MarkerDetail.Text = _marker.Detail;
        MarkerDetail.Visibility = string.IsNullOrEmpty(_marker.Detail) ? Visibility.Collapsed : Visibility.Visible;
        var telescope = _marker.TelescopeLine;
        MarkerTelescope.Text = telescope;
        MarkerTelescope.Visibility = telescope.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        MarkerLed.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, phase switch
        {
            Sky.MarkerPhase.Calibrated => "Signal",
            Sky.MarkerPhase.Searching or Sky.MarkerPhase.Calibrating or Sky.MarkerPhase.Collecting => "Accent",
            Sky.MarkerPhase.WaitingForNight or Sky.MarkerPhase.NeedsSite => "Warn",
            _ => "TextFaint",
        });
        var on = _settings.TargetMarker.Enabled;
        MarkerBurnCheck.IsEnabled = on;
        // Only means something with burn-in on. Like burn-in, it applies from the next recording.
        MarkerKeepOriginalCheck.IsEnabled = on && _settings.TargetMarker.BurnIntoRecordings;
        MarkerRecalibrateButton.IsEnabled = on && _settings.Site.IsSet
                                             && phase is not (Sky.MarkerPhase.Searching or Sky.MarkerPhase.Calibrating);
    }
}
