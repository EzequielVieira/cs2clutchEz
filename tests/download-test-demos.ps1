# Downloads publicly available CS2 sample demos used by the integration tests.
# Source: DemoFile.Net test corpus (https://github.com/saul/demofile-net, MIT License).
# For full-match tests, point CS2HG_TEST_DEMOS to your own demos (e.g. CS2 "replays" folder),
# separated by ';' — those files stay on your machine and are never committed.
$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot 'TestData\demos'
New-Item -ItemType Directory -Force $target | Out-Null
$ProgressPreference = 'SilentlyContinue'
foreach ($name in '14155.dem', '14011.dem') {
    $out = Join-Path $target $name
    if (Test-Path $out) { Write-Host "ok   $name"; continue }
    Invoke-WebRequest "https://raw.githubusercontent.com/saul/demofile-net/main/demos/$name" -OutFile $out -UseBasicParsing
    Write-Host "got  $name"
}
