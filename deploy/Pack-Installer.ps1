param(
    [string]$OutputDir = ".\deploy\output"
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $PSScriptRoot
Set-Location $projectDir

function Invoke-Native {
    param(
        [Parameter(Mandatory=$true)]
        [scriptblock]$Command
    )
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE"
    }
}

$payloadDir = Join-Path $env:TEMP "WmsDeliveryMobile_payload"
$payloadZip = Join-Path $PSScriptRoot "Installer\Payload.zip"
$installerProject = Join-Path $PSScriptRoot "Installer\WmsDeliveryMobileInstaller.csproj"
$outputFullPath = [IO.Path]::GetFullPath((Join-Path $projectDir $OutputDir))

if (Test-Path $payloadDir) {
    Remove-Item -LiteralPath $payloadDir -Recurse -Force
}
if (Test-Path $payloadZip) {
    Remove-Item -LiteralPath $payloadZip -Force
}
if (Test-Path $outputFullPath) {
    Remove-Item -LiteralPath $outputFullPath -Recurse -Force
}
New-Item -ItemType Directory -Path $outputFullPath -Force | Out-Null

Write-Host "Building frontend..."
$distDir = Join-Path $projectDir "dist"
if (Test-Path $distDir) {
    $resolvedDist = [IO.Path]::GetFullPath($distDir)
    $resolvedProject = [IO.Path]::GetFullPath($projectDir).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not $resolvedDist.StartsWith($resolvedProject, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete dist outside project directory: $resolvedDist"
    }
    Remove-Item -LiteralPath $resolvedDist -Recurse -Force
}
if (Test-Path package-lock.json) {
    Invoke-Native { npm ci }
} else {
    Invoke-Native { npm install }
}
Invoke-Native { npm run build }

Write-Host "Publishing web app payload..."
Invoke-Native { dotnet publish -c Release -r win-x64 --self-contained true -o $payloadDir }

Write-Host "Creating payload zip..."
Compress-Archive -Path (Join-Path $payloadDir "*") -DestinationPath $payloadZip -Force

Write-Host "Publishing setup executable..."
Invoke-Native { dotnet publish $installerProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $outputFullPath }

$setupPath = Join-Path $outputFullPath "WmsDeliveryMobile-IIS-Setup.exe"
if (-not (Test-Path $setupPath)) {
    throw "Setup executable was not generated: $setupPath"
}

$readme = @"
WMS Delivery Mobile IIS setup
=============================

Setup file:
WmsDeliveryMobile-IIS-Setup.exe

Install:
1. Copy WmsDeliveryMobile-IIS-Setup.exe to the target Windows server.
2. Right click and run as Administrator.
3. Choose action 1.
4. Input SQL Server instance, database name, authentication mode, install directory, IIS port and IIS site name.

Defaults:
- Database name: hh2j1332
- Install directory: D:\WmsDeliveryMobile
- IIS site name: WmsDeliveryMobile
- IIS port: 5189

Uninstall:
- Run WmsDeliveryMobile-IIS-Setup.exe as Administrator and choose action 2.
- Or run the generated D:\WmsDeliveryMobile\Uninstall-WmsDeliveryMobile.bat as Administrator.

Server requirements:
- Windows Server / Windows 10 / Windows 11
- IIS
- .NET 8 Hosting Bundle, for ASP.NET Core Module V2

Notes:
- The web application payload is self-contained.
- IIS still requires ASP.NET Core Module V2.
- The IIS app pool is configured as No Managed Code, so it can coexist with .NET Framework 4 IIS sites.
"@

Set-Content -LiteralPath (Join-Path $outputFullPath "README.txt") -Value $readme -Encoding UTF8

Write-Host ""
Write-Host "Setup executable created:"
Write-Host $setupPath
