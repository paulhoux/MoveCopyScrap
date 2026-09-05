#Requires -Version 5.1
<#
.SYNOPSIS
    Builds MoveCopyScrap, installing any missing prerequisite as it goes.

.DESCRIPTION
    .NET SDK -> CMake -> a CMake generator -> Visual C++ runtime -> configure,
    build, install, shortcut.

    Works on a clean Windows machine. It never asks you to rerun after
    installing a prerequisite: see Update-Environment.

    Note what is *not* required. CMake cannot compile WinUI 3 C# itself, so
    CMakeLists.txt drives `dotnet publish` instead, and the .NET SDK carries its
    own compiler. That leaves no C++ toolchain in the picture at all: the only
    thing a generator is needed for is running one custom command, and Ninja
    does that as well as Visual Studio. So Visual Studio is used when it happens
    to be installed, and Ninja is fetched when it is not.

    NuGet packages (the Windows App SDK) are restored by `dotnet publish`
    itself and need no separate step.

.PARAMETER Preset
    CMake configure preset. Defaults to vs2022-x64 when Visual Studio 2022 with
    the C++ toolchain is installed, and ninja-x64 otherwise.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER Clean
    Delete the preset's build directory first.

.PARAMETER NoInstall
    Build only; do not install into dist\ and do not create a shortcut.

.PARAMETER NoShortcut
    Install, but do not create the Start Menu shortcut.

.PARAMETER DesktopShortcut
    Also put a shortcut on the desktop.

.PARAMETER SingleFile
    Publish one self-extracting .exe instead of a folder of ~400 files. Costs a
    slower first launch: everything is unpacked to %TEMP% before the app starts.

.PARAMETER Installer
    Also compile installer\MoveCopyScrap.iss into a Setup .exe with Inno Setup,
    installing Inno Setup from winget if it is missing. Off by default: it
    recompresses the whole ~180 MB payload, which is not something you want on
    every build.

.PARAMETER Run
    Launch the application when the build finishes.

.PARAMETER NonInteractive
    Never prompt; take the recommended default for every choice and do not
    pause on exit. For CI.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Clean -Run
    .\build.ps1 -Configuration Debug -NoInstall
    .\build.ps1 -Preset ninja-x64 -NonInteractive
#>

[CmdletBinding()]
param(
    [string] $Preset,
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [switch] $Clean,
    [switch] $NoInstall,
    [switch] $NoShortcut,
    [switch] $DesktopShortcut,
    [switch] $SingleFile,
    [switch] $Installer,
    [switch] $Run,
    [switch] $NonInteractive
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$RepoDir     = $PSScriptRoot
$PresetsFile = Join-Path $RepoDir 'CMakePresets.json'
$AppName     = 'MoveCopyScrap'
$ExeName     = 'MoveCopyScrap.exe'

# The lowest .NET major version the project's TargetFramework can build against.
$MinDotnetMajor = 8

# Declared up front: StrictMode errors on a read of an undefined variable.
$script:CMakeExe = $null
$script:InstallerPath = $null

# ================================================================
# Output
# ================================================================

function Write-Step { param([string]$Message) Write-Host "`n=== $Message ===" -ForegroundColor Cyan }
function Write-Info { param([string]$Message) Write-Host "  $Message" -ForegroundColor DarkGray }
function Write-Ok   { param([string]$Message) Write-Host "  $Message" -ForegroundColor Green }

# ================================================================
# Native commands
# ================================================================

# Run a native command, judging success by exit code alone.
#
# Two Windows PowerShell 5.1 behaviours make the plain `& exe` form unsafe
# inside this script, and this function exists to contain both:
#
#   1. Redirecting or piping a native command turns its stderr into
#      ErrorRecords, which $ErrorActionPreference = 'Stop' escalates to a
#      throw. A tool that merely warns -- winget's upgrade notice, or
#      `dotnet --list-sdks` on a runtime-only install -- would abort the script
#      instead of returning a code the caller can act on. Relaxing the
#      preference for the duration is the only reliable guard.
#   2. Inside a function whose output is captured, a native command's stdout
#      joins the function's return value rather than reaching the console.
#      Returning a single object keeps that unambiguous.
#
# -Show streams output live (use for long installs); without it the output is
# captured and returned. Note that -Show does not also capture: the two modes
# are exclusive so that progress stays live rather than buffering to the end.
function Invoke-Native {
    param(
        [Parameter(Mandatory)][string] $Exe,
        [string[]] $Arguments = @(),
        [switch] $Show
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($Show) {
            # Write-Host per line, so merged stderr renders as plain text
            # rather than red NativeCommandError blocks.
            & $Exe @Arguments 2>&1 | ForEach-Object { Write-Host "$_" }
            $captured = @()
        }
        else {
            $captured = @(& $Exe @Arguments 2>&1 |
                          Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })
        }
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $captured }
    }
    catch   { return [pscustomobject]@{ ExitCode = -1; Output = @() } }
    finally { $ErrorActionPreference = $previous }
}

