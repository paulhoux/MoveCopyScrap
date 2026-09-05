# MoveCopyScrap

A fast photo/video culling tool for Windows. Point it at a folder, fly through the images
with the arrow keys, hit **Space** on the keepers, then copy, move or delete the marked set
in one go.

* **WinUI 3 / C#** (.NET 8, Windows App SDK), unpackaged and xcopy-deployable
* **CMake** drives configure / build / install on top of Visual Studio 2022's MSBuild
* 3D perspective carousel animated entirely on the compositor thread
* Background thumbnail generation, virtualized filmstrip
* Marks survive crashes and restarts

---

## Quick start

Double-click **`build.bat`**, or from a terminal:

```powershell
.\build.bat
```

It installs whatever is missing, builds, installs into `dist\`, and puts an
**MoveCopyScrap** entry in your Start Menu. Nothing needs to be set up first beyond a
normal Windows install with `winget` available.

| Switch | Effect |
| --- | --- |
| `-Run` | Launch the app when the build finishes |
| `-Clean` | Delete the build directory first |
| `-Configuration Debug` | Debug build instead of Release |
| `-NoInstall` | Build only; leave the output in `build\publish\` |
| `-NoShortcut` | Install, but create no Start Menu entry |
| `-DesktopShortcut` | Also drop a shortcut on the desktop |
| `-SingleFile` | Publish one self-extracting .exe instead of a folder |
| `-Installer` | Also build a Setup .exe with Inno Setup (fetched via winget) |
| `-Preset ninja-x64` | Force a particular CMake preset |
| `-NonInteractive` | Never prompt, never pause. For CI |

What it fetches, each only if missing:

| Prerequisite | Why | Source |
| --- | --- | --- |
| .NET 8 SDK | Compiles the C# and the XAML | `winget Microsoft.DotNet.SDK.8` |
| CMake ≥ 3.21 | Drives the build | Visual Studio's copy, else `winget Kitware.CMake` |
| Ninja | Only when Visual Studio is absent | Visual Studio's copy, else `winget Ninja-build.Ninja` |
| Visual C++ runtime | The Windows App SDK's native DLLs link against it | `winget Microsoft.VCRedist.2015+.x64` |

The Windows App SDK itself is a NuGet package and is restored by the build.

**Visual Studio is optional.** CMake here only shells out to `dotnet publish`, and the
.NET SDK brings its own compiler, so there is no C++ toolchain in the picture — a
generator is all that is needed, and Ninja does that job. The script uses Visual Studio
when it finds it and falls back to Ninja when it does not.

The rest of this section describes doing it by hand.

## Building

### Prerequisites

* **.NET 8 SDK or newer** — <https://dotnet.microsoft.com/download/dotnet/8.0>
  (the *SDK*, not just the runtime). This is what actually compiles the project.
* **Windows 11 SDK 10.0.22621 or newer** (any Visual Studio 2022 install, including
  Build Tools, provides one).
* **CMake 3.21 or newer** — the copy inside VS works (*Desktop development with C++* →
  *C++ CMake tools*).

Visual Studio 2022 with the **.NET Desktop Development** workload also works and lets you
F5-debug, but it is not required: a VS 2022 *Build Tools* install plus the standalone .NET
SDK is enough for a command-line build.

> **Note on VS Build Tools.** A Build Tools installation ships `MSBuild.exe` but not
> `MSBuild\Sdks\Microsoft.NET.Sdk`, so that MSBuild cannot build SDK-style C# projects at
> all (`MSB4236: The SDK 'Microsoft.NET.Sdk' specified could not be found`). CMake
> therefore prefers the `dotnet` CLI, which carries its own SDK, and skips any MSBuild
> candidate that lacks the managed SDKs.
>
> Relatedly, the project sets `<EnableMsixTooling>true</EnableMsixTooling>`. It stays an
> **unpackaged** app (`WindowsPackageType=None`); the flag only selects the Windows App
> SDK's own MSIX/PRI build tasks, which ship inside the NuGet package. Without it the SDK
> uses the legacy `MrtCore.PriGen.targets`, which loads
> `Microsoft.Build.Packaging.Pri.Tasks.dll` from a Visual Studio install's `AppxPackage`
> folder and fails with `MSB4062` under the dotnet CLI. `resources.pri` is needed either
> way, because WinUI 3 resolves `ms-appx:///` XAML URIs through it.

