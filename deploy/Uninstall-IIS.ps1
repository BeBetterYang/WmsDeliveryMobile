param(
    [string]$SiteName,
    [string]$InstallDir,
    [int]$Port,
    [switch]$RemoveFiles,
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
        [string]$DefaultValue
    )
    if ($NonInteractive) {
        return $DefaultValue
    }
    $value = Read-Host "$Prompt [$DefaultValue]"
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $DefaultValue
    }
    return $value.Trim()
}

Assert-Admin

if (-not (Get-Module -ListAvailable -Name WebAdministration)) {
    throw "IIS WebAdministration module was not found. IIS may not be installed."
}
Import-Module WebAdministration

$SiteName = Read-Default "IIS site name" $(if ($SiteName) { $SiteName } else { "WmsDeliveryMobile" })
$InstallDir = Read-Default "Install directory" $(if ($InstallDir) { $InstallDir } else { "D:\WmsDeliveryMobile" })
$PortText = Read-Default "IIS port" $(if ($Port) { "$Port" } else { "5189" })
$Port = [int]$PortText

if (Test-Path "IIS:\Sites\$SiteName") {
    Write-Host "Removing IIS site $SiteName..."
    Stop-Website -Name $SiteName -ErrorAction SilentlyContinue
    Remove-Website -Name $SiteName
} else {
    Write-Host "IIS site not found: $SiteName"
}

if (Test-Path "IIS:\AppPools\$SiteName") {
    Write-Host "Removing app pool $SiteName..."
    Stop-WebAppPool -Name $SiteName -ErrorAction SilentlyContinue
    Remove-WebAppPool -Name $SiteName
} else {
    Write-Host "App pool not found: $SiteName"
}

$ruleName = "$SiteName $Port"
$rule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
if ($rule) {
    Write-Host "Removing firewall rule $ruleName..."
    $rule | Remove-NetFirewallRule
}

if (-not $RemoveFiles -and -not $NonInteractive) {
    $answer = Read-Host "Remove install directory $InstallDir ? Enter Y to remove, press Enter to keep"
    if ($answer.Trim().Equals("Y", [StringComparison]::OrdinalIgnoreCase)) {
        $RemoveFiles = $true
    }
}

if ($RemoveFiles) {
    if (Test-Path $InstallDir) {
        Write-Host "Removing install directory $InstallDir..."
        Remove-Item -LiteralPath $InstallDir -Recurse -Force
    } else {
        Write-Host "Install directory not found: $InstallDir"
    }
}

Write-Host ""
Write-Host "Uninstall completed."