# Install a package, then refresh the environment.
#
# winget does not return 0 when a package is already installed or current, so
# its exit code is advisory only -- every caller re-probes for the thing it
# actually wanted, and that probe decides success.
function Invoke-Winget {
    param(
        [Parameter(Mandatory)][string] $Id,
        [string[]] $ExtraArgs = @()
    )

    if (-not (Get-Command winget -ErrorAction Ignore)) {
        throw "winget is not available. Install 'App Installer' from the Microsoft Store, or install the prerequisite by hand and rerun."
    }

    $wingetArgs = @('install', '-e', '--id', $Id, '--source', 'winget',
                    '--accept-package-agreements', '--accept-source-agreements') + $ExtraArgs

    Write-Info "winget $($wingetArgs -join ' ')"
    $code = (Invoke-Native -Exe 'winget' -Arguments $wingetArgs -Show).ExitCode

    # 0x8A15002B no applicable upgrade / 0x8A150061 already installed / 0x8A150014 current
    if ($code -notin @(0, -1978335189, -1978335135, -1978335212)) {
        Write-Warning "winget exited with $code for $Id. Continuing -- the follow-up check decides whether this really failed."
    }

    Update-Environment
}

# ================================================================
# Environment
# ================================================================

# Re-read machine and user environment variables into this process. This is
# what removes the "please rerun" step after every install.
#
# Safe to call at any point, because:
#   - PATH is merged, never replaced, so process-only entries added by the tool
#     probes below survive.
#   - Other variables are only set if currently undefined, so an inherited or
#     deliberately-set value always outranks the registry.
function Update-Environment {
    foreach ($scope in 'Machine', 'User') {
        $vars = [Environment]::GetEnvironmentVariables($scope)
        foreach ($name in $vars.Keys) {
            if ($name -eq 'PATH') { continue }
            if ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($name, 'Process'))) {
                [Environment]::SetEnvironmentVariable($name, $vars[$name], 'Process')
            }
        }
    }

    $env:PATH = Merge-PathValue -Current $env:PATH -Additional @(
        [Environment]::GetEnvironmentVariable('PATH', 'Machine')
        [Environment]::GetEnvironmentVariable('PATH', 'User')
    )
}

