<#
.SYNOPSIS
    The fast local verification loop for False Gods.

.DESCRIPTION
    Runs, in order:
      1. SDK check          — the pinned SDK from global.json is present
      2. dotnet build       — restricted project references; the compiler catches most boundary violations
      3. architecture tests — the FG-ARCH-* checks in tests/FalseGods.ArchitectureTests
      4. git diff --check   — whitespace damage and conflict markers

    Success and failure are decided by PROCESS EXIT CODES, never by searching output for the word
    "error". That matters more than it looks: this repository's own build prints "0 个错误" on success,
    and a grep for "错误" reports a passing build as broken. A check that can be wrong about its subject
    is worse than no check.

    Budget: about a minute. Explicitly NOT here — Unity, SULFUR, or anything needing two game instances.
    Those live at the manual pre-release level (Docs/ArchitectureEnforcement.md §4).

.PARAMETER Configuration
    Build configuration. Defaults to Debug.

.EXAMPLE
    .\scripts\verify.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'False Gods.slnx'
$testProject = Join-Path $repoRoot 'tests/FalseGods.ArchitectureTests/FalseGods.ArchitectureTests.csproj'

$stepNumber = 0
$startedAt = Get-Date

function Start-Step {
    param([string] $Name)
    $script:stepNumber++
    Write-Host ''
    Write-Host "[$script:stepNumber/4] $Name" -ForegroundColor Cyan
}

function Assert-LastExitCode {
    param([string] $What)
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host "FAILED: $What (exit code $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

Push-Location $repoRoot
try {
    # ---------------------------------------------------------------- 1. SDK
    Start-Step 'SDK'

    $sdkVersion = & dotnet --version
    Assert-LastExitCode 'dotnet --version (is the SDK pinned by global.json installed?)'
    Write-Host "  .NET SDK $sdkVersion"

    # ---------------------------------------------------------------- 2. build
    Start-Step "Build ($Configuration)"

    # --disable-build-servers and -m:1 keep this working in constrained/sandboxed environments,
    # where persistent MSBuild/Roslyn server processes are unavailable or blocked.
    & dotnet build $solution --configuration $Configuration --disable-build-servers -m:1 --nologo -v minimal
    Assert-LastExitCode 'dotnet build'

    # ---------------------------------------------------------------- 3. architecture tests
    Start-Step 'Architecture tests'

    # --no-build: step 2 just built it, and the FG-ARCH-002 metadata check needs those exact binaries.
    & dotnet test $testProject --configuration $Configuration --no-build --nologo -v minimal
    Assert-LastExitCode 'architecture tests'

    # ---------------------------------------------------------------- 4. whitespace
    Start-Step 'git diff --check'

    & git diff --check
    Assert-LastExitCode 'git diff --check (whitespace damage or conflict markers)'
    Write-Host '  clean'

    # ---------------------------------------------------------------- done
    $elapsed = (Get-Date) - $startedAt
    Write-Host ''
    # ASCII only: this line is read in consoles whose code page mangles non-ASCII punctuation.
    Write-Host ("OK - all 4 steps passed in {0:mm\:ss}" -f $elapsed) -ForegroundColor Green
    exit 0
}
finally {
    Pop-Location
}
