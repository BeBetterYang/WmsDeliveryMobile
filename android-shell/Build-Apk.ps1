param(
    [string]$GradleBat = "gradle",
    [string]$AndroidSdk = $env:ANDROID_HOME,
    [string]$OutputApk = "..\deploy\output\Delivery.apk"
)

$ErrorActionPreference = "Stop"
$shellDir = $PSScriptRoot
Set-Location $shellDir

if ([string]::IsNullOrWhiteSpace($AndroidSdk)) {
    $AndroidSdk = $env:ANDROID_SDK_ROOT
}
if ([string]::IsNullOrWhiteSpace($AndroidSdk)) {
    $defaultSdk = Join-Path $env:LOCALAPPDATA "Android\Sdk"
    if (Test-Path $defaultSdk) {
        $AndroidSdk = $defaultSdk
    }
}
if ([string]::IsNullOrWhiteSpace($AndroidSdk) -or -not (Test-Path $AndroidSdk)) {
    throw "Android SDK was not found. Pass -AndroidSdk or set ANDROID_HOME."
}

$gradleCommand = Get-Command $GradleBat -ErrorAction SilentlyContinue
if (-not $gradleCommand) {
    throw "Gradle was not found. Pass -GradleBat or add gradle to PATH."
}

$env:ANDROID_HOME = $AndroidSdk
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME

& $gradleCommand.Source --no-daemon clean assembleDebug
if ($LASTEXITCODE -ne 0) {
    throw "Gradle build failed with exit code $LASTEXITCODE"
}

$source = Join-Path $shellDir "app\build\outputs\apk\debug\app-debug.apk"
if (-not (Test-Path $source)) {
    throw "APK was not generated: $source"
}

$target = [IO.Path]::GetFullPath((Join-Path $shellDir $OutputApk))
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
Copy-Item -LiteralPath $source -Destination $target -Force

Write-Host "APK created:"
Write-Host $target