# Append entries from $Additional that $Current lacks, keeping $Current's
# order and precedence. Case and a trailing backslash are ignored when
# comparing, so "C:\Tools\" and "c:\tools" are one entry.
function Merge-PathValue {
    param([string] $Current, [string[]] $Additional)

    $seen    = [System.Collections.Generic.HashSet[string]]::new()
    $ordered = [System.Collections.Generic.List[string]]::new()

    foreach ($list in @(, $Current) + $Additional) {
        if ([string]::IsNullOrWhiteSpace($list)) { continue }
        foreach ($entry in ($list -split ';')) {
            $trimmed = $entry.Trim()
            if (-not $trimmed) { continue }
            if ($seen.Add($trimmed.TrimEnd('\').ToLowerInvariant())) { $ordered.Add($trimmed) }
        }
    }
    return ($ordered -join ';')
}

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return [Security.Principal.WindowsPrincipal]::new($identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ================================================================
# Lookup
# ================================================================

# Latest Visual Studio install carrying the x64 C++ toolchain, or $null.
#
# The C++ toolchain is what matters here, not MSBuild: CMake's "Visual Studio
# 17 2022" generator emits .vcxproj files even for a LANGUAGES NONE project,
# and building those needs Microsoft.CppCommon.targets. A VS without it cannot
# host this generator, and the Ninja path is used instead.
#
# -products * matters: vswhere defaults to Community/Professional/Enterprise
# and would not report a Build Tools-only install.
function Find-VsPath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { return $null }

    $probe = Invoke-Native -Exe $vswhere -Arguments @(
        '-latest', '-products', '*',
        '-version', '[17.0,18.0)',
        '-requires', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64',
        '-property', 'installationPath')

    $found = @($probe.Output | Where-Object { $_ -and "$_".Trim() })
    if ($probe.ExitCode -eq 0 -and $found.Count -gt 0) { return "$($found[0])".Trim() }
    return $null
}

# Locate an executable, preferring PATH, then a copy bundled with Visual
# Studio, and installing it from winget as a last resort. Returns the full
# path and leaves its directory on PATH.
#
# Probing $VsDir directly matters: VS ships both cmake.exe and ninja.exe but
# does not put either on PATH, so without this a machine that already has
# everything would still trigger a download.
function Resolve-Tool {
    param(
        [Parameter(Mandatory)][string] $Exe,        # cmake.exe
        [string] $VsDir,                            # ...\CommonExtensions\Microsoft\CMake\CMake\bin
        [Parameter(Mandatory)][string] $WingetId,   # Kitware.CMake
        [scriptblock] $Validate                     # optional: { param($path) ... } -> bool
    )

    $name = [IO.Path]::GetFileNameWithoutExtension($Exe)

    $candidates = @()
    $onPath = Get-Command $Exe -ErrorAction Ignore
    if ($onPath) { $candidates += $onPath.Source }
    if ($VsDir) { $candidates += (Join-Path $VsDir $Exe) }

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        if ($Validate -and -not (& $Validate $candidate)) {
            Write-Info "$candidate is present but too old; looking further."
            continue
        }
        $dir = Split-Path -Parent $candidate
        $env:PATH = Merge-PathValue -Current $dir -Additional @($env:PATH)
        return $candidate
    }

    Write-Info "$name not found. Installing..."
    Invoke-Winget -Id $WingetId

    # PowerShell caches external command lookups, so a tool installed moments
    # ago is not always visible to Get-Command. Probe the usual roots too.
    $fresh = @()
    $again = Get-Command $Exe -ErrorAction Ignore
    if ($again) { $fresh += $again.Source }
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA)) {
        if (-not $base -or -not (Test-Path -LiteralPath $base)) { continue }
        $fresh += @(Get-ChildItem -LiteralPath $base -Filter $Exe -Recurse -File -Depth 4 -ErrorAction Ignore |
                    ForEach-Object { $_.FullName })
    }

    foreach ($candidate in $fresh) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        if ($Validate -and -not (& $Validate $candidate)) { continue }
        $dir = Split-Path -Parent $candidate
        $env:PATH = Merge-PathValue -Current $dir -Additional @($env:PATH)
        return $candidate
    }

    throw "$name was installed but $Exe could not be located. Open a new terminal and rerun."
}

# ================================================================
# Prerequisites
# ================================================================

# A dotnet.exe whose SDK list contains a new enough SDK, or $null.
#
# The distinction that matters: `dotnet --version` succeeds on a runtime-only
# install, which cannot build anything. Only `--list-sdks` proves an SDK.
function Resolve-DotnetSdk {
    $candidates = @()
    $onPath = Get-Command 'dotnet.exe' -ErrorAction Ignore
    if ($onPath) { $candidates += $onPath.Source }
    foreach ($base in @($env:ProgramFiles, $env:ProgramW6432, 'C:\Program Files')) {
        if (-not $base) { continue }
        $candidates += (Join-Path $base 'dotnet\dotnet.exe')
    }

    foreach ($candidate in $candidates) {
        if (-not $candidate -or -not (Test-Path -LiteralPath $candidate)) { continue }

        $probe = Invoke-Native -Exe $candidate -Arguments @('--list-sdks')
        if ($probe.ExitCode -ne 0) { continue }

        foreach ($line in $probe.Output) {
            # e.g. "8.0.424 [C:\Program Files\dotnet\sdk]"
            if ("$line" -match '^\s*(?<major>\d+)\.\d+\.\d+' -and
                [int]$Matches['major'] -ge $MinDotnetMajor) {
                return [pscustomobject]@{ Path = $candidate; Sdks = @($probe.Output | ForEach-Object { "$_".Trim() }) }
            }
        }
    }
    return $null
}

