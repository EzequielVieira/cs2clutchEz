<#
  Builds, tests and packages cs2clutchEz (cs2clutch.ez.api.br).
    ./build.ps1                 # build + tests + dist/app + portable zip + MSI
    ./build.ps1 -SkipTests
    ./build.ps1 -SkipInstaller
  Requires the .NET 10 SDK. FFmpeg is downloaded into tools/ffmpeg when missing.
#>
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$app = Join-Path $dist 'app'
$version = '1.0.0'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Run($exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

$sdk = (& dotnet --list-sdks) -join "`n"
if ($sdk -notmatch '(^|\n)10\.') { throw '.NET 10 SDK not found. Install it from https://dot.net (or set DOTNET_ROOT).' }

$ffmpegDir = Join-Path $root 'tools\ffmpeg'
if (-not (Test-Path (Join-Path $ffmpegDir 'ffmpeg.exe'))) {
    Step 'Downloading FFmpeg 9.0 (BtbN, GPL shared build)'
    $zip = Join-Path ([IO.Path]::GetTempPath()) 'cs2hg-ffmpeg.zip'
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip' -OutFile $zip -UseBasicParsing
    $tmp = Join-Path ([IO.Path]::GetTempPath()) 'cs2hg-ffmpeg'
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive $zip $tmp
    New-Item -ItemType Directory -Force $ffmpegDir | Out-Null
    $src = Get-ChildItem $tmp -Directory | Select-Object -First 1
    Copy-Item (Join-Path $src.FullName 'bin\*') $ffmpegDir -Force
    Copy-Item (Join-Path $src.FullName 'LICENSE.txt') $ffmpegDir -Force
    Remove-Item $zip, $tmp -Recurse -Force
}

Step "Build ($Configuration)"
Run dotnet @('build', (Join-Path $root 'CS2DemoHighlightGenerator.sln'), '-c', $Configuration, '--nologo', '-v', 'q')

if (-not $SkipTests) {
    Step 'Tests'
    Run dotnet @('test', (Join-Path $root 'tests\CS2HG.Tests\CS2HG.Tests.csproj'), '-c', $Configuration, '--no-build', '--nologo', '-v', 'q')
}

Step 'Publish self-contained win-x64 -> dist/app'
# Deleting dist/app under a running copy of the app would leave it half-replaced.
$running = Get-Process cs2clutchEz, cs2clutchez-cli -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($app, [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw "Close cs2clutchEz running from $app (process $(($running | ForEach-Object Id) -join ', ')) and run the build again." }
Remove-Item $app -Recurse -Force -ErrorAction SilentlyContinue
foreach ($project in 'src\CS2HG.UI\CS2HG.UI.csproj', 'src\CS2HG.Cli\CS2HG.Cli.csproj') {
    Run dotnet @('publish', (Join-Path $root $project), '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true',
        '-o', $app, '--nologo', '-v', 'q', '-p:DebugType=none', '-p:GenerateDocumentationFile=false')
}
Copy-Item (Join-Path $root 'README*.md'), (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD-PARTY-NOTICES.md') $app -Force
Copy-Item (Join-Path $root 'docs') (Join-Path $app 'docs') -Recurse -Force

Step 'Portable zip'
$zipOut = Join-Path $dist "cs2clutchEz-$version-portable-x64.zip"
Remove-Item $zipOut -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zipOut -CompressionLevel Optimal

if (-not $SkipInstaller) {
    Step 'MSI installer (WiX 5)'
    Run dotnet @('build', (Join-Path $root 'installer\CS2HG.Installer.wixproj'), '-c', $Configuration, '--nologo', '-v', 'q', "-p:AppDir=$app")
    $msi = Get-ChildItem (Join-Path $root 'installer\bin') -Recurse -Filter *.msi | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Copy-Item $msi.FullName $dist -Force
}

Step 'Done'
Get-ChildItem $dist -File | ForEach-Object { '{0,-55} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
