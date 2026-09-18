# tools/

`ffmpeg.exe` belongs here. It is not committed: it is a 98 MB third-party binary, which is
both larger than GitHub's per-file limit and not ours to carry in this history.

PierCam runs it as a separate process — it does not link against it — and also falls back to
whatever `ffmpeg` is on `PATH`, so you can point the Config page at an existing install
instead of putting one here.

## The build this was developed against

    ffmpeg 9.0.1 "essentials", built by Gyan Doshi — https://www.gyan.dev/ffmpeg/builds/
    configured --enable-gpl --enable-version3, so GPL v3 applies to the binary

Any reasonably recent ffmpeg with `libx264` will do. What PierCam actually needs from it:

- `libx264` encoding, `-crf`, `-preset`
- the `hqdn3d` filter (noise reduction) and `scale` (downscaling old timelapses)
- `-f rawvideo -pix_fmt bgr24` piped to stdout, which is how playback decodes frames

## Licence

Shipping this binary to anyone else means shipping a GPL binary, and the obligation to make
the corresponding source available comes with it. `publish.ps1` writes a `THIRD-PARTY.txt`
into every drop that says so and points at the source.
