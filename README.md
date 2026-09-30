# WSnip

A screenshot tool with the feel of the Windows Snipping Tool that keeps HDR and wide color
gamut content. Screens are captured at 16-bit float precision. SDR content stays pixel-exact, and
HDR highlights are saved as gain-map images: SDR viewers show a tone-mapped base image, and HDR
viewers restore the original brightness.

WSnip runs on Windows and KDE; see [Linux](#linux) for what differs there. HDR / WCG is currently
only supported on Windows.

## Features

- **Snip modes:** rectangle, window, full screen and freeform, with an optional 3, 5 or 10 second
  delay and an option to include the cursor. The screen freezes behind one borderless overlay per
  display. Change mode from the overlay's bar; press Esc or right-click to cancel.
- **Whole window mode:** the screen stays live and WSnip's own windows step aside. The pointer
  turns into a crosshair everywhere and the next click picks the window under it; the click does
  not reach that window. The window is read from the compositor whole, including parts covered by
  other windows and its own per-pixel transparency, so the snip has no desktop behind it. Esc or
  right-click cancels, and WSnip's windows come back either way.
- **Entry points:**
  - On Windows, a global hotkey, **Win+Shift+H** by default. It can be changed, set to Print Screen
    or turned off.
  - On Windows, a notification-area icon.
  - `WSnip.exe --snip [rectangle|window|fullscreen|freeform|wholewindow]`.
  - On Windows, `--background` starts the app in the tray.
  - Later launches forward their command line to the running instance.
- **Capture:** On Windows, Windows.Graphics.Capture delivers `R16G16B16A16Float` scRGB frames at
  each display's SDR white level and peak brightness. Nothing is clipped to SDR or sRGB. WSnip's
  own windows stay open but are left out of the capture.
- **Editor:**
  - Pen, highlighter, eraser and crop tools, with undo/redo, zoom and pan.
  - On an HDR display it draws into an FP16 scRGB surface, so highlights show at their captured
    brightness, rolled off to the display's headroom. A toggle switches to the SDR rendition that
    SDR files and the clipboard get.
  - Custom pen colors on sRGB, Display P3 or BT.2020 primaries, with a brightness up to 16× SDR
    white, so strokes can be wide-gamut or HDR. The eyedropper keeps a color picked from the snip
    exactly, HDR and wide colors included. Custom colors are saved; right-click one to edit or
    remove it. Highlighters multiply, so their colors stay within SDR sRGB.
  - Pixel information next to the pointer while viewing or drawing: position, the color (hex for
    SDR sRGB; otherwise the narrowest of sRGB, Display P3 and BT.2020 with its brightness factor,
    plus linear scRGB), and luminance in multiples of SDR white and in nits at the capturing
    display's SDR white. It can be turned off in settings.
  - On Windows, closing the window returns WSnip to the tray and discards the snip, which was
    already copied or saved as the settings ask.
- **Delivery:**
  - Snips are copied to the clipboard and saved to `Pictures\Screenshots` automatically, using
    Snipping Tool file names ("Screenshot 2026-09-30 101530.png").
  - Save as offers every format.
- **Look:** Modern Windows styles and controls; light and dark themes; the system accent color;
  and an optional Mica backdrop on Windows 11.

## Output formats

The settings pick one format for SDR snips (PNG by default) and one for snips with HDR content (AVIF
by default).

| Format | SDR snip | HDR snip |
| --- | --- | --- |
| PNG | 8-bit sRGB, with transparency | SDR rendition only |
| JPEG | Baseline sRGB | UltraHDR: SDR base + gain map (MPF, ISO 21496-1 and `hdrgm` XMP) |
| HEIC | HEVC through Kvazaar | SDR base + `tmap` gain map item |
| AVIF | AV1 through libaom, 4:4:4 | SDR base + `tmap` gain map item |
| JPEG XL | 8-bit sRGB through libjxl | 16-bit BT.2100 PQ (no gain map) |
| PNG (HDR) | — | 16-bit BT.2100 PQ with `cICP` and `cLLI` |

HEIC, AVIF and JPEG XL use the LGPL FFmpeg build that Light Player bundles. A format whose encoder
is missing is hidden, and automatic saves fall back to PNG or UltraHDR JPEG.

## HDR pipeline

1. **Relative image.** A capture is converted to linear light relative to SDR white: 1.0 is the
   source display's SDR white and 2.0 is twice as bright. The image then gets robust statistics: a
   percentile peak that ignores isolated hot pixels, and whether it has HDR or wide-gamut content.
2. **SDR base.** The base keeps everything at or below SDR white unchanged and compresses only the
   highlights, following the recommended practice for gain-map images. The tone mapping setting
   chooses how:
   - **Adaptive** (default): the compression fades out away from HDR regions, so UI and text next
     to an HDR video stay pixel-exact.
   - **Global:** one curve for the whole image.
   - **Clip:** no compression.
3. **Gain map:**
   - ISO 21496-1, with baseline headroom 0 (SDR) and alternate headroom log2(peak).
   - Offsets of 1/64, gamma 1, three channels, full resolution, so sharp HDR edges survive.
   - JPEG carries both the ISO metadata and Adobe's `hdrgm` XMP, following libultrahdr's segment
     layout.
   - HEIC and AVIF use a `tmap` derived item in an `altr` group, so readers that don't understand
     gain maps show the SDR base.
4. **PQ outputs** (JPEG XL, HDR PNG) map SDR white to 203 nits, per ITU-R BT.2408.
5. **Clipboard.** Windows has no standard HDR clipboard format, so the clipboard gets the SDR
   rendition as PNG (keeping transparency) and as a DIB. The data is written right away, so it
   stays pasteable after WSnip exits.

The gain-map output has been checked against libavif and FFmpeg decoders, and against Light
Player's decoders.

## Linux

WSnip uses the Avalonia fork's Wayland backend in Wayland sessions, and X11 otherwise. What
differs from Windows:

- **Capture** on KDE Plasma goes through KWin's screencast protocol where KWin grants it to WSnip
  (see [KWin screencast access](#kwin-screencast-access)). KWin streams every display to PipeWire
  without asking and leaves WSnip's own windows out, so they stay open. Otherwise capture goes
  through the desktop's ScreenCast portal (xdg-desktop-portal) and PipeWire. The first snip opens
  the system's screen sharing dialog: share the displays to snip and allow restoring, and later
  snips skip the dialog. WSnip's windows can't be left out of a portal capture, so they hide while
  one is taken. Compositors share 8-bit SDR frames for now (KWin offers nothing wider, on either
  path), so snips are SDR. The frames become the same relative linear image as on Windows, so
  wide-gamut and HDR formats can follow once compositors offer them. Without PipeWire or the
  portal, WSnip says what to install.
- **Overlay:** Wayland apps can't place their windows, so one full-screen overlay opens on the
  display the compositor picks and shows that display.
- **Window mode** snips the whole display, as Wayland doesn't tell apps where windows are.
- **Whole window mode** on KDE Plasma picks the window with KWin's own pointer, which it asks for
  over KWin's D-Bus interface (`org.kde.KWin`), and KWin streams that window whole. This needs
  KWin's screencast; otherwise the window is picked in the system's sharing dialog instead of with a
  click on the live screen.
- **Hotkey:** Wayland gives apps no global hotkeys. Bind a shortcut to the desktop entry's
  **New snip**, **Rectangle snip**, **Window snip**, **Full screen snip**, **Freeform snip**, or
  **Whole window snip** action (KDE: System Settings > Keyboard > Shortcuts), or to
  `wsnip --snip [rectangle|window|fullscreen|freeform|wholewindow]`.
- **Clipboard:** the SDR rendition as PNG. It can be pasted while WSnip is open; preserving it
  after exit depends on the desktop's clipboard manager.
- **Lifetime:** WSnip exits when its windows are closed and any active capture has finished.

### KWin screencast access

KWin offers its screencast protocol, `zkde_screencast_unstable_v1`, only to apps whose desktop
entry lists it in `X-KDE-Wayland-Interfaces`. The log (see [Files](#files)) says which way the
displays are captured.

- **Flatpak:** KWin reads the entry named after the app ID, and the Flatpak's entry lists the
  protocol. Recent Flatpak releases, such as 1.18.4 and 1.19.2, leave that key out of the entries
  they install; there, a copy of the entry that keeps it grants the protocol:

  ```sh
  cp ~/.local/share/flatpak/exports/share/applications/im.hjc.WSnip.desktop ~/.local/share/applications/
  sed -i '/^\[Desktop Entry\]$/a X-KDE-Wayland-Interfaces=zkde_screencast_unstable_v1' \
    ~/.local/share/applications/im.hjc.WSnip.desktop
  ```

  A system-wide install keeps its entry in `/var/lib/flatpak/exports/share/applications`. The
  Flatpak reads the streams from the PipeWire daemon (`--filesystem=xdg-run/pipewire-0`) and talks
  to KWin over D-Bus (`--talk-name=org.kde.KWin`).
- **Other builds:** KWin looks for an entry whose `Exec` is the absolute path of the WSnip
  executable, such as this one in `~/.local/share/applications`:

  ```ini
  [Desktop Entry]
  Type=Application
  Name=WSnip
  Exec=/opt/wsnip/WSnip
  NoDisplay=true
  X-KDE-Wayland-Interfaces=zkde_screencast_unstable_v1
  ```

## Architecture

```
src/
  WSnip.Core      net10.0, AOT-compatible. Imaging, tone mapping, gain maps, encoders, annotations,
                  settings and the platform interfaces. No UI and no OS dependencies.
  WSnip.App       Avalonia UI (cross-platform): overlay, editor, settings, tray, theming.
  WSnip.Windows   Windows implementations of the platform interfaces.
  WSnip.Linux     Linux implementations: KWin screencast, ScreenCast portal and PipeWire capture,
                  KWin window picking, D-Bus single instance, freedesktop shell integration.
  WSnip.Desktop   Head for Windows and Linux: the WSnip executable and NativeAOT; on Windows also
                  the manifest and FFmpeg bundling.
```

Platform features are reached only through the interfaces in `WSnip.Core.Platform`:

- `IScreenCaptureService` (displays, and single windows from the compositor)
- `IWindowPickerService` (pointing at a window on the live screen)
- `IGlobalHotkeyService`
- `IWindowIntegrationService` (Mica, frame theme, exclusion from capture, window placement, cursor
  position)
- `IShellService`
- `IClipboardService` (optional; Avalonia's clipboard serves without one)
- `ISingleInstanceService`

To port WSnip to another OS, implement these interfaces and create them in `Program.CreatePlatform`
of `WSnip.Desktop`.

Shared with Light Player (`..\LightPlayer`, source unchanged):

- `LightStudio.Logging`
- `LightStudio.FfmpegShim`
- `build\Ffmpeg.targets`, which downloads and bundles the LGPL FFmpeg shared build
- `packaging/flatpak/nuget-sources.py`, which pins the NuGet packages of the offline Flatpak build
- The Avalonia fork packages (`12.1.4-lightplayer.*`), including the `ExtendedLinear` color mode of
  the Win32 and Wayland backends that provides the FP16 scRGB surface

## Building

Requirements:

- Windows 10 2004 (build 19041) or later. HDR capture and display need an HDR-enabled display.
- Or Linux with PipeWire and xdg-desktop-portal with the desktop's backend, such as
  xdg-desktop-portal-kde. HEIC, AVIF and JPEG XL use the system's FFmpeg 9 libraries.
- .NET SDK 10.0.301 or later 10.0 feature band, as pinned in `global.json`.
- A Light Player checkout next to this repository (`..\LightPlayer`). To use another location, set
  `-p:LightPlayerRoot=...\`.
- The Avalonia fork packages, available through the feeds in `nuget.config`.

```powershell
# Debug build
dotnet build src\WSnip.Desktop -p:Platform=x64

# NativeAOT build: artifacts\publish\WSnip.Desktop\release_win-x64\WSnip.exe
dotnet publish src\WSnip.Desktop -c Release -r win-x64 -p:Platform=x64
```

For ARM64, use `-p:Platform=ARM64 -r win-arm64`.

On Linux, the `Makefile` wraps the same commands. It passes `LIGHTPLAYER_ROOT` (by default
`../LightStudio.LightPlayer`) as `LightPlayerRoot`:

```sh
make                        # Release build
make run
make publish                # NativeAOT: artifacts/publish/WSnip.Desktop/release_linux-x64/WSnip
make publish RID=linux-arm64
```

## Packaging

Windows packages include the self-contained NativeAOT build with the LGPL FFmpeg
libraries.

- **MSIX bundle** (`packaging\WSnipMSIX`): x64 and ARM64 in one sideload bundle, signed with the
  Light Studio certificate (`CN=Light Studio, O=Light Studio, C=US`). It declares a startup task
  for "Start with Windows" (package registry writes are private, so the Run key does not work),
  a `WSnip.exe` execution alias, and the capability to capture without the yellow border. The
  package keeps settings and logs in its own application data. Building it needs Visual Studio's
  MSBuild with the Windows application packaging tools:

  ```powershell
  $env:RuntimeIdentifiers = 'win-x64;win-arm64'
  msbuild packaging\WSnipMSIX\WSnipMSIX.wapproj /restore /p:Configuration=Release /p:Platform=x64 `
    /p:UapAppxPackageBuildMode=SideloadOnly /p:AppxBundle=Always '/p:AppxBundlePlatforms=x64|ARM64' `
    /p:AppxPackageSigningEnabled=false
  ```

  To sign locally, copy the PFX to `packaging\WSnipMSIX\WSnipMSIX_TemporaryKey.pfx`, set
  `$env:PackageCertificatePassword` and drop `/p:AppxPackageSigningEnabled=false`.
- **Setup executables** (`packaging\WSnipSetup`, WiX 5.0.2): one per architecture, installing for
  all users under `Program Files\WSnip` with a Start menu shortcut and an App Paths entry.
  `packaging\WSnipSetup\build.ps1 [-Architecture x64,arm64]` publishes, builds the MSI and wraps
  it in `artifacts\release\WSnipSetup-<arch>.exe`. The setup shows `LICENSE` and `THIRDPARTY.txt`,
  and installs both next to `WSnip.exe`.
- **Flatpak** (`packaging/flatpak`, app ID `im.hjc.WSnip`): `make flatpak` publishes the NativeAOT
  build inside the `org.freedesktop.Sdk` 26.08 sandbox with the .NET 10 SDK extension, restoring
  offline from pinned NuGet packages, and writes
  `artifacts/flatpak-x86_64/im.hjc.WSnip-x86_64.flatpak`; `make flatpak-install` also installs it
  for the current user. FFmpeg 8.1 and PipeWire come from the `org.freedesktop.Platform` runtime.
  It needs `flatpak`, `flatpak-builder` and `python3`; the first build installs the SDK and its
  extension from Flathub. Add a `<release>` to the metainfo for each version.

`packaging\Set-Version.ps1 -Tag v1.2.3` stamps one version into `Directory.Build.props` and the MSIX
manifest; the release workflow runs it with the release tag.

## Releases

`.github/workflows/release.yml` builds the MSIX bundle, the setup executables and the Flatpak
bundles for x64 and ARM64 and publishes them in a GitHub Release when a `v*` tag is pushed, or when
it is run from the Actions tab with a tag. It checks out Light Player next to WSnip, as local builds
expect.

One-time setup of the WSnip repository (**Settings > Secrets and variables > Actions**):

| Secret | Contents |
| --- | --- |
| `WINDOWS_SIGNING_CERTIFICATE_BASE64` | MSIX Signing PFX |
| `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` | Its password |
| `LIGHTPLAYER_DEPLOY_KEY` | Private half of a read-only deploy key of `hjc4869/LightPlayer-Internal` |

- GitHub never shows a secret again, so copy the certificate secrets from the vault where Light
  Player's PFX and password are kept (its `docs/release-signing.md`); the base64 text is
  `[Convert]::ToBase64String([IO.File]::ReadAllBytes('LightPlayer-Signing.pfx'))`. Using the same
  certificate lets one trusted certificate cover both apps, and every WSnip release update in place.
- Create the deploy key with `ssh-keygen -t ed25519 -C "WSnip CI" -f wsnip-ci` and an empty
  passphrase, add `wsnip-ci.pub` to LightPlayer-Internal under **Settings > Deploy keys** with
  write access unchecked, store the contents of `wsnip-ci` as `LIGHTPLAYER_DEPLOY_KEY`, then delete
  both files.
- The workflow asks for `contents: write` itself to create the release; nothing else needs
  changing unless the repository owner restricts workflow permissions.
- Optionally set the repository variable `LIGHTPLAYER_REF` to pin the Light Player branch, tag or
  commit built against; it defaults to `main`, and a manual run can override it.

The Windows jobs use the `windows-2025-vs2026` image, as Light Player does: the .NET 10 SDK needs
MSBuild 18, and the `.wapproj` builds only with Visual Studio's MSBuild. The Flatpak jobs run
`make flatpak` on the `ubuntu-26.04` and `ubuntu-26.04-arm` images, so each architecture builds
natively.

## Files

- Settings: `%APPDATA%\WSnip\settings.json`; the MSIX package keeps them in its own local
  application data (`%LOCALAPPDATA%\Packages\LightStudio.WSnip_<id>\LocalState\WSnip`)
- Log: `logs\wsnip.log` next to the settings
- Snips: the system Screenshots folder (usually `Pictures\Screenshots`), or the folder chosen in
  settings
- Launch at startup: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\WSnip`, or the package's
  startup task for the MSIX
- On Linux: settings in `~/.config/WSnip` (`~/.var/app/im.hjc.WSnip/config/WSnip` for the
  Flatpak), snips in `~/Pictures/Screenshots`, and launch at startup in
  `~/.config/autostart/im.hjc.WSnip.desktop`. `screencast-restore-token` next to the settings
  restores the displays shared through the portal; delete it to choose them again.

## Known limitations

- Linux captures SDR only, and a Wayland overlay covers one display, so a snip can't span
  displays there. The Linux version has been developed on KDE Plasma 6.7; other desktops are
  untested.
- Whole window snips keep the transparency a window draws itself. Windows 11's rounded corners
  and a window-wide opacity set with `SetLayeredWindowAttributes` are applied by the compositor
  afterwards and are not part of the captured window.
- HEVC needs dimensions that are a multiple of 8. HEIC images are padded and then cropped back
  with a `clap` box. Readers that ignore `clap` show a few extra edge pixels.
- JPEG XL keeps HDR as PQ rather than a gain map, because FFmpeg's libjxl wrapper can't write JPEG
  XL gain maps.
- It has been tested on one 4K HDR display. Snips that span displays with different SDR white
  levels are composed in relative light but have not been tested.

## License

Copyright (c) 2026 David Huang. **All rights reserved.**

This project is **source-available for reference and private, personal, non-redistributable use only**; it is currently *not* FOSS licensed. See [LICENSE](LICENSE) for the full terms.
