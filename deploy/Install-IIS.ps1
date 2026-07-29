param(
    [string]$DbServer,
    [string]$DbName,
    [string]$DbUser,
    [string]$DbPassword,
    [string]$InstallDir,
    [int]$Port,
    [string]$SiteName,
    [switch]$UseSqlAuth,
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Please run this script as Administrator."
    }
}

function Read-Default {
    param(
        [string]$Prompt,
        [string]$DefaultValue,
        [switch]$Secure
    )
    if ($NonInteractive) {
        return $DefaultValue
    }
    if ($Secure) {
        $secureValue = Read-Host "$Prompt" -AsSecureString
        if ($secureValue.Length -eq 0) {
            return $DefaultValue
        }
        $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureValue)
        try {
            return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
        } finally {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
        }
    }
    $value = Read-Host "$Prompt [$DefaultValue]"
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $DefaultValue
    }
    return $value.Trim()
}

function Test-Command {
    param([string]$Name)
    return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

function Ensure-Iis {
    if (-not (Get-Module -ListAvailable -Name WebAdministration)) {
        Write-Host "Enabling IIS features..."
        $features = @(
            "IIS-WebServerRole",
            "IIS-WebServer",
            "IIS-ManagementConsole",
            "IIS-StaticContent",
            "IIS-DefaultDocument",
            "IIS-HttpErrors",
            "IIS-HttpLogging",
            "IIS-RequestFiltering",
            "IIS-ISAPIExtensions",
            "IIS-ISAPIFilter"
        )
        foreach ($feature in $features) {
            Enable-WindowsOptionalFeature -Online -FeatureName $feature -All -NoRestart | Out-Null
        }
    }
    Import-Module WebAdministration

    $ancm = Get-WebGlobalModule -Name "AspNetCoreModuleV2" -ErrorAction SilentlyContinue
    if (-not $ancm) {
        throw "ASP.NET Core Module V2 was not found. Install .NET 8 Hosting Bundle first, then run this script again."
    }
}

function Stop-PortOwner {
    param([int]$ListenPort)
    $listeners = Get-NetTCPConnection -LocalPort $ListenPort -State Listen -ErrorAction SilentlyContinue
    foreach ($listener in $listeners) {
        if ($listener.OwningProcess -eq 4) {
            continue
        }
        $process = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
        if ($process -and ($process.ProcessName -in @("dotnet", "WmsDeliveryMobile"))) {
            Write-Host "Port $ListenPort is used by $($process.ProcessName)($($process.Id)); stopping it..."
            Stop-Process -Id $process.Id -Force
            Start-Sleep -Seconds 1
            continue
        }
        throw "Port $ListenPort is used by process $($listener.OwningProcess). Release it or choose another IIS port."
    }
}

function Set-AppSettings {
    param(
        [string]$Path,
        [string]$ConnectionString,
        [int]$ListenPort
    )
    $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if (-not $json.ConnectionStrings) {
        $json | Add-Member -MemberType NoteProperty -Name ConnectionStrings -Value ([pscustomobject]@{})
    }
    $json.ConnectionStrings | Add-Member -MemberType NoteProperty -Name Wms -Value $ConnectionString -Force
    $json.Urls = "http://0.0.0.0:$ListenPort"
    $json.AllowedHosts = "*"
    $json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8
}

Assert-Admin

$projectDir = Split-Path -Parent $PSScriptRoot
Set-Location $projectDir

$DbServer = Read-Default "Database server" $(if ($DbServer) { $DbServer } else { "." })
$DbName = Read-Default "Database name" $(if ($DbName) { $DbName } else { "hh2j1332" })
$InstallDir = Read-Default "Install directory" $(if ($InstallDir) { $InstallDir } else { "D:\WmsDeliveryMobile" })
$SiteName = Read-Default "IIS site name" $(if ($SiteName) { $SiteName } else { "WmsDeliveryMobile" })
$PortText = Read-Default "IIS port" $(if ($Port) { "$Port" } else { "5189" })
$Port = [int]$PortText

$projectFullPath = [IO.Path]::GetFullPath($projectDir).TrimEnd('\')
$installFullPath = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
if ($installFullPath.Equals($projectFullPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Install directory cannot be the source directory: $projectFullPath"
}

if (-not $UseSqlAuth -and -not $NonInteractive) {
    $auth = Read-Host "DB auth: enter S for SQL auth, press Enter for Windows integrated auth"
    if ($auth.Trim().Equals("S", [StringComparison]::OrdinalIgnoreCase)) {
        $UseSqlAuth = $true
    }
}

if ($UseSqlAuth) {
    $DbUser = Read-Default "Database user" $(if ($DbUser) { $DbUser } else { "sa" })
    $DbPassword = Read-Default "Database password" $(if ($DbPassword) { $DbPassword } else { "" }) -Secure
    if ([string]::IsNullOrWhiteSpace($DbPassword)) {
        throw "Database password is required when SQL auth is enabled."
    }
    $connectionString = "Server=$DbServer;Database=$DbName;User ID=$DbUser;Password=$DbPassword;TrustServerCertificate=True;Encrypt=False"
} else {
    $connectionString = "Server=$DbServer;Database=$DbName;Integrated Security=True;TrustServerCertificate=True;Encrypt=False"
}

if (-not (Test-Command "dotnet")) {
    throw "dotnet command was not found. Install .NET 8 SDK first."
}
if (-not (Test-Command "npm")) {
    throw "npm command was not found. Install Node.js first."
}

Ensure-Iis
Stop-PortOwner -ListenPort $Port

if (Test-Path "IIS:\Sites\$SiteName") {
    Write-Host "Removing existing IIS site $SiteName..."
    Stop-Website -Name $SiteName -ErrorAction SilentlyContinue
    Remove-Website -Name $SiteName
}
if (Test-Path "IIS:\AppPools\$SiteName") {
    Write-Host "Removing existing app pool $SiteName..."
    Stop-WebAppPool -Name $SiteName -ErrorAction SilentlyContinue
    Remove-WebAppPool -Name $SiteName
}

Write-Host "Building frontend..."
if (Test-Path package-lock.json) {
    npm ci
} else {
    npm install
}
npm run build

$publishTemp = Join-Path $env:TEMP "WmsDeliveryMobile_publish"
if (Test-Path $publishTemp) {
    Remove-Item -LiteralPath $publishTemp -Recurse -Force
}

Write-Host "Publishing .NET self-contained package..."
dotnet publish -c Release -r win-x64 --self-contained true -o $publishTemp

if (Test-Path $InstallDir) {
    Write-Host "Cleaning install directory $InstallDir..."
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
}
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $publishTemp "*") -Destination $InstallDir -Recurse -Force

$appSettingsPath = Join-Path $InstallDir "appsettings.json"
Set-AppSettings -Path $appSettingsPath -ConnectionString $connectionString -ListenPort $Port

if (-not (Test-Path "IIS:\AppPools\$SiteName")) {
    New-WebAppPool -Name $SiteName | Out-Null
}
Set-ItemProperty "IIS:\AppPools\$SiteName" -Name managedRuntimeVersion -Value ""
Set-ItemProperty "IIS:\AppPools\$SiteName" -Name managedPipelineMode -Value "Integrated"
Set-ItemProperty "IIS:\AppPools\$SiteName" -Name enable32BitAppOnWin64 -Value $false
Set-ItemProperty "IIS:\AppPools\$SiteName" -Name processModel.identityType -Value "ApplicationPoolIdentity"

Write-Host "Granting app pool permissions..."
$appPoolIdentity = "IIS AppPool\$SiteName"
& icacls $InstallDir /grant "${appPoolIdentity}:(OI)(CI)(M)" /T | Out-Null

Write-Host "Creating IIS site..."
New-Website -Name $SiteName -Port $Port -PhysicalPath $InstallDir -ApplicationPool $SiteName | Out-Null

$ruleName = "$SiteName $Port"
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow | Out-Null
}

Start-WebAppPool -Name $SiteName
Start-Website -Name $SiteName

Write-Host ""
Write-Host "IIS deployment completed."
Write-Host "Site name: $SiteName"
Write-Host "Install directory: $InstallDir"
Write-Host "Local URL: http://localhost:$Port/"
Write-Host "LAN URL: http://SERVER_IP:$Port/"
Write-Host ""
Write-Host "Note: This project is a .NET 8 app. It cannot run on .NET Framework 4, but this IIS deployment can coexist with .NET 4 sites."