# True when the Visual C++ runtime the Windows App SDK links against is present.
#
# This is a genuine runtime dependency and it is not satisfied by publishing
# self-contained: the .NET runtime and the Windows App SDK are both copied next
# to the exe, but the VC runtime they were compiled against is not, and its
# absence surfaces only at launch as "the application has failed to start
# because its side-by-side configuration is incorrect".
function Test-VCRuntime {
    foreach ($dll in @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $env:SystemRoot "System32\$dll"))) { return $false }
    }
    return $true
}

# ================================================================
# Presets
# ================================================================

# binaryDir and CMAKE_INSTALL_PREFIX for a configure preset, with ${sourceDir}
# expanded. Read rather than assumed, so moving a preset's output in
# CMakePresets.json does not silently leave this script installing or
# shortcutting the wrong folder.
function Get-PresetPaths {
    param(
        [Parameter(Mandatory)][string] $PresetsFile,
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $SourceDir
    )

    if (-not (Test-Path -LiteralPath $PresetsFile)) { throw "CMakePresets.json not found at $PresetsFile" }

    $json   = Get-Content -LiteralPath $PresetsFile -Raw | ConvertFrom-Json
    $preset = $json.configurePresets | Where-Object { $_.name -eq $Name } | Select-Object -First 1
    if (-not $preset) {
        $names = ($json.configurePresets | ForEach-Object { $_.name }) -join ', '
        throw "No configure preset named '$Name' in CMakePresets.json. Available: $names"
    }

    # Each hop null-guarded: StrictMode throws on a missing property.
    $binaryDir = "$SourceDir\build"
    if ($preset.PSObject.Properties['binaryDir'] -and $preset.binaryDir) {
        $binaryDir = $preset.binaryDir.Replace('${sourceDir}', $SourceDir).Replace('/', '\')
    }

    $installPrefix = $null
    if ($preset.PSObject.Properties['cacheVariables'] -and $preset.cacheVariables -and
        $preset.cacheVariables.PSObject.Properties['CMAKE_INSTALL_PREFIX']) {
        $installPrefix = "$($preset.cacheVariables.CMAKE_INSTALL_PREFIX)".Replace('${sourceDir}', $SourceDir).Replace('/', '\')
    }

    return [pscustomobject]@{ BinaryDir = $binaryDir; InstallPrefix = $installPrefix }
}

# ================================================================
# Prompts
# ================================================================

# Options are "&Label|help text". Under -NonInteractive the first option --
# always the recommended one -- is taken, and announced so an unattended log
# still explains itself.
function Read-Choice {
    param(
        [Parameter(Mandatory)][string] $Title,
        # Not Mandatory: a mandatory [string] rejects ''.
        [AllowEmptyString()][string] $Message = '',
        [Parameter(Mandatory)][string[]] $Options
    )

    if ($NonInteractive) {
        Write-Info "-NonInteractive: choosing '$(($Options[0] -split '\|')[0])'"
        return 0
    }

    $choices = foreach ($option in $Options) {
        $label, $help = $option -split '\|', 2
        [System.Management.Automation.Host.ChoiceDescription]::new($label, $help)
    }
    return $Host.UI.PromptForChoice($Title, $Message,
        [System.Management.Automation.Host.ChoiceDescription[]]$choices, 0)
}

# ================================================================
# Inno Setup
# ================================================================

# Read a registry key's default value, or $null.
#
# Not `(Get-ItemProperty $path).'(default)'`: under StrictMode that throws
# rather than yielding $null both when the key is absent and when it exists
# with no default set. Both are normal states here.
function Get-RegistryDefault {
    param([Parameter(Mandatory)][string] $Path)

    try {
        $value = (Get-Item -LiteralPath $Path -ErrorAction Stop).GetValue('')
        if ([string]::IsNullOrWhiteSpace($value)) { return $null }
        return "$value"
    }
    catch { return $null }
}

# The executable registered to open a file extension. Callers must still check
# that it is the program they wanted -- see Get-InnoCompiler.
function Get-AssociatedExe {
    param([Parameter(Mandatory)][string] $Extension)

    $progId = Get-RegistryDefault "Registry::HKEY_CLASSES_ROOT\$Extension"
    if (-not $progId) { return $null }

    $command = Get-RegistryDefault "Registry::HKEY_CLASSES_ROOT\$progId\shell\open\command"
    if (-not $command) { return $null }

    if ($command -match '^\s*"([^"]+)"') { return $Matches[1] }
    if ($command -match '^\s*(\S+)')     { return $Matches[1] }
    return $null
}

# The Inno Setup command-line compiler, or $null.
#
# Deliberately not the .iss association alone: editors such as VS Code and
# Notepad++ commonly claim .iss, and deriving ISCC.exe from whatever owns it
# produces a path that fails confusingly at compile time without ever offering
# to install Inno Setup. Every candidate here is confirmed by the presence of
# ISCC.exe itself.
function Get-InnoCompiler {
    $onPath = Get-Command 'ISCC.exe' -ErrorAction Ignore
    if ($onPath) { return $onPath.Source }

    $uninstallRoots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')

    foreach ($root in $uninstallRoots) {
        if (-not (Test-Path $root)) { continue }
        # Each hop null-guarded: a subkey may be unreadable or carry neither
        # DisplayName nor InstallLocation, and StrictMode throws on all three.
        $entries = Get-ChildItem $root -ErrorAction Ignore |
                   ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction Ignore } |
                   Where-Object { $_ -and $_.PSObject.Properties['DisplayName'] } |
                   Where-Object { $_.DisplayName -like 'Inno Setup*' }

        foreach ($entry in $entries) {
            if (-not $entry.PSObject.Properties['InstallLocation'] -or -not $entry.InstallLocation) { continue }
            $candidate = Join-Path $entry.InstallLocation 'ISCC.exe'
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
    }

    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if (-not $base) { continue }
        foreach ($version in @('Inno Setup 6', 'Inno Setup 5')) {
            $candidate = Join-Path $base "$version\ISCC.exe"
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
    }

    $associated = Get-AssociatedExe '.iss'
    if ($associated -and (Test-Path -LiteralPath $associated)) {
        $candidate = Join-Path (Split-Path -Parent $associated) 'ISCC.exe'
        if (Test-Path -LiteralPath $candidate) { return $candidate }
        Write-Info ".iss is associated with $associated, which is not Inno Setup. Ignoring it."
    }

    return $null
}

# ================================================================
# Shortcuts
# ================================================================

function New-Shortcut {
    param(
        [Parameter(Mandatory)][string] $LinkPath,
        [Parameter(Mandatory)][string] $TargetPath,
        [string] $Description = ''
    )

    $parent = Split-Path -Parent $LinkPath
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }

    $shell    = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($LinkPath)
    $shortcut.TargetPath       = $TargetPath
    $shortcut.WorkingDirectory = Split-Path -Parent $TargetPath
    $shortcut.IconLocation     = "$TargetPath,0"
    $shortcut.Description      = $Description
    $shortcut.Save()

    [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
}

# ================================================================
# Main
# ================================================================

$exitCode = 0
try {
    Write-Host "$AppName -- build" -ForegroundColor White
    Write-Info "Repository:    $RepoDir"
    Write-Info "Configuration: $Configuration"
    if ($SingleFile) { Write-Info 'Single file:   ON' }
    if ($Installer)  { Write-Info 'Installer:     ON' }

    if (-not (Test-Path -LiteralPath (Join-Path $RepoDir 'CMakeLists.txt'))) {
        throw "CMakeLists.txt not found next to this script. Run build.bat from the repository root."
    }
    if (-not (Test-Elevated)) {
        Write-Info 'Not running as administrator. Windows will prompt for elevation if a prerequisite needs installing.'
    }

    # --- .NET SDK ---
    # The only real compiler dependency: CMake shells out to `dotnet publish`,
    # and that is what builds the XAML and the C#.
    Write-Step '.NET SDK'

    $dotnet = Resolve-DotnetSdk
    if (-not $dotnet) {
        Write-Info "No .NET $MinDotnetMajor+ SDK found. Installing..."
        Invoke-Winget -Id "Microsoft.DotNet.SDK.$MinDotnetMajor"
        $dotnet = Resolve-DotnetSdk
        if (-not $dotnet) {
            throw "The .NET SDK was installed but no SDK $MinDotnetMajor or newer is runnable. Open a new terminal and rerun."
        }
    }
    Write-Ok "Using dotnet at: $($dotnet.Path)"
    foreach ($sdk in $dotnet.Sdks) { Write-Info "SDK: $sdk" }

    # --- Generator ---
    # Decided before CMake is resolved, because Visual Studio may supply both.
    Write-Step 'CMake generator'

    $vsPath = Find-VsPath
    if (-not $Preset) {
        $Preset = if ($vsPath) { 'vs2022-x64' } else { 'ninja-x64' }
        Write-Info "No -Preset given; chose '$Preset'."
    }

    if ($vsPath) { Write-Ok "Visual Studio 2022 with the C++ toolchain: $vsPath" }
    else         { Write-Info 'No Visual Studio 2022 C++ toolchain found; the Ninja generator will be used.' }

    $vsCMakeRoot = if ($vsPath) { Join-Path $vsPath 'Common7\IDE\CommonExtensions\Microsoft\CMake' } else { $null }

    # --- CMake ---
    Write-Step 'CMake'

    # CMakeLists.txt asks for 3.21; Ninja Multi-Config needs 3.17. 3.21 covers both.
    $cmakeIsRecent = {
        param($path)
        $probe = Invoke-Native -Exe $path -Arguments @('--version')
        if ($probe.ExitCode -ne 0) { return $false }
        foreach ($line in $probe.Output) {
            if ("$line" -match 'cmake version (?<major>\d+)\.(?<minor>\d+)') {
                $version = [version]::new([int]$Matches['major'], [int]$Matches['minor'])
                return ($version -ge [version]::new(3, 21))
            }
        }
        return $false
    }

    $script:CMakeExe = Resolve-Tool -Exe 'cmake.exe' -WingetId 'Kitware.CMake' -Validate $cmakeIsRecent `
        -VsDir $(if ($vsCMakeRoot) { Join-Path $vsCMakeRoot 'CMake\bin' } else { $null })
    Write-Ok "Using CMake at: $script:CMakeExe"

    if ($Preset -like 'ninja*') {
        $ninjaExe = Resolve-Tool -Exe 'ninja.exe' -WingetId 'Ninja-build.Ninja' `
            -VsDir $(if ($vsCMakeRoot) { Join-Path $vsCMakeRoot 'Ninja' } else { $null })
        Write-Ok "Using Ninja at: $ninjaExe"
    }

    # --- Visual C++ runtime ---
    Write-Step 'Visual C++ runtime'

    if (Test-VCRuntime) {
        Write-Ok 'The Visual C++ runtime is present.'
    }
    else {
        Write-Info 'The Visual C++ runtime is missing. Installing...'
        Invoke-Winget -Id 'Microsoft.VCRedist.2015+.x64'
        if (-not (Test-VCRuntime)) {
            Write-Warning 'The Visual C++ runtime still looks incomplete. The build will continue, but the app may fail to start.'
        }
    }

    # --- Configure, build, install ---
    $paths = Get-PresetPaths -PresetsFile $PresetsFile -Name $Preset -SourceDir $RepoDir
    Write-Info "Preset:        $Preset"
    Write-Info "Build dir:     $($paths.BinaryDir)"

    Push-Location $RepoDir
    try {
        if ($Clean -and (Test-Path -LiteralPath $paths.BinaryDir)) {
            Write-Step 'Clean'
            Write-Info "Removing $($paths.BinaryDir)"
            Remove-Item -LiteralPath $paths.BinaryDir -Recurse -Force
        }

        Write-Step 'Configure'
        # Passed explicitly every time rather than left to the cache, so that the
        # switch is what decides and a stale value from an earlier configure cannot
        # quietly persist. (Spelling the -D by hand is easy to get wrong: CMake
        # accepts any unknown -D silently, so a typo just does nothing.)
        $singleFileValue = if ($SingleFile) { 'ON' } else { 'OFF' }
        & $script:CMakeExe --preset $Preset "-DMCS_SINGLE_FILE=$singleFileValue"
        if ($LASTEXITCODE -ne 0) { throw "CMake configuration failed." }

        Write-Step "Build ($Configuration)"
        # Addressed by build directory rather than by build preset, so this
        # works the same for the Visual Studio and Ninja Multi-Config presets
        # without needing a matching build preset for each.
        & $script:CMakeExe --build $paths.BinaryDir --config $Configuration
        if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    }
    finally { Pop-Location }

    $installedExe = $null
    if ($NoInstall) {
        Write-Step 'Install'
        Write-Info '-NoInstall was given; leaving the build in the publish folder.'
        $publishedExe = Join-Path $paths.BinaryDir "publish\$Configuration\$ExeName"
        if (Test-Path -LiteralPath $publishedExe) { $installedExe = $publishedExe }
    }
    else {
        Write-Step 'Install'
        & $script:CMakeExe --install $paths.BinaryDir --config $Configuration
        if ($LASTEXITCODE -ne 0) { throw "Install failed." }

        if ($paths.InstallPrefix) {
            # The install layout is flat by default, but MCS_INSTALL_BINDIR can put the app
            # in a subfolder, so look in both rather than assuming.
            foreach ($relative in @($ExeName, "bin\$ExeName")) {
                $candidate = Join-Path $paths.InstallPrefix $relative
                if (Test-Path -LiteralPath $candidate) { $installedExe = $candidate; break }
            }
        }
        if (-not $installedExe) {
            Write-Warning "Installed, but $ExeName was not found under $($paths.InstallPrefix). Skipping the shortcut."
        }
        else {
            Write-Ok "Installed to: $installedExe"
        }
    }

    # --- Shortcut ---
    # Standing in for an installer: the point is simply that the app can be
    # started without remembering where the build put it.
    if ($installedExe -and -not $NoShortcut -and -not $NoInstall) {
        Write-Step 'Shortcut'

        $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"
        New-Shortcut -LinkPath $startMenu -TargetPath $installedExe -Description "$AppName - photo and video culling"
        Write-Ok "Start Menu: $startMenu"

        if ($DesktopShortcut) {
            $desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk"
            New-Shortcut -LinkPath $desktop -TargetPath $installedExe -Description "$AppName - photo and video culling"
            Write-Ok "Desktop:    $desktop"
        }
    }

    # --- Installer ---
    if ($Installer) {
        Write-Step 'Inno Setup'

        $iscc = Get-InnoCompiler
        if (-not $iscc) {
            Write-Info 'Inno Setup not found. Installing...'
            Invoke-Winget -Id 'JRSoftware.InnoSetup' -ExtraArgs @('--override', '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART')
            $iscc = Get-InnoCompiler
            if (-not $iscc) {
                throw "Inno Setup was installed but ISCC.exe could not be located. Install it by hand and rerun."
            }
        }
        Write-Ok "Using Inno Setup compiler at: $iscc"

        if (-not $installedExe) {
            throw "Nothing to package: $ExeName was not found. Run without -NoInstall so the app is installed first."
        }

        $issFile = Join-Path $RepoDir "installer\$AppName.iss"
        if (-not (Test-Path -LiteralPath $issFile)) { throw "Installer script not found at $issFile" }

        # Whatever folder the app actually ended up in - dist\ normally, or the
        # publish folder under -NoInstall - rather than a path duplicated here.
        $payloadDir = Split-Path -Parent $installedExe
        $outDir     = Join-Path $RepoDir 'build\installer'

        Write-Step 'Compile installer'
        Write-Info "Packaging $payloadDir"
        & $iscc "/DSourceDir=$payloadDir" "/DOutputDir=$outDir" $issFile
        if ($LASTEXITCODE -ne 0) { throw "Failed to compile the installer." }

        $setup = @(Get-ChildItem -LiteralPath $outDir -Filter '*.exe' -File -ErrorAction Ignore |
                   Sort-Object LastWriteTime -Descending | Select-Object -First 1)
        if ($setup.Count -gt 0) {
            $script:InstallerPath = $setup[0].FullName
            Write-Ok "Installer: $script:InstallerPath"
        }
    }

    # --- Summary ---
    Write-Host "`nDone." -ForegroundColor Green
    if ($installedExe) {
        Write-Host "  Application: $installedExe" -ForegroundColor Green
        if (-not $NoShortcut -and -not $NoInstall) {
            Write-Host "  Start it from the Start Menu: $AppName" -ForegroundColor Green
        }
    }
    Write-Host "  Build:       $($paths.BinaryDir)" -ForegroundColor Green
    if ($script:InstallerPath) {
        Write-Host "  Installer:   $script:InstallerPath" -ForegroundColor Green
    }

    if ($Run -and $installedExe) {
        Write-Step 'Run'
        Start-Process -FilePath $installedExe -WorkingDirectory (Split-Path -Parent $installedExe)
    }
}
catch {
    Write-Host "`nERROR: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.ScriptStackTrace -and $VerbosePreference -eq 'Continue') {
        Write-Host $_.ScriptStackTrace -ForegroundColor DarkRed
    }
    $exitCode = 1
}
finally {
    if (-not $NonInteractive) {
        Write-Host ''
        Read-Host 'Press Enter to close'
    }
}

exit $exitCode
