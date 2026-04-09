<#
.SYNOPSIS
    Sborka i deploy InsulationMasterPro v papku deploy.
.DESCRIPTION
    1. Ochischaet papku deploy
    2. Sobiraet proekt v Release x64
    3. Kopiruet neobhodimye DLL v deploy
    4. (Opcionalno) Kopiruet v papku plagina AutoCAD
    5. Vyvodit itogovyy otchyot (SHA256, timestamp, SDK version)
.PARAMETER AutoCadPluginPath
    Put k papke plagina AutoCAD. Esli ukazan - faily kopiruutsya i tuda.
.EXAMPLE
    .\deploy.ps1
    .\deploy.ps1 -AutoCadPluginPaths @("D:\path\to\plugin1", "D:\path\to\plugin2")
#>
param(
    [string[]]$AutoCadPluginPaths = @(
        "D:\Autodesk AutoCAD 2026.60.0 x64 Ru-En Portable\plagin",
        "D:\Autodesk AutoCAD 2026.60.0 x64 Ru-En Portable\plagin2"
    )
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot
$CsprojPath  = Join-Path $ProjectRoot "InsulationMasterPro.csproj"
$DeployDir   = Join-Path $ProjectRoot "deploy"
$BuildOutput = Join-Path $ProjectRoot "bin\Release"

# ===================================================
# Files for deploy (managed DLLs + PDB + deps.json)
# ===================================================
$ManagedFiles = @(
    "InsulationMasterPro.dll",
    "InsulationMasterPro.pdb",
    "InsulationMasterPro.deps.json",
    "Clipper2Lib.dll",
    "Google.OrTools.dll",
    "Google.Protobuf.dll",
    "Newtonsoft.Json.dll",
    "ClosedXML.dll",
    "ClosedXML.Parser.dll",
    "ExcelNumberFormat.dll",
    "DocumentFormat.OpenXml.dll",
    "DocumentFormat.OpenXml.Framework.dll",
    "RBush.dll",
    "SixLabors.Fonts.dll",
    "System.IO.Packaging.dll"
)

# Native DLL OrTools (win-x64)
$NativeDllRelPath = "runtimes\win-x64\native\google-ortools-native.dll"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " InsulationMasterPro Deploy Script"       -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# === Step 1: Clean deploy folder ===
Write-Host "[1/4] Cleaning deploy folder..." -ForegroundColor Yellow
if (Test-Path $DeployDir) {
    Remove-Item -Path "$DeployDir\*" -Recurse -Force
    Write-Host "      Deploy folder cleaned." -ForegroundColor Green
} else {
    New-Item -ItemType Directory -Path $DeployDir | Out-Null
    Write-Host "      Deploy folder created." -ForegroundColor Green
}

# === Step 2: Build project ===
Write-Host "[2/4] Building Release x64..." -ForegroundColor Yellow
$buildLog = dotnet build $CsprojPath -c Release -p:Platform=x64 --nologo 2>&1

if ($LASTEXITCODE -ne 0) {
    Write-Host "      BUILD ERROR! Details below:" -ForegroundColor Red
    Write-Host "      ----------------------------------------" -ForegroundColor Red
    $buildLog | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
    Write-Host "      ----------------------------------------" -ForegroundColor Red
    exit 1
}

# Show warnings count from build output
$warnings = ($buildLog | Select-String -Pattern "warning\b" | Measure-Object).Count
if ($warnings -gt 0) {
    Write-Host "      Build successful ($warnings warnings)." -ForegroundColor DarkYellow
} else {
    Write-Host "      Build successful." -ForegroundColor Green
}

# === Step 3: Copy managed DLLs ===
Write-Host "[3/4] Copying files to deploy..." -ForegroundColor Yellow
foreach ($file in $ManagedFiles) {
    $src = Join-Path $BuildOutput $file
    if (Test-Path $src) {
        Copy-Item $src -Destination $DeployDir -Force
        $fileSizeKB = [math]::Round((Get-Item $src).Length / 1KB)
        Write-Host ("      + {0} ({1} KB)" -f $file, $fileSizeKB) -ForegroundColor Green
    } else {
        Write-Host ("      x {0} -- NOT FOUND!" -f $file) -ForegroundColor Red
        exit 1
    }
}

# Native DLL
$nativeSrc = Join-Path $BuildOutput $NativeDllRelPath
if (Test-Path $nativeSrc) {
    Copy-Item $nativeSrc -Destination $DeployDir -Force
    $nativeSizeMB = [math]::Round((Get-Item $nativeSrc).Length / 1MB)
    Write-Host ("      + google-ortools-native.dll ({0} MB)" -f $nativeSizeMB) -ForegroundColor Green
} else {
    Write-Host "      x google-ortools-native.dll -- NOT FOUND!" -ForegroundColor Red
    exit 1
}

# ExcelGenerator (standalone fallback for Excel report generation)
$excelGenDir = Join-Path $ProjectRoot "tools\ExcelGenerator\publish"
if (Test-Path $excelGenDir) {
    $excelGenExe = Join-Path $excelGenDir "ExcelGenerator.exe"
    if (Test-Path $excelGenExe) {
        Copy-Item $excelGenExe -Destination $DeployDir -Force
        $exeSize = [math]::Round((Get-Item $excelGenExe).Length / 1KB)
        Write-Host ("      + ExcelGenerator.exe ({0} KB)" -f $exeSize) -ForegroundColor Green
        # Copy ExcelGenerator DLL too (needed for framework-dependent exe)
        $excelGenDll = Join-Path $excelGenDir "ExcelGenerator.dll"
        if (Test-Path $excelGenDll) {
            Copy-Item $excelGenDll -Destination $DeployDir -Force
        }
        $excelGenRuntimeCfg = Join-Path $excelGenDir "ExcelGenerator.runtimeconfig.json"
        if (Test-Path $excelGenRuntimeCfg) {
            Copy-Item $excelGenRuntimeCfg -Destination $DeployDir -Force
        }
        $excelGenDeps = Join-Path $excelGenDir "ExcelGenerator.deps.json"
        if (Test-Path $excelGenDeps) {
            Copy-Item $excelGenDeps -Destination $DeployDir -Force
        }
    } else {
        Write-Host "      ! ExcelGenerator.exe not found - run 'dotnet publish' first" -ForegroundColor DarkYellow
    }
} else {
    Write-Host "      ! ExcelGenerator publish folder not found - skipping" -ForegroundColor DarkYellow
}

# === Step 4: Copy to AutoCAD (optional) ===
if ($AutoCadPluginPaths -and $AutoCadPluginPaths.Count -gt 0) {
    Write-Host "[4/4] Copying to AutoCAD plugins..." -ForegroundColor Yellow
    foreach ($pluginPath in $AutoCadPluginPaths) {
        Write-Host "   -> Target: $pluginPath" -ForegroundColor Cyan
        if (Test-Path $pluginPath) {
            # Clean up .bak files from previous deploy
            $bakFiles = Get-ChildItem $pluginPath -Filter "*.bak" -File -ErrorAction SilentlyContinue
            if ($bakFiles) {
                $bakFiles | ForEach-Object {
                    try { Remove-Item $_.FullName -Force -ErrorAction Stop } catch { }
                }
                Write-Host "      Cleaned $($bakFiles.Count) .bak files" -ForegroundColor DarkGray
            }

            $copySuccess = $true
            $targetTotalSize = 0
            Get-ChildItem $DeployDir -File | ForEach-Object {
                $destFile = Join-Path $pluginPath $_.Name
                try {
                    Copy-Item $_.FullName -Destination $pluginPath -Force -ErrorAction Stop
                    $targetTotalSize += $_.Length
                    Write-Host ("      + {0}" -f $_.Name) -ForegroundColor Green
                } catch {
                    # File locked by AutoCAD — rename old, copy new
                    try {
                        $bakPath = "$destFile.bak"
                        if (Test-Path $bakPath) {
                            try { Remove-Item $bakPath -Force -ErrorAction Stop } catch { }
                        }
                        Rename-Item -Path $destFile -NewName "$($_.Name).bak" -Force -ErrorAction Stop
                        Copy-Item $_.FullName -Destination $pluginPath -Force -ErrorAction Stop
                        $targetTotalSize += $_.Length
                        Write-Host ("      + {0} (renamed old -> .bak)" -f $_.Name) -ForegroundColor DarkYellow
                    } catch {
                        Write-Host ("      x Failed: {0} ({1})" -f $_.Name, $_.Exception.Message) -ForegroundColor Red
                        $copySuccess = $false
                    }
                }
            }
            $targetSizeMB = [math]::Round($targetTotalSize / 1MB, 1)
            if ($copySuccess) {
                Write-Host "      Total copied: $targetSizeMB MB" -ForegroundColor Green
            } else {
                Write-Host "      ! Some files failed to copy. Copied: $targetSizeMB MB" -ForegroundColor DarkYellow
            }
        } else {
            Write-Host "      x Path does not exist: $pluginPath" -ForegroundColor DarkGray
        }
    }
} else {
    Write-Host "[4/4] AutoCAD paths not specified -- skipped." -ForegroundColor DarkGray
}

# === Summary ===
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Deploy complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan

$deployFiles = Get-ChildItem $DeployDir -File
$totalSize = [math]::Round(($deployFiles | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host " Files: $($deployFiles.Count)  |  Size: $totalSize MB"
Write-Host " Folder: $DeployDir"

# SHA256 hash of main DLL (quick version verification)
$mainDll = Join-Path $DeployDir "InsulationMasterPro.dll"
if (Test-Path $mainDll) {
    $hash = (Get-FileHash $mainDll -Algorithm SHA256).Hash.Substring(0, 16)
    Write-Host " SHA256: $hash... (InsulationMasterPro.dll)" -ForegroundColor DarkGray
}

# Build timestamp & SDK version
$buildTime = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
try { $sdkVersion = dotnet --version 2>$null } catch { $sdkVersion = "unknown" }
if (-not $sdkVersion) { $sdkVersion = "unknown" }
Write-Host " Built:  $buildTime" -ForegroundColor DarkGray
Write-Host " SDK:    $sdkVersion" -ForegroundColor DarkGray
Write-Host ""
