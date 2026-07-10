<#
.SYNOPSIS
    The fast local verification loop for False Gods.

.DESCRIPTION
    Runs, in order:
      1. SDK + configuration — the pinned SDK from global.json is present, and -Configuration is one the
                               projects actually declare (read from MSBuild, not assumed)
      2. dotnet build        — restricted project references; the compiler catches most boundary violations
      3. test projects       — every test project under tests/ (the FG-ARCH-* checks plus the unit tests),
                               told which configuration was just built
      4. whitespace checks   — git diff HEAD --check (staged AND unstaged), plus the committed range
                               BaseRef...HEAD (default origin/master) when a merge base exists: the same
                               range CI checks on a pull request. Without it, damage in an already-committed
                               file only surfaces in CI (it did once: PR #6, a Unity ProjectSettings file).

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

.PARAMETER CiSafe
    Runs the subset that needs no game install: builds only the inner assemblies (Core / Protocol /
    RuntimeContracts / Application) plus the test project, and skips architecture checks that read a
    compiled OUTER assembly (they need the game DLLs to build, which CI does not have — Docs report §6.2).

    ONE script, one source of truth: -CiSafe narrows what runs, it does not fork the logic. GitHub CI calls
    this with -CiSafe; you run it without. What CI cannot cover — the FG-ARCH-002 metadata layer and any
    check over an outer DLL — stays your responsibility locally and at L3.

.PARAMETER BaseRef
    The ref the committed-range whitespace check diffs against (BaseRef...HEAD). Default: origin/master —
    the only PR base this repository uses (CONTRIBUTING.md). When the ref is missing or shares no merge
    base with HEAD (e.g. a clone that never fetched), the range check is skipped with an explicit notice;
    the working-tree check always runs.

.EXAMPLE
    .\scripts\verify.ps1
    .\scripts\verify.ps1 -Configuration Release
    .\scripts\verify.ps1 -CiSafe            # what the GitHub workflow runs
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Debug',
    [switch] $CiSafe,
    [string] $BaseRef = 'origin/master'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'False Gods.slnx'
# Every test project under tests/ EXCEPT the Fixtures (which are input data for the architecture checks,
# not test assemblies). Discovered from disk rather than hardcoded, the same way the checks discover
# production projects from src/ (RepoLayout.ProductionProjectNames): a test project added later is covered
# automatically instead of silently going unrun.
$testProjects = @(
    Get-ChildItem -Path (Join-Path $repoRoot 'tests') -Directory |
        Where-Object { $_.Name -ne 'Fixtures' } |
        ForEach-Object { Join-Path $_.FullName ($_.Name + '.csproj') } |
        Where-Object { Test-Path $_ } |
        Sort-Object
)
if ($testProjects.Count -eq 0) {
    throw 'No test projects found under tests/. Expected at least FalseGods.ArchitectureTests.'
}
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

<#
    Runs a native command whose FAILURE is an answer, not an error: returns its stdout on exit code 0,
    $null otherwise. Same function-scoped ErrorActionPreference dance as Invoke-Native, for the same
    stderr-under-5.1 reason; stderr is additionally discarded because a probe's advisory text is noise.
#>
function Invoke-NativeQuery {
    param([Parameter(Mandatory)] [scriptblock] $Command)

    $ErrorActionPreference = 'Continue'

    $output = & $Command 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return $output
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
    Start-Step "Build ($Configuration)$(if ($CiSafe) { ' - CI-safe: inner assemblies + tests' })"

    # --disable-build-servers and -m:1 keep this working in constrained/sandboxed environments,
    # where persistent MSBuild/Roslyn server processes are unavailable or blocked.
    if ($CiSafe) {
        # The outer assemblies (UnityRuntime / Integration.* / Plugin) reference the game and BepInEx DLLs,
        # which a CI runner does not have — building the solution would fail on them. Build only what needs
        # no game: FalseGods.Application (which pulls in Core / Protocol / RuntimeContracts) and the test
        # project. That the inner four build with no game installed is the property CI exists to guard.
        $innerRoot = Join-Path $repoRoot 'src/FalseGods.Application/FalseGods.Application.csproj'
        Invoke-Native -What 'dotnet build (inner assemblies)' -Command {
            & dotnet build $innerRoot --configuration $Configuration --disable-build-servers -m:1 --nologo -v minimal
        }
        # Both test projects build with no game installed: the architecture tests reference nothing under src/,
        # and the unit tests reference only inner assemblies (Protocol -> Core). That is exactly the property CI
        # exists to guard, so both belong in the CI-safe subset.
        foreach ($testProject in $testProjects) {
            $testProjectName = [System.IO.Path]::GetFileNameWithoutExtension($testProject)
            Invoke-Native -What "dotnet build ($testProjectName)" -Command {
                & dotnet build $testProject --configuration $Configuration --disable-build-servers -m:1 --nologo -v minimal
            }
        }
    }
    else {
        Invoke-Native -What 'dotnet build' -Command {
            & dotnet build $solution --configuration $Configuration --disable-build-servers -m:1 --nologo -v minimal
        }
    }

    # ------------------------------------------------- 3. architecture tests
    Start-Step "Architecture tests ($Configuration)$(if ($CiSafe) { ' - CI-safe subset' })"

    # Tell the checks which configuration was just built, so the FG-ARCH-002 metadata check reads THAT
    # assembly and never a stale one from the other configuration. The test process inherits this.
    $env:FALSEGODS_VERIFY_CONFIGURATION = $Configuration

    # --no-build: step 2 just built these, and the FG-ARCH-002 metadata check needs those exact binaries.
    # In CI-safe mode, exclude checks that read a compiled OUTER assembly (Requires=BuiltOuterAssemblies):
    # the outer DLLs were not built above, so those checks belong to local/L3, not CI (Docs report §6.2). A
    # test with no Requires trait (every unit test) still runs under that filter, which is what we want.
    foreach ($testProject in $testProjects) {
        $testProjectName = [System.IO.Path]::GetFileNameWithoutExtension($testProject)
        $testArgs = @('test', $testProject, '--configuration', $Configuration, '--no-build', '--nologo', '-v', 'minimal')
        if ($CiSafe) {
            $testArgs += @('--filter', 'Requires!=BuiltOuterAssemblies')
        }
        Invoke-Native -What "tests ($testProjectName)" -Command {
            & dotnet @testArgs
        }
    }

    # ------------------------------------------------- 4. whitespace
    Start-Step 'Whitespace (working tree + committed range)'

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
    Write-Host '  working tree clean'

    # The committed range too — the same BaseRef...HEAD expression CI checks on a pull request
    # (.github/workflows/verify.yml). `git diff HEAD --check` is empty right after a commit, so damage in an
    # already-committed file passes every local gate and dies in CI (PR #6 did exactly that). Checking the
    # range here — and therefore in the pre-push hook, which runs this script — closes that gap before the
    # push leaves the machine. Guarded by a merge-base probe: a missing remote ref degrades to an explicit
    # notice, never to a green checkmark that checked nothing.
    $mergeBase = Invoke-NativeQuery { & git merge-base $BaseRef HEAD }
    if ($mergeBase) {
        Invoke-Native -What "git diff $BaseRef...HEAD --check (whitespace in the committed range CI checks)" -Command {
            & git -c core.safecrlf=false diff --check "$BaseRef...HEAD"
        }
        Write-Host "  committed range vs $BaseRef clean"
    }
    else {
        Write-Host "  NOTE: skipped the committed-range check - no merge base with '$BaseRef' (fetch the remote, or pass -BaseRef)" -ForegroundColor Yellow
    }

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
