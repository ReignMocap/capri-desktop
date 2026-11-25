# Capri on the desktop: the release zip.
#
#   powershell -NoProfile -File tools\make_release.ps1
#
# A stand-alone build (no .NET install needed) with her art beside it (art\, from E:\VoxtaArt\CapriDesktop, made by
# tools\prep_art.py and tools\prep_face.py), her character pack (Assets\capri.voxpak) and README.txt, zipped to
# E:\VoxtaBuild\release-capri-desktop-<version>\CapriDesktop-<version>.zip.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$proj = Join-Path $repo 'CapriDesktop\CapriDesktop.csproj'
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$art = 'E:\VoxtaArt\CapriDesktop'
$out = "E:\VoxtaBuild\release-capri-desktop-$version"
$stage = Join-Path $out 'CapriDesktop'
$zip = Join-Path $out "CapriDesktop-$version.zip"

if (-not (Test-Path (Join-Path $art 'idle_2\f0.png'))) { throw "No art in ${art}: run tools\prep_art.py and tools\prep_face.py first." }
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $stage
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# her pictures, face HUD and sounds (the icon files and the box notes stay behind)
$artOut = Join-Path $stage 'art'
New-Item -ItemType Directory -Force $artOut | Out-Null
Get-ChildItem $art -Directory | ForEach-Object { Copy-Item $_.FullName (Join-Path $artOut $_.Name) -Recurse }
Copy-Item (Join-Path $PSScriptRoot 'README.txt') $stage

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
"Capri $version -> $zip ($mb MB)"
