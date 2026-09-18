# PierCam

A dusk-to-dawn timelapse recorder for an observatory pier camera, for Windows.

Live view, scheduled all-night recording that encodes straight to video, and a library to watch
the results back. Built for ZWO ASI cameras — developed against an ASI662MC watching a pier
inside a roll-off-roof building.

## Why it exists

General-purpose capture software writes one image file per frame. A ten-hour night at a
15-second cadence is a few thousand PNGs and the best part of ten gigabytes, and the programs
that produce them tend to get heavy after a few days of uptime.

PierCam never writes those frames. Each exposure is debayered, stretched and pushed into an
H.264 encoder as it is captured. One file grows through the night. There is no point at which
the full frame set exists on disk, and no post-processing pass at the end.

Measured on a real 2,374-frame night at 1080p:

| Setting | Size | vs. raw frames |
|---|---:|---:|
| Raw PNG frames | 9.17 GB | — |
| CRF 20, no noise reduction | 677 MB | 14× smaller |
| **CRF 26 + medium noise reduction (default)** | **92.5 MB** | **101× smaller** |

Both video figures are real renders of those frames, not projections.

The numbers are far larger than a mostly-black sky suggests because **sensor noise differs in
every frame**, so the encoder cannot predict any of it and spends most of its bitrate there.
That is also why the noise reduction pays twice: it roughly halves the file *and* the result
looks better than the original, because what it removes is noise rather than detail.

The noise reduction is spatial-dominant on purpose. Temporal denoise averages a pixel against
the same pixel in earlier frames, and in a timelapse the sky rotates between frames — turned up,
it smears stars into short trails.

## Requirements

