$rootDir = "D:\00_ Projects\pfofiru\Uteplitel"
$archiveDir = Join-Path $rootDir "_archive"

if (-not (Test-Path $archiveDir)) {
    New-Item -ItemType Directory -Path $archiveDir | Out-Null
}

$foldersToMove = "ver1,ver2,ver2-ortools,ver3,ver3_ortools,ver4,ver4 резерв,ver4_gpt,ver6,ver6 - копия,_SharedForReview,GPT_TEST,tz,ver1103,ver16032026,ver17032026,ver18032026".Split(',')

foreach ($folder in $foldersToMove) {
    $folderPath = Join-Path $rootDir $folder
    if (Test-Path $folderPath) {
        Move-Item -Path $folderPath -Destination $archiveDir -Force
        Write-Host "Moved $folder to _archive/"
    }
}

$filesToMove = @("TZ.md")
foreach ($file in $filesToMove) {
    $filePath = Join-Path $rootDir $file
    if (Test-Path $filePath) {
        Move-Item -Path $filePath -Destination $archiveDir -Force
        Write-Host "Moved $file to _archive/"
    }
}

$currentDir = "D:\00_ Projects\pfofiru\Uteplitel\ver19032026"
$garbageFiles = @("blog_article.md", "test_report.md", "deploy_log.txt")

foreach ($file in $garbageFiles) {
    $filePath = Join-Path $currentDir $file
    if (Test-Path $filePath) {
        Remove-Item -Path $filePath -Force
        Write-Host "Deleted $file"
    }
}
Write-Host "Context cleanup completed."
