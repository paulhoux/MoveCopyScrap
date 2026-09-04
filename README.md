# ImageCuller 2

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
**ImageCuller 2** entry in your Start Menu. Nothing needs to be set up first beyond a
normal Windows install with `winget` available.

| Switch | Effect |
| --- | --- |
| `-Run` | Launch the app when the build finishes |
| `-Clean` | Delete the build directory first |
| `-Configuration Debug` | Debug build instead of Release |
| `-NoInstall` | Build only; leave the output in `build\publish\` |
| `-NoShortcut` | Install, but create no Start Menu entry |
| `-DesktopShortcut` | Also drop a shortcut on the desktop |
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
cmake --install build --prefix C:\Apps\ImageCuller2
```

or without presets:

```powershell
cmake -S . -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
cmake --install build --prefix C:\Apps\ImageCuller2
```

Run it straight from the build tree with:

```powershell
cmake --build build --config Release --target run
```

`cpack -G ZIP --config build\CPackConfig.cmake` produces a redistributable zip.

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
| `IC_RUNTIME_IDENTIFIER` | `win-x64` | `win-x64` or `win-arm64` |
| `IC_SELF_CONTAINED` | `ON` | Bundle the .NET runtime **and** the Windows App SDK so the output folder runs anywhere |
| `IC_BUILD_DRIVER` | `auto` | `auto`, `dotnet`, or `msbuild` |
| `IC_DOTNET_EXECUTABLE` | auto | Override which `dotnet` is used |
| `IC_MSBUILD_EXECUTABLE` | auto | Override which `MSBuild.exe` is used |
| `IC_PARALLEL_BUILD` | `ON` | Build in parallel |
| `IC_INSTALL_BINDIR` | `bin` | Install subdirectory for the app |

### Without CMake

`src/ImageCuller2.csproj` is a perfectly ordinary SDK-style project. Open it (or the
solution CMake generates) in Visual Studio 2022, set the platform to **x64**, and press F5.

---

## Using it

| Key | Action |
| --- | --- |
| `←` `→` | Previous / next image (200 ms animated transition) |
| `Home` / `End` | First / last image |
| `Space` | Mark or unmark the current image |
| `F` or `Enter` | Fill the window (side images fade out, Ken Burns drift starts) |
| `Esc` | Leave fill mode |
| `F11` | Real full screen |
| `Ctrl+O` | Open a folder |
| `Ctrl+C` | Copy marked files to another folder |
| `Ctrl+M` | Move marked files to another folder (confirmation required) |
| `Delete` | Delete marked files (confirmation required) |

Mouse: the wheel steps through images, clicking a side image jumps to it, clicking the
centre image toggles fill mode, right-clicking the centre image marks it, and clicking a
thumbnail jumps straight there.

### Marks

Marks are stored per folder in:

```
%LOCALAPPDATA%\ImageCuller2\marks\<folder-name>-<hash>.json
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

```
CMakeLists.txt              CMake driver (configure / build / install / cpack)
CMakePresets.json           vs2022-x64, vs2022-arm64, ninja-x64
src/
  ImageCuller2.csproj       SDK-style WinUI 3 project, unpackaged, self-contained
  app.manifest              PerMonitorV2 DPI, long paths, UTF-8
  App.xaml[.cs]             Dark theme, palette and command-bar styles
  MainWindow.xaml[.cs]      Title bar, command bar, filmstrip, dialogs, keyboard
  Controls/CarouselView.cs  The carousel
  Models/MediaItem.cs       One file: marks, thumbnail, aspect ratio (INotifyPropertyChanged)
  Services/
    MediaScanner.cs         Folder enumeration + Explorer-style natural sort
    ThumbnailService.cs     Background thumbnails, bounded concurrency
    ImageLoader.cs          Full-size decode-to-display-size, LRU cache
    MarkStore.cs            Debounced atomic JSON mark state
    FileOperations.cs       Copy / move / recycle-bin delete
  Helpers/NaturalComparer.cs
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
`OverlapFraction`, `CellWidthFraction`, `PerspectiveDistance`, `TransitionDuration` (200 ms),
`KenBurnsZoom` and `KenBurnsPeriod`.

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
stub that becomes `ImageCuller2.exe` — keeps the previous manifest while every other part
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
