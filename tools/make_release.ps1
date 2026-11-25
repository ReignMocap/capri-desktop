# Capri on the desktop: her app's release files.
#
#   powershell -NoProfile -File tools\make_release.ps1 [-Version 0.1.0] [-Art <folder>] [-Out <folder>] [-Installer]
#
# A stand-alone build of her app (no .NET install needed) with her art beside it (art\: her pictures, face HUD and
# sounds, made by tools\prep_art.py and tools\prep_face.py), her character pack (Assets\capri.voxpak) and README.txt:
#   <Out>\Capri-Desktop-App-<version>.zip      what her Voxta module downloads (holds a CapriDesktop folder)
#   <Out>\Capri-Desktop-Setup-<version>.exe    with -Installer: her installer (Inno Setup 6, installer\capri.iss)
# Defaults: the version in CapriDesktop.csproj, art from E:\VoxtaArt\CapriDesktop (else the repo's art\), out to
# E:\VoxtaBuild\release-capri-desktop-<version>. GitHub's release build passes its own (.github\workflows\release.yml).
param(
    [string]$Version,
    [string]$Art,
    [string]$Out,
    [switch]$Installer
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$proj = Join-Path $repo 'CapriDesktop\CapriDesktop.csproj'
if (-not $Version) { $Version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if (-not $Art) { $Art = if (Test-Path 'E:\VoxtaArt\CapriDesktop\idle_2\f0.png') { 'E:\VoxtaArt\CapriDesktop' } else { Join-Path $repo 'art' } }
if (-not $Out) { $Out = "E:\VoxtaBuild\release-capri-desktop-$Version" }
$stage = Join-Path $Out 'CapriDesktop'
$zip = Join-Path $Out "Capri-Desktop-App-$Version.zip"

if (-not (Test-Path (Join-Path $Art 'idle_2\f0.png'))) { throw "No art in ${Art}: run tools\prep_art.py and tools\prep_face.py first." }
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:Version=$Version -o $stage
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# her pictures, face HUD and sounds (the folders only: the icon files and box notes stay behind)
$artOut = Join-Path $stage 'art'
New-Item -ItemType Directory -Force $artOut | Out-Null
Get-ChildItem $Art -Directory | ForEach-Object { Copy-Item $_.FullName (Join-Path $artOut $_.Name) -Recurse }
Copy-Item (Join-Path $PSScriptRoot 'README.txt') $stage
Set-Content (Join-Path $stage 'version.txt') $Version -NoNewline   # (her Voxta module reads it)

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip
"Capri $Version -> $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"

if ($Installer) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 is not installed (ISCC.exe).' }
    & $iscc "/DAppVersion=$Version" "/DStage=$stage" "/DOutDir=$Out" (Join-Path $repo 'installer\capri.iss')
    if ($LASTEXITCODE -ne 0) { throw 'the installer build failed' }
    "Installer -> $(Join-Path $Out "Capri-Desktop-Setup-$Version.exe")"
}