### With CMake (recommended)

```powershell
cmake --preset vs2022-x64
cmake --build --preset release
cmake --install build --prefix C:\Apps\MoveCopyScrap
```

or without presets:

```powershell
cmake -S . -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
cmake --install build --prefix C:\Apps\MoveCopyScrap
```

Run it straight from the build tree with:

```powershell
cmake --build build --config Release --target run
```

### Making something to hand out

An installer, which is the friendliest thing to send someone:

```powershell
.\build.bat -Installer
```

Inno Setup is fetched with winget if it is missing, and
`build\installer\MoveCopyScrap-1.0.0-Setup.exe` comes out the other end. It installs
per-user into `%LOCALAPPDATA%\Programs` with no UAC prompt at all (the wizard still
offers all-users), adds Start Menu and optional desktop shortcuts, registers in Add/Remove
Programs, and on uninstall asks whether to keep your saved marks. The version comes from
the .exe itself, so nothing has to be kept in step with CMake by hand.

Requires Inno Setup 6.3 or newer, which is what winget installs.

Or a plain zip, if you would rather not have anything installed:

```powershell
cpack -G ZIP --config build\CPackConfig.cmake
```

produces `MoveCopyScrap-1.0.0-win-x64.zip`: one folder, with `MoveCopyScrap.exe` and the
README right at the top. Unzip anywhere and run it — nothing to install.

For a single file instead of a folder of ~400:

```powershell
.\build.bat -SingleFile
```

or, by hand — note the `MCS_` prefix, CMake ignores a mistyped `-D` in silence:

```powershell
cmake --preset vs2022-x64 -DMCS_SINGLE_FILE=ON
cmake --build --preset release
```

That publishes one self-extracting `MoveCopyScrap.exe` of about the same total size. The
Windows App SDK supports this for unpackaged, self-contained apps, which is what this is.
The trade-off is the first launch: everything, native DLLs included, is unpacked to a
per-version folder under `%TEMP%` before the app starts. Subsequent launches reuse it.

#### What CMake actually does

CMake cannot compile WinUI 3 C# itself — its built-in `CSharp` language support only emits
legacy, non-SDK `.csproj` files, which cannot run the XAML compiler or the Windows App SDK
targets. So `CMakeLists.txt` is a driver:

| Step | What happens |
| --- | --- |
| configure | Picks a build driver (`dotnet` CLI, else a *validated* VS 2022 MSBuild found through `vswhere`), resolves the RID/platform, decides self-contained vs framework-dependent, stamps the version |
| build | `dotnet publish` (or `MSBuild -t:Restore` + `-t:Publish`) into `build/publish/<Config>` |
| install | Copies the published folder to `<prefix>/bin` |

Useful cache variables:

| Variable | Default | Meaning |
| --- | --- | --- |
| `MCS_RUNTIME_IDENTIFIER` | `win-x64` | `win-x64` or `win-arm64` |
| `MCS_SELF_CONTAINED` | `ON` | Bundle the .NET runtime **and** the Windows App SDK so the output folder runs anywhere |
| `MCS_BUILD_DRIVER` | `auto` | `auto`, `dotnet`, or `msbuild` |
| `MCS_DOTNET_EXECUTABLE` | auto | Override which `dotnet` is used |
| `MCS_MSBUILD_EXECUTABLE` | auto | Override which `MSBuild.exe` is used |
| `MCS_PARALLEL_BUILD` | `ON` | Build in parallel |
| `MCS_SINGLE_FILE` | `OFF` | Publish one self-extracting .exe instead of a folder |
| `MCS_INSTALL_BINDIR` | `.` | Install subdirectory for the app; `.` keeps it flat |

