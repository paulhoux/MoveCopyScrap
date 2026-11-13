<#
.SYNOPSIS
    Withdraws a MoveCopyScrap release so installed copies go back to the newest
    remaining one, or reinstates it.

.DESCRIPTION
    Installed copies install the highest version among the stable releases on
    GitHub, and may go back to a lower one (UpdateService: AllowVersionDowngrade).
    So:

      .\rollback.ps1 -Version 1.3.0          marks v1.3.0 as a pre-release; copies
                                             on 1.3.0 go back to the previous release
                                             at their next check.
      .\rollback.ps1 -Version 1.3.0 -Undo    makes v1.3.0 stable (and latest) again.
      .\rollback.ps1 -Version 1.3.0 -Delete  deletes the release (the tag is kept).
                                             Cannot be undone.

    A downgrade downloads the full package; deltas only go forward. Ship the fix
    as a new, higher version; never re-use 1.3.0.
#>
[CmdletBinding(DefaultParameterSetName = 'Withdraw')]
param(
    [Parameter(Mandatory = $true)][string] $Version,
    [Parameter(ParameterSetName = 'Undo')][switch] $Undo,
    [Parameter(ParameterSetName = 'Delete')][switch] $Delete
)

$ErrorActionPreference = 'Stop'

$service = Get-Content (Join-Path $PSScriptRoot 'src\Services\UpdateService.cs') -Raw
$repo    = [regex]::Match($service, 'RepositoryUrl\s*=\s*"([^"]+)"').Groups[1].Value
$tag     = 'v' + ($Version -replace '^[vV]', '')

if ($Delete) {
    $answer = Read-Host "Permanently delete release $tag from $repo? Type the tag to confirm"
    if ($answer -ne $tag) { Write-Host 'Cancelled.'; exit 1 }
    gh release delete $tag --repo $repo --yes
} elseif ($Undo) {
    gh release edit $tag --repo $repo --prerelease=false --latest
} else {
    gh release edit $tag --repo $repo --prerelease --latest=false
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Stable releases installed copies now see (the highest Velopack version wins):'
gh release list --repo $repo --limit 10 --exclude-pre-releases --exclude-drafts
