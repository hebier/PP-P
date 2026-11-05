<#
PP-PATCH :: apply-patch.ps1
=====================================================================
Runs ONLY inside the GitHub Actions runner, on a FRESH upstream clone.

This is the replacement for "fork + git merge". The patch repo contains
ONLY patch files; the workflow clones upstream at a release tag into a
temp folder, then calls this script to:

  1. copy the 5 new .cs into the upstream source tree
  2. inject 5 <Compile Include> lines into the explicit csproj list
  3. inject 3 entry hooks (each carries a // PP-PATCH marker)
  4. verify every injection landed

DESIGN RULE: every anchor is checked. If upstream renamed/restructured a
method, FAIL LOUDLY. Never "best-effort" inject: a silently misplaced hook
produces a dll that compiles fine but falls back to the laggy legacy
pipeline -- exactly the failure that is hardest to notice.

If this script throws, send the CI log to the maintainer. The fix is to
update the anchor regex below, not to relax the check.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $UpstreamDir,
    [string] $PatchDir = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

function Read-Raw  { param([string] $p) return [System.IO.File]::ReadAllText($p) }
function Write-Raw {
    param([string] $p, [string] $t)
    [System.IO.File]::WriteAllText($p, $t, (New-Object System.Text.UTF8Encoding($false)))
}
function Get-Nl {
    param([string] $t)
    if ($t.Contains("`r`n")) { return "`r`n" } else { return "`n" }
}

$upstream = (Resolve-Path $UpstreamDir).Path
$patch    = (Resolve-Path $PatchDir).Path
$srcRoot  = Join-Path $patch    'src\ExpressPackingMonitoring'
$dstRoot  = Join-Path $upstream 'src\ExpressPackingMonitoring'
Assert-True (Test-Path $dstRoot) "upstream source tree not found: $dstRoot"

# ---------------------------------------------------------------- 1. copy
$patchFiles = @(
    'Config\AppConfig.Patch.cs',
    'Services\DirectCaptureRecordingService.cs',
    'Services\ClipByOrderService.cs',
    'Services\MjpegArchiveService.cs',
    'Services\PpPatchRuntime.cs'
)
foreach ($rel in $patchFiles) {
    $from = Join-Path $srcRoot $rel
    $to   = Join-Path $dstRoot $rel
    Assert-True (Test-Path $from) "patch file missing in patch repo: $rel"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
    Copy-Item -LiteralPath $from -Destination $to -Force
    Write-Host "  copied  $rel"
}

# -------------------------------------------------------------- 2. csproj
$csproj = Join-Path $dstRoot 'ExpressPackingMonitoring.csproj'
Assert-True (Test-Path $csproj) 'csproj not found in upstream tree'
$proj = Read-Raw $csproj
Assert-True ($proj -match 'EnableDefaultCompileItems\s*>\s*false') `
    'UPSTREAM CHANGED: csproj no longer uses an explicit <Compile Include> list. Send this log to the maintainer.'
Assert-True ($proj -notmatch 'DirectCaptureRecordingService\.cs') `
    'csproj already patched - expected a fresh upstream clone.'

$anchor = [regex]::Match($proj, '(?m)^[ \t]*<Compile Include="')
Assert-True $anchor.Success `
    'UPSTREAM CHANGED: no <Compile Include> line found in csproj. Send this log to the maintainer.'

$nl = Get-Nl $proj
$compileLines = @(
    '    <Compile Include="Config\AppConfig.Patch.cs" />',
    '    <Compile Include="Services\DirectCaptureRecordingService.cs" />',
    '    <Compile Include="Services\ClipByOrderService.cs" />',
    '    <Compile Include="Services\MjpegArchiveService.cs" />',
    '    <Compile Include="Services\PpPatchRuntime.cs" />'
)
$proj = $proj.Insert($anchor.Index, ($compileLines -join $nl) + $nl)
Write-Raw $csproj $proj
Write-Host '  csproj  +5 <Compile Include> lines'

# --------------------------------------------------------------- 3. hooks
function Inject-Hook {
    param(
        [string]   $File,
        [string]   $SignatureRegex,
        [string]   $Label,
        [string[]] $HookLines
    )
    $path = Join-Path $dstRoot $File
    Assert-True (Test-Path $path) "hook target missing upstream: $File"
    $raw = Read-Raw $path

    # Match the method declaration line AND the following opening-brace line,
    # so the hook lands as the very first statement of the method body.
    $pattern = '(?m)^[ \t]*' + $SignatureRegex + '[ \t]*\r?\n[ \t]*\{[ \t]*\r?\n'
    $match = [regex]::Match($raw, $pattern)
    if (-not $match.Success) {
        throw "UPSTREAM CHANGED - HOOK ANCHOR NOT FOUND: $Label in $File`n" +
              "Expected method matching: $SignatureRegex`n" +
              "Intentional hard failure. A misplaced hook would silently fall back to the laggy legacy pipeline.`n" +
              "Send this CI log to the maintainer; fix = update the anchor, NOT relax the check."
    }
    $nl2  = Get-Nl $raw
    $block = ($HookLines -join $nl2) + $nl2
    $raw   = $raw.Insert($match.Index + $match.Length, $block)
    Write-Raw $path $raw
    Write-Host "  hook    $Label -> $File"
}

