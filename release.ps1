<#
  Makes a release of Satchel that the app's updater can install.

  1. Builds the portable app (publish.ps1 > .\release\Satchel\)
  2. Packs it into   .\release\Satchel-<version>-win-x64.zip          (your Data folder is never included)
  3. Writes          .\release\Satchel-<version>-win-x64.zip.sha256   (the checksum the updater verifies)
  4. Copies this version's notes from CHANGELOG.md into .\release\release-notes-<version>.md
  5. Tells you what to upload to GitHub (or does it, with -Publish and the GitHub CLI)

  The version comes from <Version> in src\Satchel\Satchel.csproj - raise it there first.
  Step by step: see "GitHub and update instructions.md".

  Usage:
    .\release.ps1              # build + package
    .\release.ps1 -Publish     # also create the GitHub release (needs the GitHub CLI "gh", signed in)
    .\release.ps1 -SkipBuild   # package what's already in .\release\Satchel
#>
param(
    [switch]$SkipBuild,
    [switch]$Publish,
    # Advanced/testing: package another published folder, and/or write the files somewhere else,
    # under another version number (it must still match the version of that folder's Satchel.exe).
    [string]$AppFolder,
    [string]$OutDir,
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\Satchel\Satchel.csproj"
if (-not $AppFolder) { $AppFolder = Join-Path $root "release\Satchel" }
if (-not $OutDir) { $OutDir = Join-Path $root "release" }
$resolved = Resolve-Path -LiteralPath $AppFolder -ErrorAction SilentlyContinue
if ($resolved) { $AppFolder = $resolved.Path }

# --- the version, from the project file ---
if ($Version) {
    $version = $Version.Trim()
} else {
    [xml]$csproj = Get-Content -LiteralPath $project -Raw
    $version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
    if (-not $version) { throw "No <Version> found in $project." }
    $version = $version.Trim()
}
Write-Host "Satchel $version" -ForegroundColor Cyan

# --- build ---
if (-not $SkipBuild) {
    & (Join-Path $root "publish.ps1")
    if ($LASTEXITCODE -ne 0) { throw "The build failed." }
}

$exe = Join-Path $AppFolder "Satchel.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "There's no Satchel.exe in $AppFolder - build first (run without -SkipBuild)." }
if (Test-Path -LiteralPath (Join-Path $AppFolder "Satchel.dll")) { throw "$AppFolder holds a development build (Satchel.dll). Package the published app made by publish.ps1." }

# The program must carry the same version as the release, or the updater would offer it again and again.
$fileVersion = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
$exeVersion = ($fileVersion -split '\.')[0..2] -join '.'
if ($exeVersion -ne $version) {
    throw "Satchel.exe is version $exeVersion but the project says $version. Rebuild (run without -SkipBuild)."
}

# --- the zip: everything except your data ---
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$zipName = "Satchel-$version-win-x64.zip"
$zipPath = Join-Path $OutDir $zipName
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$appRoot = (Get-Item -LiteralPath $AppFolder).FullName.TrimEnd('\')
$files = Get-ChildItem -LiteralPath $appRoot -Recurse -File | Where-Object {
    $rel = $_.FullName.Substring($appRoot.Length + 1)
    -not ($rel -like "Data\*") -and -not ($_.Name -match '\.old(-\d+)?$') -and -not ($_.Name -like "*.pdb")
}
if (-not $files) { throw "Nothing to package in $appRoot." }
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($appRoot.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, "Satchel/$rel", [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {
    $zip.Dispose()
}

# --- the checksum ---
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sumPath = "$zipPath.sha256"
[System.IO.File]::WriteAllText($sumPath, "$hash  $zipName`n")

# --- the release notes for this version, from CHANGELOG.md ---
$notesPath = Join-Path $OutDir "release-notes-$version.md"
$changelog = Join-Path $root "CHANGELOG.md"
$notes = ""
if (Test-Path -LiteralPath $changelog) {
    $inSection = $false
    foreach ($line in Get-Content -LiteralPath $changelog -Encoding UTF8) {
        if ($line -match '^##\s+\[?v?(\d+\.\d+(\.\d+)?)\]?') {
            if ($inSection) { break }
            $inSection = ($Matches[1] -eq $version)
            continue
        }
        if ($inSection) { $notes += $line + "`n" }
    }
}
$notes = $notes.Trim()
if (-not $notes) {
    $notes = "- (Describe what changed in $version - add a ""## $version"" section to CHANGELOG.md.)"
    Write-Host "CHANGELOG.md has no section for $version - the notes file is a placeholder." -ForegroundColor Yellow
}
[System.IO.File]::WriteAllText($notesPath, $notes + "`n", (New-Object System.Text.UTF8Encoding($false)))

$sizeMb = (Get-Item -LiteralPath $zipPath).Length / 1MB
Write-Host ""
Write-Host ("Packaged {0} ({1:N1} MB)" -f $zipName, $sizeMb) -ForegroundColor Green
Write-Host "  $zipPath"
Write-Host "  $sumPath"
Write-Host "  $notesPath"

if ($Publish) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "The GitHub CLI (gh) isn't installed - create the release on github.com instead (see the instructions)." }
    gh release create "v$version" $zipPath $sumPath --title "Satchel $version" --notes-file $notesPath
    if ($LASTEXITCODE -ne 0) { throw "gh couldn't create the release." }
    Write-Host "Published release v$version on GitHub." -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "Next: on GitHub open your repository > Releases > Draft a new release:" -ForegroundColor Cyan
    Write-Host "  Tag:          v$version   (create it on publish)"
    Write-Host "  Title:        Satchel $version"
    Write-Host "  Description:  paste release-notes-$version.md   (this is what Settings > Updates shows as ""What's new"")"
    Write-Host "  Attach:       $zipName  and  $zipName.sha256"
    Write-Host "  Then:         Publish release"
}
