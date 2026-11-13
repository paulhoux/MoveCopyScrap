<#
.SYNOPSIS
    Builds MoveCopyScrap, packs it with Velopack and publishes it as a GitHub
    release, with a delta package from the previous release.

.DESCRIPTION
    1. Takes the version from CMakeLists.txt (project(... VERSION x.y.z)) and
       checks that HEAD is tagged vx.y.z, that the tag is pushed to GitHub, and
       that the working tree is clean, so a release is always reproducible.
    2. Builds a Release folder publish for x64 with build.ps1 (not -SingleFile:
       a single compressed .exe would make every delta as large as the app).
    3. Downloads the newest stable Velopack release from GitHub. vpk pack needs
       its full package to make the delta. Releases marked pre-release (withdrawn
       with rollback.ps1) and the older Inno Setup releases are skipped.
    4. Packs build\publish\Release with vpk: full and delta packages,
       MoveCopyScrap.App-win-Setup.exe and releases.win.json.
    5. Uploads those as a published GitHub release named after the tag.

    Installs are per-user (%LOCALAPPDATA%\MoveCopyScrap.App), with a Start Menu
    shortcut. Marks and settings stay in %LOCALAPPDATA%\MoveCopyScrap, which
    Velopack never touches, so they survive an uninstall and a copy installed
    after the old Inno Setup version picks up the marks it left behind.

    Requires the .NET SDK (for vpk) and the GitHub CLI, signed in (gh auth login)
    with write access to the repository.

.EXAMPLE
    # After bumping VERSION in CMakeLists.txt and committing:
    git tag v1.3.0
    git push origin velopack v1.3.0
    .\release.ps1

.EXAMPLE
    .\release.ps1 -Notes notes\1.3.0.md
#>
[CmdletBinding()]
param(
    # Markdown release notes, shown on GitHub and available to the app.
    [string] $Notes,
    # Pack what is already in build\publish\Release.
    [switch] $SkipBuild,
    # Upload as a draft instead of publishing.
    [switch] $Draft,
    # Allow a dirty working tree and an untagged HEAD (testing only; uploads nothing
    # unless -Upload is given as well).
    [switch] $Test,
    [switch] $Upload,
    # Velopack package id. It names the install folder and identifies the app on
    # every machine: never change it once a release is out. It must differ from
    # "MoveCopyScrap", the data folder, or uninstalling would delete the marks.
    [string] $PackId = 'MoveCopyScrap.App',
    # Must match the Velopack package version in src\MoveCopyScrap.csproj.
    [string] $VpkVersion = '1.2.161'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function Exit-Release([string] $Message) {
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit 1
}

# Runs a native command with its stderr discarded. In Windows PowerShell 5.1,
# redirected native stderr under $ErrorActionPreference = 'Stop' is a terminating
# error, so relax it here (local to this function). Check $LASTEXITCODE after.
function Invoke-Quiet([scriptblock] $Command) {
    $ErrorActionPreference = 'Continue'
    & $Command 2>$null
}

function Invoke-Checked([string] $Exe, [string[]] $Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { Exit-Release "$([IO.Path]::GetFileName($Exe)) failed with exit code $LASTEXITCODE." }
}

$root    = $PSScriptRoot
$publish = Join-Path $root 'build\publish\Release'
$outDir  = Join-Path $root 'build\releases'
$mainExe = 'MoveCopyScrap.exe'
$doUpload = -not $Test -or $Upload

# --- Repository and version ---------------------------------------------------
# The app checks the same repository: UpdateService.RepositoryUrl.
$service = Get-Content (Join-Path $root 'src\Services\UpdateService.cs') -Raw
$repoUrl = [regex]::Match($service, 'RepositoryUrl\s*=\s*"([^"]+)"').Groups[1].Value
if (-not $repoUrl) { Exit-Release 'RepositoryUrl not found in src\Services\UpdateService.cs.' }

$cmake   = Get-Content (Join-Path $root 'CMakeLists.txt') -Raw
$version = [regex]::Match($cmake, '(?s)project\(\s*MoveCopyScrap\s+VERSION\s+(\d+\.\d+\.\d+)\b').Groups[1].Value
if (-not $version) { Exit-Release 'project(MoveCopyScrap VERSION x.y.z) not found in CMakeLists.txt.' }
$tag = "v$version"

Push-Location $root
try {
    $commit = (Invoke-Quiet { git rev-parse HEAD }).Trim()
    if (-not $Test) {
        if (Invoke-Quiet { git status --porcelain --untracked-files=no }) {
            Exit-Release 'The working tree has uncommitted changes. Commit them first.'
        }
        $tagCommit = Invoke-Quiet { git rev-parse "$tag^{commit}" }
        if ($LASTEXITCODE -ne 0 -or -not $tagCommit -or $tagCommit.Trim() -ne $commit) {
            Exit-Release "HEAD is not tagged $tag (the version in CMakeLists.txt). Tag it: git tag $tag"
        }
        $remote = @(Invoke-Quiet { git ls-remote --tags origin "refs/tags/$tag" "refs/tags/$tag^{}" })
        $peeled = $remote | Where-Object { $_ -match '\^\{\}$' } | Select-Object -First 1
        $line   = if ($peeled) { $peeled } else { $remote | Select-Object -First 1 }
        if (-not $line -or ($line -split '\s+')[0] -ne $commit) {
            Exit-Release "Tag $tag is not on GitHub at HEAD. Push it: git push origin <branch> $tag"
        }
    }
} finally { Pop-Location }

# --- Tools ---------------------------------------------------------------------
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Exit-Release 'GitHub CLI (gh) not found. Install it and run gh auth login.' }
$token = Invoke-Quiet { gh auth token }
if ($LASTEXITCODE -ne 0 -or -not $token) { Exit-Release 'Not signed in to GitHub. Run gh auth login.' }
$token = $token.Trim()