# Both files already have `using ExpressPackingMonitoring.Services;` (verified
# on v0.0.95). Add it only if upstream removed it, so hooks always compile.
foreach ($f in @('ViewModels\MainViewModel.Camera.cs', 'ViewModels\MainViewModel.Recording.cs')) {
    $p   = Join-Path $dstRoot $f
    $raw = Read-Raw $p
    if ($raw -notmatch 'using\s+ExpressPackingMonitoring\.Services\s*;') {
        $nl3 = Get-Nl $raw
        Write-Raw $p ("using ExpressPackingMonitoring.Services;" + $nl3 + $raw)
        Write-Host "  using   added to $f"
    }
}

Inject-Hook -File 'ViewModels\MainViewModel.Camera.cs' `
    -SignatureRegex 'private\s+void\s+StartCamera\s*\(\s*\)' `
    -Label 'StartCamera' `
    -HookLines @(
        '            // PP-PATCH: direct pipeline hook (keep this marker - CI sentinel checks it)',
        '            if (Config.Patch?.IsDirect != false) { PpPatchRuntime.Start(Config); return; }'
    )

# NOTE: use CurrentOrderId, NOT _recordingOrderId. The latter is only assigned
# much later in the original method (Recording.cs:693), so at method entry it
# still holds the PREVIOUS order (or null) and BeginOrder would receive "".
Inject-Hook -File 'ViewModels\MainViewModel.Recording.cs' `
    -SignatureRegex 'private\s+async\s+Task\s+InternalStartRecordingAsync\s*\(\s*\)' `
    -Label 'InternalStartRecordingAsync' `
    -HookLines @(
        '            // PP-PATCH: direct pipeline hook (keep this marker)',
        '            if (PpPatchRuntime.DirectEnabled) { PpPatchRuntime.BeginOrder(CurrentOrderId ?? ""); return; }'
    )

Inject-Hook -File 'ViewModels\MainViewModel.Recording.cs' `
    -SignatureRegex 'private\s+async\s+Task\s+InternalStopRecordingAsync\s*\(\s*\)' `
    -Label 'InternalStopRecordingAsync' `
    -HookLines @(
        '            // PP-PATCH: direct pipeline hook (keep this marker)',
        '            if (PpPatchRuntime.DirectEnabled) { PpPatchRuntime.EndOrder(); return; }'
    )

# ------------------------------------------------------------- 4. verify
$proj = Read-Raw $csproj
foreach ($f in @('AppConfig.Patch.cs', 'DirectCaptureRecordingService.cs',
                 'ClipByOrderService.cs', 'MjpegArchiveService.cs', 'PpPatchRuntime.cs')) {
    Assert-True ($proj -match [regex]::Escape($f)) "VERIFY FAILED: $f missing from csproj"
}
foreach ($f in @('ViewModels\MainViewModel.Camera.cs', 'ViewModels\MainViewModel.Recording.cs')) {
    $raw = Read-Raw (Join-Path $dstRoot $f)
    Assert-True ($raw -match 'PP-PATCH') "VERIFY FAILED: no PP-PATCH marker in $f"
}
foreach ($f in $patchFiles) {
    Assert-True (Test-Path (Join-Path $dstRoot $f)) "VERIFY FAILED: $f not copied"
}

Write-Host 'PP-PATCH apply-patch OK (5 files + 5 compile lines + 3 hooks)'
