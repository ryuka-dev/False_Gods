<#
.SYNOPSIS
    One-time developer setup for this clone: activate the version-controlled git hooks.

.DESCRIPTION
    Points git at the repository's .githooks directory (core.hooksPath), which activates
    .githooks/pre-push - the local gate that runs scripts/verify.ps1 before every push.

    The setting is LOCAL to this clone (git config --local): it is written to this repo's .git/config and
    affects nothing else on the machine. It is idempotent - run it as many times as you like. You need it
    once per clone; run it again after a fresh clone, on a new machine, or if the repo's .git config is
    lost or reset.

    It changes no tracked files, deploys nothing, and touches no game install or Gale profile.

.EXAMPLE
    .\scripts\setup-dev.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

function Stop-Setup {
    param([string] $Message)
    Write-Host ''
    Write-Host "setup-dev: $Message" -ForegroundColor Red
    exit 1
}

Push-Location $repoRoot
try {
    # 1. This must be a git working tree.
    $null = & git rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -ne 0) {
        Stop-Setup "this is not a git repository. Run from a clone of False Gods (the folder with 'False Gods.slnx')."
    }

    # 2. The hook we are about to activate must actually be present, or we would point git at nothing.
    $hookPath = Join-Path $repoRoot '.githooks/pre-push'
    if (-not (Test-Path -LiteralPath $hookPath)) {
        Stop-Setup ".githooks/pre-push is missing. Is the working tree fully checked out? Expected: $hookPath"
    }

    # 3. Install (idempotent). A relative value keeps it portable across machines and path names.
    & git config --local core.hooksPath .githooks
    if ($LASTEXITCODE -ne 0) {
        Stop-Setup "git config --local core.hooksPath failed (exit $LASTEXITCODE)."
    }

    # 4. Read it back and confirm the final value, rather than trusting the write.
    $actual = (& git config --local --get core.hooksPath)
    if ($LASTEXITCODE -ne 0 -or $actual -ne '.githooks') {
        Stop-Setup "core.hooksPath reads back as '$actual', expected '.githooks'. Hooks are NOT active."
    }

    Write-Host ''
    Write-Host "setup-dev: core.hooksPath = $actual (local to this clone)." -ForegroundColor Green
    Write-Host "setup-dev: pre-push verification is active. 'git push' now runs scripts/verify.ps1 first."
    Write-Host "setup-dev: set up once per clone; re-run after a fresh clone or if .git config is lost."
    exit 0
}
finally {
    Pop-Location
}