if ($doUpload) {
    Invoke-Quiet { gh release view $tag --repo $repoUrl } | Out-Null
    if ($LASTEXITCODE -eq 0) { Exit-Release "Release $tag already exists. Raise VERSION in CMakeLists.txt for a new release." }
}

$toolDir = Join-Path $env:LOCALAPPDATA "MoveCopyScrap-build\vpk\$VpkVersion"
$vpk     = Join-Path $toolDir 'vpk.exe'
if (-not (Test-Path $vpk)) {
    Write-Host "Installing vpk $VpkVersion into $toolDir"
    Invoke-Checked 'dotnet' @('tool', 'install', 'vpk', '--version', $VpkVersion, '--tool-path', $toolDir)
}

Write-Host ''
Write-Host "Releasing MoveCopyScrap $version ($PackId, win-x64) to $repoUrl" -ForegroundColor Cyan
Write-Host ''

# --- Build ---------------------------------------------------------------------
if (-not $SkipBuild) {
    # build.ps1 picks vs2022-x64 or ninja-x64, both x64 (ARM64 is not released).
    Invoke-Checked 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'build.ps1'),
                                  '-Configuration', 'Release', '-NoInstall', '-NonInteractive')
}
if (-not (Test-Path (Join-Path $publish $mainExe))) { Exit-Release "$publish\$mainExe not found. Build first." }

# --- Previous release, for the delta ------------------------------------------
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory $outDir | Out-Null

Write-Host 'Downloading the latest stable release (the base for the delta)...'
& $vpk download github --repoUrl $repoUrl --token $token --outputDir $outDir --skip-updates
if ($LASTEXITCODE -ne 0) { Write-Host 'No previous Velopack release: this one ships without a delta.' -ForegroundColor Yellow }

# --- Pack ----------------------------------------------------------------------
$packArgs = @(
    'pack', '--skip-updates',
    '--packId', $PackId,
    '--packVersion', $version,
    '--packTitle', 'MoveCopyScrap',
    '--packAuthors', 'MoveCopyScrap',
    '--packDir', $publish,
    '--mainExe', $mainExe,
    '--runtime', 'win-x64',
    '--icon', (Join-Path $root 'assets\MoveCopyScrap.ico'),
    '--outputDir', $outDir,
    # Self-contained: the .NET runtime and Windows App SDK ship inside.
    '--shortcuts', 'StartMenuRoot',
    # Per-user only: no all-users MSI.
    '--instLocation', 'PerUser',
    # The zip from cpack stays the no-install option; skip Velopack's own.
    '--noPortable'
)
if ($Notes) { $packArgs += @('--releaseNotes', (Resolve-Path $Notes).Path) }
Invoke-Checked $vpk $packArgs

# --- Upload --------------------------------------------------------------------
if (-not $doUpload) {
    Write-Host ''
    Write-Host "Packed $version into $outDir (test run, nothing uploaded)." -ForegroundColor Green
    exit 0
}
$uploadArgs = @(
    'upload', 'github', '--skip-updates',
    '--repoUrl', $repoUrl,
    '--token', $token,
    '--outputDir', $outDir,
    '--tag', $tag,
    '--targetCommitish', $commit,
    '--releaseName', "MoveCopyScrap $version"
)
if (-not $Draft) { $uploadArgs += '--publish' }
Invoke-Checked $vpk $uploadArgs

Write-Host ''
Write-Host "Released $tag" -ForegroundColor Green
Write-Host "  $repoUrl/releases/tag/$tag"
Write-Host "  Installer: $outDir\$PackId-win-Setup.exe"
