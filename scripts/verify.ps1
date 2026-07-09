<#
.SYNOPSIS
    The fast local verification loop for False Gods.

.DESCRIPTION
    Runs, in order:
      1. SDK + configuration — the pinned SDK from global.json is present, and -Configuration is one the
                               projects actually declare (read from MSBuild, not assumed)
      2. dotnet build        — restricted project references; the compiler catches most boundary violations
      3. architecture tests  — the FG-ARCH-* checks, told which configuration was just built
      4. git diff HEAD --check — whitespace damage and conflict markers, staged AND unstaged

    Success and failure are decided by PROCESS EXIT CODES, never by searching output for the word
    "error". That matters more than it looks: this repository's own build prints "0 个错误" on success,
    and a grep for "错误" reports a passing build as broken. A check that can be wrong about its subject
    is worse than no check.

    Budget: about a minute. Explicitly NOT here — Unity, SULFUR, or anything needing two game instances.
    Those live at the manual pre-release level (Docs/ArchitectureEnforcement.md §4).

.PARAMETER Configuration
    Build configuration. Must be one of the configurations declared in Directory.Build.props
    (<Configurations>), which this script reads from MSBuild rather than hardcoding. Defaults to Debug,
    matching VerificationContext.DefaultConfiguration in the test project.

.EXAMPLE
    .\scripts\verify.ps1
    .\scripts\verify.ps1 -Configuration Release
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
# Any production project will do: Directory.Build.props declares <Configurations> for all of them, and
# a test asserts they agree.
$configurationProbeProject = Join-Path $repoRoot 'src/FalseGods.Core/FalseGods.Core.csproj'

$stepNumber = 0
$startedAt = Get-Date

function Start-Step {
    param([string] $Name)
    $script:stepNumber++
    Write-Host ''
    Write-Host "[$script:stepNumber/4] $Name" -ForegroundColor Cyan
}

function Stop-WithError {
    param([string] $Message)
    Write-Host ''
    Write-Host "FAILED: $Message" -ForegroundColor Red
    exit 1
}

<#
    Runs a native command and judges it ONLY by its exit code.

    $ErrorActionPreference is forced to Continue for the duration. In Windows PowerShell 5.1, when a native
    command's stderr is redirected (as any CI does), each stderr line becomes an ErrorRecord — and under
    'Stop' that terminates the script. git writes harmless advisories there ("LF will be replaced by CRLF"),
    so `git diff HEAD --check` would "fail" on a perfectly clean tree, but only when its output was piped.
    A verification script whose result depends on whether someone redirected it is not a verification script.

    The assignment is function-scoped, so cmdlet failures elsewhere still stop the run.
#>
function Invoke-Native {
    param(
        [Parameter(Mandatory)] [string] $What,
        [Parameter(Mandatory)] [scriptblock] $Command,
        [switch] $PassThru
    )

    $ErrorActionPreference = 'Continue'

    $output = & $Command
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        # Native stdout is captured above even when the caller did not ask for a return value. Replay it
        # on failure so compiler errors, failed-test details, and git diff --check findings remain visible.
        if ($output) { $output | Write-Host }
        Write-Host ''
        Write-Host "FAILED: $What (exit code $exitCode)" -ForegroundColor Red
        exit $exitCode
    }

    if ($PassThru) { return $output }
}

Push-Location $repoRoot
try {
    # ------------------------------------------------- 1. SDK and configuration
    Start-Step 'SDK and configuration'

    $sdkVersion = Invoke-Native -What 'dotnet --version (is the SDK pinned by global.json installed?)' -PassThru -Command {
        & dotnet --version
    }
    Write-Host "  .NET SDK $sdkVersion"

    # Read the supported configurations from MSBuild's evaluation, never from a list duplicated here.
    # An undeclared configuration must be rejected: MSBuild would happily evaluate and build it, and every
    # reference guarded by a real configuration would go unchecked while the run stayed green.
    $declared = Invoke-Native -What 'reading $(Configurations) from MSBuild' -PassThru -Command {
        & dotnet msbuild $configurationProbeProject -getProperty:Configurations -nologo
    }

    $allowed = @(($declared -join '') -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($allowed.Count -eq 0) {
        Stop-WithError 'MSBuild reported no $(Configurations). Check <Configurations> in Directory.Build.props.'
    }

    if ($allowed -notcontains $Configuration) {
        Stop-WithError ("Configuration '$Configuration' is not declared. Declared: {0}. " -f ($allowed -join ', ') +
                        'Add it to <Configurations> in Directory.Build.props if it is genuinely supported.')
    }

    Write-Host "  configuration $Configuration (declared: $($allowed -join ', '))"

    # ------------------------------------------------- 2. build
    Start-Step "Build ($Configuration)"

    # --disable-build-servers and -m:1 keep this working in constrained/sandboxed environments,
    # where persistent MSBuild/Roslyn server processes are unavailable or blocked.
    Invoke-Native -What 'dotnet build' -Command {
        & dotnet build $solution --configuration $Configuration --disable-build-servers -m:1 --nologo -v minimal
    }

    # ------------------------------------------------- 3. architecture tests
    Start-Step "Architecture tests ($Configuration)"

    # Tell the checks which configuration was just built, so the FG-ARCH-002 metadata check reads THAT
    # assembly and never a stale one from the other configuration. The test process inherits this.
    $env:FALSEGODS_VERIFY_CONFIGURATION = $Configuration

    # --no-build: step 2 just built it, and the metadata check needs those exact binaries.
    Invoke-Native -What 'architecture tests' -Command {
        & dotnet test $testProject --configuration $Configuration --no-build --nologo -v minimal
    }

    # ------------------------------------------------- 4. whitespace
    Start-Step 'git diff HEAD --check'

    # HEAD, not the bare working-tree diff: `git diff --check` only sees UNSTAGED changes, so whitespace
    # damage that was already `git add`-ed would sail through the very check meant to catch it before commit.
    # Covers staged + unstaged modifications to tracked files. It does NOT cover untracked files — those
    # have no diff to check, and `git add` brings them into scope.
    # -c core.safecrlf=false suppresses only git's "LF will be replaced by CRLF" advisory, which it writes to
    # stderr on every eligible file and which has nothing to do with whitespace damage. Real git errors still
    # print, and the verdict is still the exit code. Findings themselves go to stdout and are untouched.
    Invoke-Native -What 'git diff HEAD --check (whitespace damage or conflict markers, staged or unstaged)' -Command {
        & git -c core.safecrlf=false diff HEAD --check
    }
    Write-Host '  clean'

    # ------------------------------------------------- done
    $elapsed = (Get-Date) - $startedAt
    Write-Host ''
    # ASCII only: this line is read in consoles whose code page mangles non-ASCII punctuation.
    Write-Host ("OK - all 4 steps passed for $Configuration in {0:mm\:ss}" -f $elapsed) -ForegroundColor Green
    exit 0
}
finally {
    Pop-Location
}