### Without CMake

`src/MoveCopyScrap.csproj` is a perfectly ordinary SDK-style project. Open it (or the
solution CMake generates) in Visual Studio 2022, set the platform to **x64**, and press F5.

---

## Using it

| Key | Action |
| --- | --- |
| `←` `→` | Previous / next image (200 ms animated transition) |
| `Home` / `End` | First / last image |
| `Space` | Mark or unmark the current image |
| `Ctrl+left` / `Ctrl+right` | Rotate the current image a quarter turn anticlockwise / clockwise |
| `F` or `Enter` | Fill the window (side images fade out, Ken Burns drift starts) |
| `K` | In fill mode: fit the whole image, or fill the window |
| `Esc` | Leave fill mode |
| `F11` | Real full screen |
| `Ctrl+O` | Open a folder |
| `Ctrl+C` | Copy marked files to another folder |
| `Ctrl+M` | Move marked files to another folder (confirmation required) |
| `Delete` | Delete marked files (confirmation required) |

Mouse: the wheel steps through images, clicking a side image jumps to it, clicking the
centre image toggles fill mode, right-clicking the centre image marks it, and clicking a
thumbnail jumps straight there.

### Rotation

`Ctrl+left` and `Ctrl+right` turn the current picture a quarter turn. The carousel card
swings round immediately - frame, filmstrip thumbnail and neighbour spacing all follow -
but the file is not touched for five seconds, and every further turn restarts that
countdown. Turn a photo back to where it started and nothing is written at all.

The write itself is **lossless**: only the EXIF orientation tag changes, two bytes in the
middle of the file, with every byte of compressed image data left exactly as it was. A
photo can be rotated as many times as you like without losing a scrap of quality, and
Explorer, Photos, phones and browsers all show the result the same way this app does.

Two consequences worth knowing:

* Only **JPEG** and **TIFF** carry an orientation tag, so only those can be rotated. PNG,
  BMP, GIF, HEIC, WebP and video are declined with an explanation rather than re-encoded
  behind your back.
* A JPEG that has metadata but no orientation tag inside it is also declined. Adding one
  means rebuilding the whole metadata block, which shifts every internal offset - including
  the ones inside the camera maker's private MakerNote, which cannot be rewritten reliably.
  Damaging somebody's metadata silently is worse than saying no. (A JPEG with no metadata
  at all is fine: there is nothing to damage, so a minimal block is added.)

Pending rotations are always flushed before the folder changes, before any copy, move or
delete, and when the window closes, so what lands on disk is never out of step with what
you saw.

`testdata/exif-orientation/` holds one fixture per EXIF orientation value. All eight are
different pixel arrays that must render as the same picture; open that folder to check
that orientation handling is still correct end to end.

### Marks

Marks are stored per folder in:

```
%LOCALAPPDATA%\MoveCopyScrap\marks\<folder-name>-<hash>.json
```

Nothing is ever written into your picture folders, so read-only and network locations work
fine. Writes are debounced (600 ms) and atomic — a crash leaves either the previous or the
new file, never a half-written one. Marks for files that have disappeared are pruned on
load, and are cleared automatically after a move or a delete.

### Deleting

Deletes go to the Recycle Bin on fixed and removable drives. On UNC paths and mapped
network drives there is no Recycle Bin, so the confirmation dialog says explicitly that the
files will be deleted **permanently** and cannot be recovered.

### Formats

Images: JPEG, PNG, BMP, GIF, TIFF, WebP, HEIC/HEIF, AVIF, ICO, DDS
Videos: MP4, M4V, MOV, MKV, AVI, WMV, WebM, MPEG, M2TS, 3GP