- Windows 10 or 11, 64-bit
- A ZWO ASI camera and ZWO's driver package. `ASICamera2.dll` is **not** bundled; it is located
  at runtime from ASIStudio, any SharpCap install, or next to the exe, so a ZWO driver update
  improves PierCam rather than breaking it. Install [ASIStudio](https://www.zwoastro.com/downloads/)
  if the Config page reports `ASI SDK  not loaded`.
- `ffmpeg.exe` with `libx264`, either in `tools\` beside the exe or on `PATH`. See
  [tools/README.md](tools/README.md).

Only one program may hold a camera at a time, so close other capture software first. Where
several ZWO cameras are attached, PierCam auto-connects only when the choice is unambiguous —
it will not take a mono imaging camera or guider away from an imaging suite.

## Installing

Download the release zip, unzip the whole folder, run `PierCam.exe`. Nothing else to install:
the .NET runtime is inside the executable. Keep `tools\ffmpeg.exe` beside it.

The build is unsigned, so Windows will warn about an unknown publisher.

## Building from source

Requires the .NET 8 SDK. If it is installed per-user rather than machine-wide:

```powershell
$env:DOTNET_ROOT = "$env:LocalAppData\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet build -c Release
```

`.\publish.ps1` produces a shareable self-contained drop in `dist\` — one executable, ffmpeg
beside it, and the third-party licence notices.

## Recording

### Following the sky rather than the clock

**Live view → Schedule → Dusk to dawn (automatic)** computes real astronomical twilight for the
configured site and records between them, so the window moves with the seasons without anyone
touching a time field. The threshold is selectable: astronomical (−18°, full dark, the default),
nautical (−12°), civil (−6°), or plain sunset-to-sunrise for the twilight colours.

Site coordinates can be entered by hand or imported from an N.I.N.A. profile, which is read
only and never modified.

A fixed schedule is also available (**Every night, automatically**, default 20:30 → 05:30). The
window may cross midnight; each night is tracked by the date it *started*, so stopping a session
by hand does not restart it tonight but does re-arm for tomorrow.

### The roof gate

**Live view → Schedule → Only record when roof is open** gates recording on an observatory roof
status file — the kind many shared sites publish on a mapped drive:

```
Z:\roof\<building>\RoofStatusFile.txt
```

containing a line like `2026-09-17 07:28:17PM CST Roof Status: OPEN`. PierCam only ever reads
it. **Config → Site & roof → Detect** searches mapped drives for candidates.

- **Roof never opens** — no video at all; the session never starts.
- **Roof opens at 02:00** — recording starts at 02:00. It need not have been open at dusk.
- **Roof shuts mid-night** — recording continues for a settable run-on (default five minutes),
  then holds. The status file is written once the roof has *finished* moving, so stopping the
  instant it reads CLOSED means the closing roof never appears in the video; the run-on is what
  puts it there.

After the run-on it holds rather than ends: frames stop, live view keeps working, and when the
roof reopens recording resumes into the *same* video. One file per night with a gap in it,
rather than a scatter of fragments. The run-on only ever *extends* a session — it cannot start
one, so a roof that shuts while nothing is recording still films nothing.

The file is read on a background thread, always, so that a hung SMB call can never reach the
capture loop.

**On file age:** these files are written when a roof *moves*, not on a heartbeat, so an
hours-old file is normal operation and a roof sitting open all night legitimately has a stale
timestamp. PierCam therefore believes the state regardless of age and only flags the file as
quiet in the readout. It reports *Unknown* only when the file is unreadable or older than a
configurable give-up threshold (default 12 hours), which means something is genuinely broken
rather than merely quiet.

### Exposure and gain

Defaults are 15 s at gain 300. Every value is a setting and is persisted; nothing is compiled in.

There are two independent automatic systems, and the distinction matters:

**Automatic brightness** happens *after* capture. Every frame's sky background is measured and
mapped to a chosen display level, with the colour channels equalised to cancel an orange
light-pollution cast. It changes how frames are rendered, never how they are exposed. In
**Smoothed** mode (the default, and the one for timelapses) the stretch is averaged over ~20
frames so the video follows the sky slowly instead of flickering.

**Automatic exposure** happens *during* capture and changes the camera. Two separate ramps,
because they want opposite things:

- **Live view** (on by default). An all-sky camera under an open roof runs from full daylight to
  a moonless sky — thousands to one — and no fixed preview exposure survives that range. This
  ramp is allowed to jump, because nobody watches the preview back frame by frame. It recovers
  from full saturation in about six adjustments.
- **Recording** (twilight ramp, off by default). Same idea, but it moves by at most 12% per
  frame, because in a finished video a sudden exposure change reads as a brightness step.

If the live view is ever a featureless grey field it is saturated, not broken; the viewport says
so explicitly and the status bar shows `CLIP %`.

Why the recording ramp is worth enabling — measured sky level through one night at a fixed
15 s / gain 300:

| Time | Median level | Pixels at full scale |
|---|---:|---:|
| 22:45 | 4.6% | 0.00% |
| 01:28 | 14.5% | 0.11% |
| 04:12 | 17.5% | 0.17% |
| **06:55** | **100%** | **100%** |
| 08:45 | 95% | 50% |

Through the dark hours a fixed exposure is ideal — only 0.1–0.2% of pixels clip, and those are
streetlights and bright stars. But a run that continues past dawn produces pure white frames
that nothing can recover. Either stop before dawn or let the exposure follow the sky; simulated
against a 10,000× dawn ramp the latter tracks down to 0.05 s at gain 0 with no saturated frames
and no step larger than 4.5%. It prefers lengthening exposure over raising gain, since longer
subs are cleaner.

### Interval

| Interval | Frames per 10 h night | Video at 30 fps |
|---|---:|---:|
| Continuous (15 s + readout) | 2,250 | 1m 15s |
| 30 seconds | 1,200 | 40s |
| **1 minute (default)** | **600** | **20s** |
| 4 minutes | 150 | 5s |

At a 1-minute interval with 15-second exposures the shutter is open a quarter of the time, so
star motion arrives in **visible steps** rather than flowing. Continuous shooting gives smooth
trails because each exposure starts where the last ended. If the finished video looks jumpy,
that is why, and a shorter interval fixes it.

Because frames go straight into the encoder, interval is about pacing rather than storage — a
full continuous night is around 90 MB rather than 9 GB.

## The library

**Carousel** stands the nights up like files on a shelf: the focused one square to the viewer
and clear of the pile, the rest turned hard away and packed tight. Arrow keys, the scroll wheel,
the ◄ ► buttons, or click any card. **Grid** shows everything at once. The choice is saved.

WPF has no perspective projection for 2D elements, so the turn is built from its two visible
consequences — horizontal foreshortening and a vertical shear. That reads as a plate swung on a
vertical axis and costs one matrix per card rather than a `Viewport3D` each.

Scrolling moves a target, never the shelf directly; the shelf chases it under a critically
damped spring. Input is proportional and never throttled, so a burst arrives as one continuous
movement instead of a queue of fixed-length animations each restarting from a standstill. The
spring is stepped by its closed-form solution rather than integrated, because frames here are
not evenly spaced and a stepwise integrator turns that into a glide that runs half again as
long, all of it added to the tail — the only part of the movement anyone watches.

### Playback

Press **Play** and the library becomes a compact strip along the bottom while the player comes
down over the space it leaves. The strip is not the same cards made small: a card shrunk far
enough to leave the player real room is a card nobody can read. The compact card keeps only what
identifies a night and what you would click — title, date, play length, and Play.

**Playback does not use MediaElement.** MediaElement is a shell over Windows Media Player, which
is an optional component and absent on plenty of Windows 11 installs, where it throws
`InvalidWmpVersionException` on construction. Instead frames are decoded through ffmpeg — already
a hard dependency, already spoken to over a pipe — and drawn into a `WriteableBitmap` the window
owns. That removes an unreliable dependency rather than adding one, and the picture sits inside a
chamfered plate like everything else rather than a black box the app does not control.

It is not expensive: ffmpeg decodes and scales a 2,374-frame night at about 360 fps, roughly six
times what playback needs. Frames are decoded straight to display size, buffers and the bitmap
are reused between clips, and the render hook stops dead when playback does.

### Shrinking timelapses

Tick **Select** on any cards, pick a size, press **Downscale**. Measured on the 92.5 MB night at
CRF 24:

| Size | Result | Share of original |
|---|---:|---:|
| 1920 × 1080 (as recorded) | 92.5 MB | — |
| 1280 × 720 | 27.1 MB | 29% |
| **960 × 540** | **11.6 MB** | **13%** |
| 854 × 480 | 8.4 MB | 9% |
| 640 × 360 | 4.0 MB | 4% |

The saving beats the pixel ratio every time — a quarter of the pixels gives an eighth of the
size — because downscaling averages neighbouring pixels together and removes most of the sensor
noise that was costing the bitrate.

It replaces the file in place and cannot be undone, so it asks first. The new file is only
written over the old one once the replacement has been encoded and checked, so an interruption
leaves the original intact. It refuses to run while recording.

**Config → Storage → Shrink old timelapses automatically** does the same unattended for anything
past a chosen age. Off by default, and three rules keep it from eating a library: it never
touches anything recent, it never runs while recording and re-checks between each file, and it
runs at most once a day and reports what it would do before doing it.

## Interface

Flat machined panels with chamfered corners, monospace type, hairline rules and registration
marks. Flat fills plus a fine grain — no gradients, no gloss. Panels that sit next to each other
keep square corners on the shared edge and cut only the exposed ones, so a row reads as one
assembly rather than a strip of separate boxes.

Where a seam jogs at 45°, **both** plates carry the shape: one has the cut, its neighbour the
matching tab, drawn into the first plate's cell with a negative margin. `ChamferPanel` does this
with per-corner `Cuts` and per-edge profiles (`RightEdge="80>0@110"` on one plate,
`LeftEdge="0>80@110"` on the other), and each plate's content is padded clear of its own cuts.

Nothing animates over the live image: no sweep, no scanline, no wash. What the viewport shows is
exactly the frame the camera returned. Only `Opacity` and `RenderTransform` are ever animated,
both composited off the layout path. Interface animation and the frame marks can be turned off
entirely without losing any function.

### Palettes

| | |
|---|---|
| **Dark** (default) | Near-black, cool panels, orange signal. |
| **Light** | Paper white, true-black hairlines, hot orange. Leans on line weight rather than fill. |
| **Night** | Red only, and dim. Rods are barely sensitive above ~620 nm, so a screen restricted to red does not undo twenty minutes of dark adaptation. Even "good" and "warning" stay inside the red band, separated by brightness rather than hue. |

Filled buttons are the one place a palette cannot share a rule, so each states its own fill and
lettering (`PrimaryFill`/`OnPrimary`, `DangerFill`/`OnDanger`). A mid red is the worst possible
background for text — nothing reads on it, dark or light — and going brighter would defeat the
point of the Night palette, so Night inverts instead: near-black plate, bright red rule and
lettering. Around 6.8:1 instead of 3.5:1, and less light thrown at the observer rather than more.

To add a palette, copy `Ui/Theme/Dark.xaml`, change the values, keep every key, and add one line
to `ThemeManager.Themes`. The picker, swatches and dropdown all build themselves from that list.
One rule when editing: **a style key must never collide with a palette key.** Styles are merged
after the palette, so a `Style` named `Glass` would shadow the `Glass` brush and every lookup for
it would return a Style — which is why panel styles carry a `-Panel` suffix.

## Settings and files

Everything is driven by `%AppData%\PierCam\settings.json`, written whenever a control changes.
Nothing is hardcoded, so a different lens, a darker site or a different camera is a matter of
changing numbers rather than rebuilding. The defaults suit a colour camera looking up inside a
dome at a light-polluted site.

| Setting | Where | Default | Change it if… |
|---|---|---|---|
| Exposure / gain | Live view | 15 s / 300 | different lens speed or sky darkness |
| Sky brightness | Live view | 0.18 | image looks too dark or washed out |
| Shadow clip / depth | Config → Image tuning | 2.8σ / 0.30 | blacks crushed, or sky looks milky |
| Smoothing frames | Config → Image tuning | 20 | video flickers, or lags the real sky |
| White balance R/B | Config → Image tuning | 55 / 75 | colour cast the auto-balance misses |
| Auto-exposure limits | Config → Auto-exposure | 0.05–15 s, gain 0–400 | slower lens or darker site |
| Max change per frame | Config → Auto-exposure | 0.12 | twilight steps visible, or ramp too slow |
| Quality / noise reduction | Config → Video output | CRF 26 / Medium | want smaller files or every last detail |

Sliders take input three ways: drag, **double-click to reset** to the shipped default, or click
the number and type one. Exposures accept the unit shown — `500 ms`, `0.5` and `0.5s` are the
same thing. Anything unparseable or out of range loses, and the box is rewritten from the slider,
so a typo can never leave a number on screen that is not the one in force.

Timelapses land in the configured library folder:

```
<library>\2026-09-15_22-45\
  timelapse.mp4     the video
  poster.jpg        thumbnail for the library
  session.json      frame count, exposure, gain, temperatures, timings
```

There is no database. The library is whatever `session.json` files exist under that folder, so
moving, copying, renaming or deleting a night in Explorer just works.

**Config → Appearance → Start with Windows** registers PierCam under the per-user `Run` key —
`HKCU`, so it needs no administrator. The registry is the record of truth rather than the
settings file, so removing the entry from Task Manager's startup tab sticks, and a stale entry
pointing at a moved copy counts as not registered. Window position, size and maximised state are
remembered; the saved rectangle is checked against the desktop that exists now and dropped if the
window would open off the edge of it.

## Staying light over long runs

The app is meant to run for weeks, so:

- **Nothing allocates per frame.** Two RGB buffers and one raw buffer are allocated when a camera
  connects and reused until it disconnects.
- **One bitmap for the life of the connection.** Creating a new image object per frame is the
  classic way a viewer's memory climbs over days.
- **No frame history.** Nothing keeps previous frames anywhere.
- **The stretch is three lookup tables**, rebuilt only when the stretch actually changes. Per-pixel
  work in the steady state is one array index.
- **Frames go to the encoder, not to a queue**, so no buffer can grow if the encoder falls behind.
- **Library thumbnails decode at 480 px and are frozen**, not at full size.

The status bar shows live memory use and uptime so this is checkable rather than a claim.

## When something goes wrong overnight

- **USB dropout** — the capture loop reconnects on its own, retrying every 10 s for 10 minutes,
  and carries on recording into the same video.
- **Power loss or crash** — the video is written as fragmented MP4 while recording, so a partial
  file still plays. On a clean finish it is remuxed into a normal seekable MP4.
- **Disk filling up** — recording stops below the free-space floor (default 5 GB) and finalises
  the video properly.
- **Errors** are logged to `%AppData%\PierCam\crash.log`; Config → Diagnostics has a button.

## Diagnostics

| Variable | Effect |
|---|---|
| `PIERCAM_LAYOUTCHECK=1` | Tests every element against its plate's actual outline geometry and writes anything overhanging an edge to `%AppData%\PierCam\layout-check-*.txt`. Costs nothing when unset. |
| `PIERCAM_SCROLLTRACE=1` | Writes every frame of every carousel glide, including the focused card's screen offset in pixels, to `%AppData%\PierCam\scroll-trace.txt`. |

## Known limitations

- **File-size estimates are estimates.** Calibrated against real measurements, they read about
  15% low over a whole night, because twilight at either end is busier than the deep-night frames
  the calibration came from. A moonlit or cloudy night lands somewhere different again.
- **The live view is as fast as the exposure.** At a 2-second preview exposure you get a frame
  every ~2 seconds. That is the camera, not the app.
- **Raw frame archiving** (Config → Video output) writes 16-bit PNGs alongside the video for
  anyone who wants the originals for stacking. Off by default, because it is the 9 GB-a-night
  option.
- **The config page does not reflow below about 1000 px.** The header and status bar collapse
  gracefully to 640 px; the config page's three columns get cramped.
- **Unsigned.** There is no code-signing certificate, so Windows SmartScreen warns on first run.

## Licence

PierCam is free software under the **GNU General Public License v3.0 or later**. See
[LICENSE](LICENSE).

    Copyright (C) 2026 YoruDev-Ryland

    This program is free software: you can redistribute it and/or modify it under
    the terms of the GNU General Public License as published by the Free Software
    Foundation, either version 3 of the License, or (at your option) any later
    version.

    This program is distributed in the hope that it will be useful, but WITHOUT
    ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
    FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

### Third-party

**FFmpeg** is not included in this repository and is not linked against — PierCam runs
`ffmpeg.exe` as a separate process and talks to it over a pipe. Redistributing a GPL ffmpeg
build alongside the application carries that build's own obligations; `publish.ps1` writes a
`THIRD-PARTY.txt` into every drop that says so and points at the source.

**The ZWO ASI SDK** is not redistributed. `ASICamera2.dll` is loaded at runtime from an existing
ZWO installation.
