<#
.SYNOPSIS
    Build, deploy & restart AutoCAD in one command.
.DESCRIPTION
    1. Builds InsulationMasterPro + ExcelGenerator
    2. Gracefully closes AutoCAD (waits for save prompt)
    3. Deploys to plagin folders
    4. Restarts AutoCAD with optional drawing + startup script
.PARAMETER NoBuild
    Skip build step (just restart + deploy).
.PARAMETER NoRestart
    Build & deploy only, don't touch AutoCAD.
.PARAMETER Drawing
    Path to .dwg to open on startup.
.PARAMETER AutoRun
    AutoCAD command to run after startup (e.g. "INS").
.PARAMETER ForceKill
    Kill AutoCAD without waiting for graceful close.
.EXAMPLE
    .\dev.ps1                                    # build + deploy + restart
    .\dev.ps1 -Drawing "tests\f_test.dxf"        # restart with test drawing
    .\dev.ps1 -Drawing "tests\f_test.dxf" -AutoRun "INS"  # restart + auto-run INS
    .\dev.ps1 -NoBuild                           # just restart (skip build)
    .\dev.ps1 -NoRestart                         # just build + deploy
#>
param(
    [switch]$NoBuild,
    [switch]$NoRestart,
    [string]$Drawing = "",
    [string]$AutoRun = "",
    [switch]$ForceKill,
    [int]$GracefulTimeoutSec = 30
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot
$DeployDir   = Join-Path $ProjectRoot "deploy"

$AutoCadExe = "D:\Autodesk AutoCAD 2026.60.0 x64 Ru-En Portable\AutoCAD-Ru.exe"
$PluginPaths = @(
    "D:\Autodesk AutoCAD 2026.60.0 x64 Ru-En Portable\plagin",
    "D:\Autodesk AutoCAD 2026.60.0 x64 Ru-En Portable\plagin2"
)

function Write-Step($step, $msg) { Write-Host "[$step] $msg" -ForegroundColor Yellow }
function Write-Ok($msg) { Write-Host "  OK: $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "  !! $msg" -ForegroundColor DarkYellow }

Write-Host ""
Write-Host "=== InsulationMasterPro Dev Cycle ===" -ForegroundColor Cyan
Write-Host ""

$sw = [System.Diagnostics.Stopwatch]::StartNew()

# =========================================
# STEP 1: BUILD
# =========================================
if (-not $NoBuild) {
    Write-Step "1" "Building..."

    $buildLog = dotnet build (Join-Path $ProjectRoot "InsulationMasterPro.csproj") `
        -c Release -p:Platform=x64 --nologo --verbosity quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  BUILD FAILED:" -ForegroundColor Red
        $buildLog | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        exit 1
    }
    Write-Ok "Plugin built"

    $excelGenProj = Join-Path $ProjectRoot "tools\ExcelGenerator\ExcelGenerator.csproj"
    if (Test-Path $excelGenProj) {
        $pubLog = dotnet publish $excelGenProj -c Release -r win-x64 `
            --self-contained false -o (Join-Path $ProjectRoot "tools\ExcelGenerator\publish") `
            --nologo --verbosity quiet 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Ok "ExcelGenerator published"
        } else {
            Write-Warn "ExcelGenerator publish failed (non-critical)"
        }
    }
} else {
    Write-Step "1" "Build skipped (-NoBuild)"
}

# =========================================
# STEP 2: CLOSE AutoCAD
# =========================================
if (-not $NoRestart) {
    Write-Step "2" "Closing AutoCAD..."

    $acadProcs = Get-Process -Name "ACad" -ErrorAction SilentlyContinue
    if ($acadProcs) {
        if ($ForceKill) {
            $acadProcs | Stop-Process -Force
            Write-Ok "AutoCAD force-killed"
        } else {
            foreach ($p in $acadProcs) {
                try { $p.CloseMainWindow() | Out-Null } catch { }
            }
            Write-Host "  Waiting for AutoCAD to close (${GracefulTimeoutSec}s)..." -ForegroundColor Gray

            $waited = 0
            while ($waited -lt $GracefulTimeoutSec) {
                Start-Sleep -Seconds 2
                $waited += 2
                $still = Get-Process -Name "ACad" -ErrorAction SilentlyContinue
                if (-not $still) { break }
                Write-Host "    ...waiting ($waited s)" -ForegroundColor DarkGray
            }

            $still = Get-Process -Name "ACad" -ErrorAction SilentlyContinue
            if ($still) {
                Write-Warn "AutoCAD didn't close gracefully - force killing"
                $still | Stop-Process -Force
                Start-Sleep -Seconds 2
            }
        }

        # Also wait for launcher process
        $launchers = Get-Process -Name "AutoCAD-Ru" -ErrorAction SilentlyContinue
        if ($launchers) {
            Start-Sleep -Seconds 3
            $launchers = Get-Process -Name "AutoCAD-Ru" -ErrorAction SilentlyContinue
            if ($launchers) { $launchers | Stop-Process -Force -ErrorAction SilentlyContinue }
        }

        Write-Ok "AutoCAD closed"
    } else {
        Write-Ok "AutoCAD not running"
    }
} else {
    Write-Step "2" "Restart skipped (-NoRestart)"
}

# =========================================
# STEP 3: DEPLOY
# =========================================
Write-Step "3" "Deploying..."

# Run existing deploy logic inline (simplified - just copy from build output)
$BuildOutput = Join-Path $ProjectRoot "bin\Release"
if (-not (Test-Path $DeployDir)) { New-Item -ItemType Directory -Path $DeployDir | Out-Null }
Remove-Item -Path "$DeployDir\*" -Recurse -Force -ErrorAction SilentlyContinue

$ManagedFiles = @(
    "InsulationMasterPro.dll", "InsulationMasterPro.pdb", "InsulationMasterPro.deps.json",
    "Clipper2Lib.dll", "Google.OrTools.dll", "Google.Protobuf.dll", "Newtonsoft.Json.dll",
    "ClosedXML.dll", "ClosedXML.Parser.dll", "ExcelNumberFormat.dll",
    "DocumentFormat.OpenXml.dll", "DocumentFormat.OpenXml.Framework.dll",
    "RBush.dll", "SixLabors.Fonts.dll", "System.IO.Packaging.dll"
)

$copied = 0
foreach ($f in $ManagedFiles) {
    $src = Join-Path $BuildOutput $f
    if (Test-Path $src) { Copy-Item $src -Destination $DeployDir -Force; $copied++ }
}

$native = Join-Path $BuildOutput "runtimes\win-x64\native\google-ortools-native.dll"
if (Test-Path $native) { Copy-Item $native -Destination $DeployDir -Force; $copied++ }

# ExcelGenerator
$excelGenPub = Join-Path $ProjectRoot "tools\ExcelGenerator\publish"
foreach ($ef in @("ExcelGenerator.exe","ExcelGenerator.dll","ExcelGenerator.runtimeconfig.json","ExcelGenerator.deps.json")) {
    $efPath = Join-Path $excelGenPub $ef
    if (Test-Path $efPath) { Copy-Item $efPath -Destination $DeployDir -Force; $copied++ }
}

# Copy to plugin folders
foreach ($pluginPath in $PluginPaths) {
    if (Test-Path $pluginPath) {
        # Clean .bak files
        Get-ChildItem $pluginPath -Filter "*.bak" -File -ErrorAction SilentlyContinue |
            ForEach-Object { try { Remove-Item $_.FullName -Force } catch { } }

        Get-ChildItem $DeployDir -File | ForEach-Object {
            try {
                Copy-Item $_.FullName -Destination $pluginPath -Force -ErrorAction Stop
            } catch {
                $destFile = Join-Path $pluginPath $_.Name
                try {
                    if (Test-Path "$destFile.bak") { Remove-Item "$destFile.bak" -Force -ErrorAction SilentlyContinue }
                    Rename-Item -Path $destFile -NewName "$($_.Name).bak" -Force -ErrorAction Stop
                    Copy-Item $_.FullName -Destination $pluginPath -Force -ErrorAction Stop
                } catch {
                    Write-Warn "Failed: $($_.Name)"
                }
            }
        }
    }
}

Write-Ok "$copied files deployed to plagin folders"

# =========================================
# STEP 4: START AutoCAD
# =========================================
if (-not $NoRestart) {
    Write-Step "4" "Starting AutoCAD..."

    # Resolve drawing path
    $drawingArg = ""
    if ($Drawing) {
        $drawingPath = $Drawing
        if (-not [System.IO.Path]::IsPathRooted($drawingPath)) {
            $drawingPath = Join-Path $ProjectRoot $drawingPath
        }
        if (Test-Path $drawingPath) {
            $drawingArg = "`"$drawingPath`""
        } else {
            Write-Warn "Drawing not found: $drawingPath"
        }
    }

    # Create startup script if AutoRun specified
    $scrArg = ""
    if ($AutoRun -and $drawingArg) {
        $scrPath = Join-Path $env:TEMP "ins_autorun.scr"
        # Script: wait a bit, then run command
        $scrContent = @"
(command "_.DELAY" 3000)
$AutoRun

"@
        Set-Content -Path $scrPath -Value $scrContent -Encoding ASCII
        $scrArg = "/b `"$scrPath`""
    }

    $args = @()
    if ($drawingArg) { $args += $drawingArg }
    if ($scrArg) { $args += $scrArg }

    $argsStr = $args -join " "
    Start-Process -FilePath $AutoCadExe -ArgumentList $argsStr
    Write-Ok "AutoCAD started$(if($Drawing){' with '+$Drawing})$(if($AutoRun){' + auto-run: '+$AutoRun})"
} else {
    Write-Step "4" "Start skipped (-NoRestart)"
}

$sw.Stop()
Write-Host ""
Write-Host "=== Done in $([math]::Round($sw.Elapsed.TotalSeconds, 1))s ===" -ForegroundColor Green
Write-Host ""