Videos play muted and looping in the centre slot with a scrub bar across the top and a
play/pause button. Some formats (HEIC, AVIF, MKV) rely on Windows codec packs from the
Microsoft Store; without them those files fall back to a placeholder.

---

## How it is put together

Everything checked in is a source file; everything generated lives under `build/` or
`dist/`, both of which are ignored by git and can be deleted at any time.

```
build.bat / build.ps1        One-command build: fetch prerequisites, build, install, shortcut
CMakeLists.txt               CMake driver (configure / build / install / cpack)
CMakePresets.json            vs2022-x64, vs2022-arm64, ninja-x64
Directory.Build.props        Redirects obj\ and bin\ out of src\ and into build\

assets/
  MoveCopyScrap.ico          App icon: 16/24/32/48/64/128/256 px

cmake/
  PurgeStaleAppHost.cmake    Guards against the SDK's stale-apphost gap

installer/
  MoveCopyScrap.iss          Inno Setup script; payload and output passed in by build.ps1

src/
  MoveCopyScrap.csproj       SDK-style WinUI 3 project, unpackaged, self-contained
  app.manifest               PerMonitorV2 DPI, long paths
  App.xaml[.cs]              Dark theme, palette and command-bar styles
  MainWindow.xaml[.cs]       Title bar, command bar, filmstrip, dialogs, keyboard
  Controls/CarouselView.cs   The carousel
  Models/MediaItem.cs        One file: marks, thumbnail, aspect ratio, rotation (INPC)
  Services/
    MediaScanner.cs          Folder enumeration + Explorer-style natural sort
    ThumbnailService.cs      Background thumbnails, bounded concurrency
    ImageLoader.cs           Decode-to-display-size, LRU cache
    MarkStore.cs             Debounced atomic JSON mark state
    RotationService.cs       Lossless rotation by rewriting the EXIF orientation tag
    SettingsStore.cs         Preferences that outlive a session
    FileOperations.cs        Copy / move / recycle-bin delete
  Helpers/NaturalComparer.cs

build/                       ── generated ──────────────────────────────────
  CMakeCache.txt, *.vcxproj  CMake's own configure output
  obj/MoveCopyScrap/         MSBuild intermediates, NuGet restore assets
  bin/MoveCopyScrap/         MSBuild output
  publish/<Config>/          The self-contained app as built
  installer/                 The compiled Setup .exe
dist/bin/                    The installed app (cmake --install)
```

### Performance notes

* **Nothing decodes on the UI thread.** Thumbnails come from the Windows shell thumbnail
  cache on a bounded pool of background threads (`ProcessorCount / 2`, clamped 2–8), which
  is also where video poster frames come from. Only the final `BitmapImage` handoff touches
  the UI thread, and the decode itself happens inside `SetSourceAsync`.
* **Images decode at display size.** `DecodePixelWidth` is set to the on-screen pixel width
  rounded up to a 512 px bucket, so a 50 MP raw JPEG never materialises at full size, and a
  small window resize does not trigger a re-decode.
* **The carousel is virtualized.** Seven element slots are recycled, so a folder with 50 000
  files costs the same as one with five.
* **All motion runs on the compositor thread.** `Translation`, `Scale`,
  `RotationAngleInDegrees` and `Opacity` are animated through the Composition API, so
  transitions stay smooth while images decode. Element sizes do not change when toggling
  fill mode, so that transition involves no XAML layout pass at all.
* **The filmstrip is an `ItemsRepeater`** inside a `ScrollViewer`; only visible thumbnails
  are realised and requested, with a ±12 prefetch around the current position.

### Perspective

WinUI 3 does not support `PlaneProjection`. The 3D effect instead comes from a 4×4 matrix
with `M34 = -1/d` installed on the carousel's stage visual — the supported way to get a
vanishing point in Composition — after which each slot simply rotates about the Y axis.
The neighbours are rotated 26°, scaled to 72 %, and positioned so the centre image overlaps
them slightly. If the rotation direction is not to your taste, flip the sign in
`CarouselView.Relayout` (the line is commented).

