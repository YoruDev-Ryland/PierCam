# Builds a shareable PierCam drop: one self-contained exe, ffmpeg beside it, licences, and a zip.
#
#   .\publish.ps1                     -> dist\PierCam-<version>-win-x64\ and the matching .zip
#   .\publish.ps1 -Installer          -> also dist\PierCam-<version>-Setup.exe
#   .\publish.ps1 -Version 1.2        -> stamps that version instead of the .csproj's
#   .\publish.ps1 -NoZip              -> leaves the folder, skips the archive
#
# Self-contained means the recipient needs no .NET install; the runtime is inside the exe.
# It cannot swallow ffmpeg.exe as well — PierCam runs ffmpeg as a separate process, so it has to
# exist as a real file on disk — hence a folder rather than a literal single file.
param(
    [switch]$NoZip,
    [switch]$Installer,
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# A per-user SDK is the norm on machines where UAC cannot be answered; fall back to PATH.
if (Test-Path "$env:LocalAppData\Microsoft\dotnet\dotnet.exe") {
    $env:DOTNET_ROOT = "$env:LocalAppData\Microsoft\dotnet"
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
}

if (-not $Version) {
    $Version = ([xml](Get-Content "$root\PierCam.csproj")).Project.PropertyGroup.FileVersion |
        Where-Object { $_ } | Select-Object -First 1
}
# Accept a git tag verbatim ("v1.2.0") and reduce it to the marketing version the UI shows.
$Version = ($Version -replace '^v', '')
$parts = @($Version -split '\.') + @('0', '0')
$short = ($parts[0..1] -join '.')
$full = ($parts[0..2] -join '.') + '.0'

$name = "PierCam-$short-win-x64"
$stage = Join-Path $root "dist\$name"

Write-Host "building $name (assembly $full)" -ForegroundColor Cyan
Get-Process PierCam -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path "$root\dist") { Remove-Item "$root\dist" -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# Trimming is deliberately off: WPF resolves XAML types by reflection and a trimmed build throws
# at the first window.
#
# Single-file compression is off too, and that one is measured rather than assumed. Compressed:
# 104 MB exe, 308 MB working set. Uncompressed: 253 MB exe, 194 MB working set — the same as an
# ordinary build. Compression decompresses every assembly into memory at startup and keeps it
# there, so it trades 114 MB of permanent memory for disk. This program exists because other
# capture software got heavy after a few days; it is not going to hand that back for a smaller
# download, and the zip and the installer both claw the difference back anyway.
& dotnet publish "$root\PierCam.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=false `
    -p:DebugType=none `
    -p:Version=$short -p:AssemblyVersion=$full -p:FileVersion=$full `
    -o "$stage" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Get-ChildItem $stage -Filter *.pdb -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue
if (-not (Test-Path "$stage\tools\ffmpeg.exe")) {
    if (-not (Test-Path "$root\tools\ffmpeg.exe")) {
        throw "tools\ffmpeg.exe is missing — see tools\README.md"
    }
    New-Item -ItemType Directory -Path "$stage\tools" -Force | Out-Null
    Copy-Item "$root\tools\ffmpeg.exe" "$stage\tools\ffmpeg.exe"
}

@"
PierCam $short — dusk-to-dawn timelapse for an observatory pier camera

WHAT YOU NEED
  Windows 10 (1809 or later) or Windows 11, 64-bit. No .NET install: the runtime is
  inside PierCam.exe.

  A ZWO camera and ZWO's driver package. PierCam does not ship ASICamera2.dll — it finds
  the one already on the machine, so ZWO driver updates help it rather than break it.
  If the Config page shows "ASI SDK  not loaded", install ZWO's free ASIStudio from
  https://www.zwoastro.com/downloads/ and restart PierCam.

RUNNING IT
  Keep tools\ffmpeg.exe beside PierCam.exe — that is what writes the video.

  Windows will warn that the app is unsigned and from an unknown publisher. It is: this is a
  hobby build with no code-signing certificate. "More info" then "Run anyway" if you trust
  where you got it from.

FIRST RUN
  Config page, in order: pick your camera at the top, set the library folder under STORAGE,
  and set your site coordinates under SITE & ROOF (or import them from N.I.N.A.). Everything
  on that page is saved per-user under %AppData%\PierCam and nothing is baked into the build.

  If your observatory writes a roof status file, point SITE & ROOF at it and PierCam will
  record only while the roof is open. Without one, set a schedule instead.

LICENCE
  PierCam is free software under the GNU General Public License v3 or later; see LICENSE.txt.
  See THIRD-PARTY.txt for ffmpeg, which is redistributed here under its own terms.
"@ | Set-Content "$stage\READ-ME-FIRST.txt" -Encoding utf8

# Name the actual binary being shipped rather than assuming a packager, since a local build and
# a CI build fetch ffmpeg from different places.
$ffmpegBanner = (& "$stage\tools\ffmpeg.exe" -version 2>&1 | Select-Object -First 1)

@"
THIRD-PARTY SOFTWARE IN THIS DOWNLOAD

FFmpeg — tools\ffmpeg.exe
  $ffmpegBanner

  A build configured with --enable-gpl, and therefore covered by the GNU General Public
  License in its own right.

  PierCam does not link against FFmpeg. It runs ffmpeg.exe as a separate process and talks to
  it over a pipe, and the binary here is unmodified.

  Source code for these builds, and the licence text, are published alongside the binaries at
  https://www.gyan.dev/ffmpeg/builds/ and upstream at https://ffmpeg.org/download.html.
  A copy of the GPL is at https://www.gnu.org/licenses/gpl-3.0.html.

  If you redistribute this folder you are redistributing a GPL binary and take on the same
  obligation to make that source available.

ZWO ASI SDK — not included
  PierCam loads ASICamera2.dll from an existing ZWO installation (ASIStudio or SharpCap) at
  run time. Nothing from ZWO is redistributed here.
"@ | Set-Content "$stage\THIRD-PARTY.txt" -Encoding utf8

"`n  PierCam.exe        {0,8:N1} MB" -f ((Get-Item "$stage\PierCam.exe").Length / 1MB)
"  tools\ffmpeg.exe   {0,8:N1} MB" -f ((Get-Item "$stage\tools\ffmpeg.exe").Length / 1MB)

if (-not $NoZip) {
    $zip = "$root\dist\$name.zip"
    Compress-Archive -Path "$stage\*" -DestinationPath $zip -CompressionLevel Optimal
    "  zip                {0,8:N1} MB" -f ((Get-Item $zip).Length / 1MB)
}

if ($Installer) {
    # Local installs land under the user profile; CI runners keep it in Program Files (x86).
    $iscc = @(
        "$env:LocalAppData\Programs\Inno Setup 7\ISCC.exe",
        "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
    if (-not $iscc) { throw "Inno Setup not found. Install it, or run without -Installer." }

    & $iscc /Q "/DAppVersion=$short" "/DStageDir=$stage" "$root\installer\PierCam.iss"
    if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
    "  setup              {0,8:N1} MB" -f ((Get-Item "$root\dist\PierCam-$short-Setup.exe").Length / 1MB)
}

Write-Host "done -> $root\dist" -ForegroundColor Green
