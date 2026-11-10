<#
  Builds the portable release of Satchel.

  Output: .\release\Satchel\   (copy this whole folder to your flash drive)

  The release is self-contained: the target PC does NOT need .NET installed.
  Your data lives in .\release\Satchel\Data\satchel.db (created on first run).

  Usage:
    .\publish.ps1                       # self-contained, ~165 MB, starts in ~1 s, lowest memory use
    .\publish.ps1 -Compress             # self-contained, ~80 MB, but slower start and ~2x memory
    .\publish.ps1 -FrameworkDependent   # tiny build that needs the .NET 9 Desktop Runtime installed
#>
param(
    [switch]$Compress,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\Satchel\Satchel.csproj"
$out = Join-Path $root "release\Satchel"

# Never overwrite user data: only the app files are replaced.
if (Test-Path $out) {
    Get-ChildItem $out -Exclude "Data" | Remove-Item -Recurse -Force
}

if ($FrameworkDependent) {
    dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o $out
} else {
    $compression = if ($Compress) { "true" } else { "false" }
    dotnet publish $project -c Release -p:PublishProfile=Portable -p:EnableCompressionInSingleFile=$compression -o $out
}

if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

# Tidy up: package documentation files and the duplicate native WebView2 loader folder aren't needed at runtime.
Get-ChildItem $out -Filter "*.xml" -File | Remove-Item -Force
$duplicate = Join-Path $out "runtimes"
if ((Test-Path (Join-Path $out "WebView2Loader.dll")) -and (Test-Path $duplicate)) { Remove-Item -Recurse -Force $duplicate }

$size = (Get-ChildItem $out -Recurse -File | Where-Object { $_.FullName -notlike "*\Data\*" } | Measure-Object Length -Sum).Sum / 1MB
Write-Host ""
Write-Host ("Done. {0:N1} MB in {1}" -f $size, $out) -ForegroundColor Green
Write-Host "Copy the whole 'Satchel' folder to your flash drive and run Satchel.exe."