### Tuning

The constants at the top of `CarouselView` control the look: `SideScale`, `SideAngleDegrees`,
`OverlapFraction`, `MinRevealFraction`, `CellWidthFraction`, `PerspectiveDistance` and
`TransitionDuration` (200 ms).

Fill mode's Ken Burns drift is driven by the crop rather than by a fixed distance. Cover-filling
fits one axis exactly and overflows the other, so the pan travels along whichever axis the crop
is hiding — vertically for a portrait picture in a landscape window, horizontally for a panorama
— far enough to reveal all of it. `KenBurnsPixelsPerSecond` then sets the period from that
distance, so the motion reads at the same speed whether it is nudging a landscape shot (~20 s) or
crossing a tall one (~60 s). `KenBurnsZoom` is the zoom depth, `KenBurnsEdgeGuard` the couple of
pixels of crop held back from the pan, and `KenBurnsMaxSweepFraction` a safety bound that only
bites on extreme aspect ratios.

The pan is bounded by the crop available at the *minimum* scale, never by the extra room the zoom
opens up. The two are eased differently — the pan decelerates into its extremes, the zoom eases in
and out — so part way through a segment the pan can run ahead of the zoom that was meant to be
making room for it, and the background shows. Since the zoom never drops below `cover`, bounding
the pan by the resting crop is safe at every instant regardless of easing. A picture whose aspect
already matches the window has no slack at all, so it only zooms.

The loop starts and ends at zero offset — the exact framing the fill transition leaves behind —
so there is no jump entering the mode, and none at the seam between repeats either.

Fill mode has two styles, toggled with `K` or the floating control that appears top-right
(it fades in on mouse movement and out again after a couple of seconds, since in fill mode
it is the only visible chrome):

* **Fill** — covers the window, crops what does not fit, and drifts across the crop.
* **Fit** — shows the whole image, letterboxed, perfectly still.

They are one setting rather than two, because once nothing is cropped the pan has nowhere
to travel; `CarouselView.FillScaleFor` is the single place that decides `max` versus `min`,
and the layout, the picture loader and the Ken Burns loop all read it. The choice is
remembered in `%LOCALAPPDATA%\MoveCopyScrap\settings.json`.

---

## Troubleshooting

**"The application has failed to start because its side-by-side configuration is
incorrect" (exit code 14001).** The embedded Win32 manifest is malformed. Event Viewer →
Windows Logs → Application has a `SideBySide` entry naming the exact element, e.g.
*"The setting http://schemas.microsoft.com/SMI/2005/WindowsSettings^dpiAwareness is not
registered"* — the 2005 namespace defines `<dpiAware>`, only the 2016 one defines
`<dpiAwareness>`.

Note that after fixing `app.manifest` you may still get the old error: the .NET SDK does
not treat `app.manifest` as an input to apphost creation, so `obj/.../apphost.exe` — the
stub that becomes `MoveCopyScrap.exe` — keeps the previous manifest while every other part
of the build correctly rebuilds. `cmake/PurgeStaleAppHost.cmake` runs before every build to
delete an apphost older than `app.manifest`; if you ever need to force it by hand:

```powershell
cmake --build build --config Release --target clean-publish
```

**A clean build that produces no window.** Unhandled exceptions are swallowed and logged
(`App.OnUnhandledException`), so a startup failure can look like nothing happening. Attach
a debugger, or temporarily set `e.Handled = false` in `App.xaml.cs`.

## Known gaps

* HEIC / AVIF / some MKV files need the Microsoft Store codec packs to render.
* Fill mode does not zoom or pan by hand yet; the Ken Burns drift is automatic.
* The folder is scanned once, non-recursively; there is no file-system watcher.
