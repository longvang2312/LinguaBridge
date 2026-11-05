<#
.SYNOPSIS
    Runs the LinguaBridge mobile app (Flutter) for development.

.DESCRIPTION
    Starts repos/linguabridge-mobile with `flutter run` on the chosen device. Chrome needs nothing but the browser; windows
    needs the "Desktop development with C++" workload of Visual Studio; android needs an emulator or a phone (flutter devices).

    The app finds the API (5020) and the STS (5001) by platform: the Android emulator uses 10.0.2.2, everything else
    localhost. -ApiUrl and -StsUrl override both (a real phone needs the address of this computer on your network).
    Start the backend first with ./scripts/docker-all-up.ps1.

.PARAMETER Device
    Device id for `flutter run -d`: chrome (default), windows, edge, android, or an id from `flutter devices`.

.PARAMETER ApiUrl
    Overrides the LinguaBridge API address (--dart-define=API_BASE_URL=...).

.PARAMETER StsUrl
    Overrides the STS address (--dart-define=STS_BASE_URL=...).

.PARAMETER NoPubGet
    Skip `flutter pub get`.

.PARAMETER Release
    Run in release mode instead of debug.

.EXAMPLE
    ./scripts/start-mobile.ps1

.EXAMPLE
    ./scripts/start-mobile.ps1 -Device windows

.EXAMPLE
    ./scripts/start-mobile.ps1 -Device android -ApiUrl http://192.168.1.20:5020 -StsUrl http://192.168.1.20:5001
#>
[CmdletBinding()]
param(
    [string] $Device = 'chrome',
    [string] $ApiUrl,
    [string] $StsUrl,
    [switch] $NoPubGet,
    [switch] $Release
)

$ErrorActionPreference = 'Stop'
$project = Join-Path (Split-Path -Parent $PSScriptRoot) 'repos\linguabridge-mobile'
if (-not (Test-Path (Join-Path $project 'pubspec.yaml'))) { throw "Flutter project not found at $project." }

if (-not (Get-Command flutter -ErrorAction SilentlyContinue)) {
    throw 'flutter was not found on PATH. Install Flutter (https://docs.flutter.dev/get-started/install) and open a new terminal.'
}

Push-Location $project
try {
    # flutter writes progress to stderr; success is decided by the exit code.
    if (-not $NoPubGet) {
        flutter pub get
        if ($LASTEXITCODE -ne 0) { throw "flutter pub get failed (exit code $LASTEXITCODE)." }
    }

    $arguments = @('run', '-d', $Device)
    if ($Release) { $arguments += '--release' }
    if ($ApiUrl) { $arguments += "--dart-define=API_BASE_URL=$ApiUrl" }
    if ($StsUrl) { $arguments += "--dart-define=STS_BASE_URL=$StsUrl" }

    Write-Host "flutter $($arguments -join ' ')" -ForegroundColor Cyan
    if (-not $ApiUrl -and -not $StsUrl) {
        $host_ = if ($Device -like 'android*' -or $Device -like 'emulator*') { '10.0.2.2' } else { 'localhost' }
        Write-Host "API http://${host_}:5020, STS http://${host_}:5001 (choose others with -ApiUrl / -StsUrl)" -ForegroundColor DarkGray
    }
    flutter @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "flutter run failed (exit code $LASTEXITCODE). Check the device with: flutter devices   and the toolchain with: flutter doctor"
    }
}
finally {
    Pop-Location
}
