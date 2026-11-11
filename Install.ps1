#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$name = 'RfidLock'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$destination = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) $name
$exe = Join-Path $destination 'RfidLock.exe'

if ((Get-Service $name -ErrorAction SilentlyContinue) -or
    (Get-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction SilentlyContinue) -or
    (Test-Path -LiteralPath $destination)) {
    throw 'Ya existe una instalación o carpeta RFID Lock. Ejecute Uninstall.ps1 primero.'
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
$installedFiles = $false
$installedService = $false
$installedTask = $false
try {
    & dotnet publish (Join-Path $PSScriptRoot 'RfidLock.csproj') -c Release -r $Runtime --self-contained true `
        --source https://api.nuget.org/v3/index.json -o $staging
    if ($LASTEXITCODE -ne 0) { throw 'Falló dotnet publish.' }

    New-Item -ItemType Directory -Path $destination | Out-Null
    $installedFiles = $true
    Copy-Item -Path (Join-Path $staging '*') -Destination $destination -Recurse
    New-Service -Name $name -BinaryPathName ('"{0}" --service' -f $exe) -StartupType Automatic |
        Out-Null
    $installedService = $true
    Start-Service -Name $name

    $action = New-ScheduledTaskAction -Execute $exe -Argument '--agent'
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
    $principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings | Out-Null
    $installedTask = $true
    Start-ScheduledTask -TaskName $name -TaskPath '\'
    Write-Host "Complete la configuración en la ventana de RFID Lock."
} catch {
    if ($installedTask) { Unregister-ScheduledTask -TaskName $name -Confirm:$false }
    if ($installedService) {
        Stop-Service -Name $name -ErrorAction SilentlyContinue
        & sc.exe delete $name | Out-Null
    }
    if ($installedFiles) { Remove-Item -LiteralPath $destination -Recurse -Force }
    throw
} finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
