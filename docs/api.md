# PierCam remote access API

A small, read-only HTTP API for other apps — a dashboard on another machine, over Tailscale or a LAN. Switch it on under **Config → 08 / NET → REMOTE ACCESS**. Nothing can be changed through it: no settings, no camera, no recordings.

- **Base URL:** `http://<pier-pc>:1890` (the port is set in PierCam). This page is served by PierCam itself at `/api/v1/docs`, and as Markdown at `/api/v1/docs.md` - both open without a key.
- **Format:** JSON, camelCase, times as ISO 8601 with the PC's offset.
- **Browsers:** CORS is open (`Access-Control-Allow-Origin: *`), so a web dashboard on another origin can call it directly.
- **Access key** (optional, off by default): when **REQUIRE AN ACCESS KEY** is on, send it as an `X-Api-Key` header, or as `?key=` on the URL — the latter is what an `<img>` or `<video>` tag needs. Without it: `401`.
- **Firewall:** the first time it is switched on, Windows may ask whether to let PierCam through. Allow it on the network your dashboard uses.

## `GET /api/v1/status`

What PierCam is doing right now.

```json
{
  "version": "1.14",
  "time": "2026-10-23T21:14:05-05:00",
  "state": "Recording",
  "message": "Recording · Friday 23 October 2026",
  "recording": { "title": "Friday 23 October 2026", "started": "…", "frames": 42, "nextFrame": "…" },
  "nextSession": "…",
  "camera": { "name": "ZWO ASI676MC", "width": 3552, "height": 3552 },
  "frame": { "sequence": 18342, "url": "/api/v1/frame.jpg" },
  "roof": { "source": "Alpaca", "state": "Open", "detail": "Building 2 says safe", "gating": true }
}
```

`state` is one of `Disconnected`, `Connecting`, `Previewing`, `Recording`, `Error`. `recording` is `null` when nothing is recording.

## `GET /api/v1/frame.jpg`

The live frame as a JPEG, exactly as the live view shows it (blur mask applied; marker and sky overlay if shown).

| Parameter | Default | |
|---|---|---|
| `width` | 1920 (or the sensor's width, if smaller) | Resized to this width; height follows the aspect. |
| `quality` | 85 | JPEG quality, 30–95. |

Polling cheaply: every response carries an `ETag` and an `X-Frame-Sequence` header. Send the ETag back as `If-None-Match` and an unchanged frame answers `304 Not Modified` with no body. `frame.sequence` in `/status` is the same number. `503` until the camera has delivered a frame.

## `GET /api/v1/sessions`

The recorded nights, newest first.

```json
[
  {
    "id": "2026-10-22_19-48",
    "title": "Thursday 22 October 2026",
    "status": "Complete",
    "started": "…", "ended": "…",
    "frames": 612, "fps": 30, "seconds": 20.4,
    "width": 1920, "height": 1920,
    "camera": "ZWO ASI676MC",
    "poster": "/api/v1/sessions/2026-10-22_19-48/poster.jpg",
    "video": { "url": "/api/v1/sessions/2026-10-22_19-48/video.mp4", "bytes": 25358671 },
    "marked": { "url": "…/marked.mp4", "bytes": 0, "poster": "…/marked-poster.jpg" }
  }
]
```

`video` is `null` while a night is still recording — the file is only complete when the night is. `marked` is `null` unless a separate copy with the target marker or sky overlay burned in was kept.

`GET /api/v1/sessions/{id}` returns one of these.

## Files

| Path | |
|---|---|
| `/api/v1/sessions/{id}/video.mp4` | The timelapse (H.264 MP4). |
| `/api/v1/sessions/{id}/marked.mp4` | The marked copy, where there is one. |
| `/api/v1/sessions/{id}/poster.jpg` | A still from the night. |
| `/api/v1/sessions/{id}/marked-poster.jpg` | The marked copy's still. |

Videos honour `Range` requests (`206 Partial Content`), so a `<video src="…/video.mp4">` streams and seeks without downloading the whole file first. `HEAD` works on everything.

## Errors

JSON `{ "error": "…" }` with `401` (key), `404` (no such endpoint, session or file), `405` (anything but GET/HEAD/OPTIONS), `503` (no frame yet, or 16 requests already in progress).
