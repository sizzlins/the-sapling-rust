#requires -Version 5.1
<#
    One-command proof that the native distance band matches the retail game.

    Fails loudly if any stage regresses, and - critically - fails if the
    harness itself goes blind (the mutated build must be detected).
#>
[CmdletBinding()]
param(
    [string]$Target = "x86_64-pc-windows-gnu"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Resolve-Path (Join-Path $here "..\..")
$exe = Join-Path $here "bin\Release\net48\ParityHarness.exe"
$goodDll = Join-Path $repo "target\$Target\release\sapling_rust.dll"
$badDir = Join-Path $repo "target-mutate"
$badDll = Join-Path $badDir "$Target\release\sapling_rust.dll"

function Step($name) { Write-Host ""; Write-Host "=== $name" -ForegroundColor Cyan }

function Fail($msg) {
    Write-Host "VERDICT: FAIL - $msg" -ForegroundColor Red
    exit 1
}

Step "1/7 unit tests"
Push-Location $repo
try {
    & cargo test --lib band:: --quiet
    if ($LASTEXITCODE -ne 0) { Fail "cargo test band:: failed" }
} finally { Pop-Location }

Step "2/7 build native (release)"
Push-Location $repo
try {
    & cargo build --release --quiet
    if ($LASTEXITCODE -ne 0) { Fail "cargo build --release failed" }
} finally { Pop-Location }

Step "3/7 build harness"
Push-Location $here
try {
    & dotnet build -c Release --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { Fail "dotnet build failed" }
} finally { Pop-Location }

Step "4/7 oracle fingerprint"
& $exe dump
if ($LASTEXITCODE -ne 0) { Fail "dump failed" }

Step "5/7 compare good build (must PASS)"
& $exe compare $goodDll
if ($LASTEXITCODE -ne 0) { Fail "good build does not match retail" }

Step "6/7 build mutated native (left/right gates swapped)"
Push-Location $repo
try {
    & cargo build --release --quiet --features mutate --target-dir $badDir
    if ($LASTEXITCODE -ne 0) { Fail "mutated build failed" }
} finally { Pop-Location }

Step "7/7 compare mutated build (must FAIL)"
& $exe compare $badDll | Select-Object -Last 4
$mutateDetected = ($LASTEXITCODE -ne 0)
if (-not $mutateDetected) {
    Fail "harness did not detect the mutated build - it is blind, do not trust it"
}

Write-Host ""
Write-Host "VERDICT: PASS" -ForegroundColor Green
Write-Host "  native band == retail over 943,552 cases / 58,418,176 ints"
Write-Host "  mutated build correctly rejected"
exit 0
