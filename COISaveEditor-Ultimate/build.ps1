<#
.SYNOPSIS
    Build & publish COI Save Editor - Ultimate as a self-contained desktop app.

.DESCRIPTION
    Creates a Release publish under .\publish\ with all .NET runtime bundled
    so the user does NOT need .NET 8 installed.

    Profiles:
      -Portable    Single-folder xcopy deployment (default)
      -SingleFile  Single .exe (larger, slower first launch)

.EXAMPLE
    .\build.ps1
    .\build.ps1 -SingleFile
    .\build.ps1 -Configuration Debug
#>
param(
    [switch]$SingleFile,
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime       = 'win-x64',
    [string]$OutputDir     = '.\publish'
)

$ErrorActionPreference = 'Stop'

# Resolve all paths relative to the script location so it works from any working directory
$scriptDir  = $PSScriptRoot
$project    = Join-Path $scriptDir 'COISaveEditor-Ultimate.csproj'
$OutputDir  = if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { [System.IO.Path]::GetFullPath((Join-Path $scriptDir $OutputDir)) }

if (!(Test-Path $project)) {
    Write-Host "ERROR: Cannot find project file at: $project" -ForegroundColor Red
    Write-Host "       Make sure build.ps1 is in the same folder as the .csproj" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host ("=" * 59) -ForegroundColor Cyan
Write-Host "  COI Save Editor - Ultimate  ::  Build Script" -ForegroundColor Cyan
Write-Host ("=" * 59) -ForegroundColor Cyan
Write-Host ""
Write-Host "  Project       : $project"
Write-Host "  Configuration : $Configuration"
Write-Host "  Runtime       : $Runtime"
Write-Host "  Single file   : $SingleFile"
Write-Host "  Output        : $OutputDir"
Write-Host ""

# ── Clean ────────────────────────────────────────────────────────────────
if (Test-Path $OutputDir) {
    Write-Host "[1/4] Cleaning previous publish..." -ForegroundColor Yellow
    try {
        Remove-Item $OutputDir -Recurse -Force -ErrorAction Stop
    } catch {
        Write-Host "      Warning: Could not fully clean $OutputDir (files may be locked)." -ForegroundColor DarkYellow
        Write-Host "      Continuing anyway..." -ForegroundColor DarkYellow
    }
} else {
    Write-Host "[1/4] No previous publish to clean." -ForegroundColor DarkGray
}

# ── Restore ──────────────────────────────────────────────────────────────
Write-Host "[2/4] Restoring packages..." -ForegroundColor Yellow
dotnet restore $project --runtime $Runtime --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "Restore failed (exit code $LASTEXITCODE)." }
Write-Host "      Restore complete." -ForegroundColor Green

# ── Test ─────────────────────────────────────────────────────────────────
$testProject = Join-Path $scriptDir '..\COISaveEditor-Ultimate.Tests\COISaveEditor-Ultimate.Tests.csproj'
if (Test-Path $testProject) {
    Write-Host "[3/4] Running unit tests..." -ForegroundColor Yellow
    dotnet test $testProject -c $Configuration --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed (exit code $LASTEXITCODE). Publish aborted." }
    Write-Host "      All tests passed." -ForegroundColor Green
} else {
    Write-Host "[3/4] WARNING: Test project not found at $testProject - skipping tests." -ForegroundColor DarkYellow
}

# ── Publish ──────────────────────────────────────────────────────────────
Write-Host "[4/4] Publishing ($Configuration | $Runtime | self-contained)..." -ForegroundColor Yellow

$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-f', 'net8.0-windows',
    '-r', $Runtime,
    '-o', $OutputDir,
    '--self-contained', 'true',
    '--no-restore',
    '-p:DebugType=none',
    '-p:DebugSymbols=false',
    '--verbosity', 'minimal'
)

if ($SingleFile) {
    $publishArgs += '-p:PublishSingleFile=true'
    $publishArgs += '-p:IncludeNativeLibrariesForSelfExtract=true'
    $publishArgs += '-p:EnableCompressionInSingleFile=true'
}

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit code $LASTEXITCODE)." }

# ── Summary ──────────────────────────────────────────────────────────────
$exe = Get-ChildItem (Join-Path $OutputDir 'COISaveEditor-Ultimate.exe') -ErrorAction SilentlyContinue
if ($exe) {
    $sizeMB = [math]::Round($exe.Length / 1MB, 1)
    Write-Host ""
    Write-Host "  Publish complete!" -ForegroundColor Green
    $msg = "    {0}  ({1} MB)" -f $exe.FullName, $sizeMB
    Write-Host $msg -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "  Publish complete - output in $OutputDir" -ForegroundColor Green
}

Write-Host ""
