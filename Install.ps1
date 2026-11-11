#Requires -RunAsAdministrator
[CmdletBinding(DefaultParameterSetName = 'Hid')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Serial')][ValidatePattern('^COM[1-9][0-9]*$')][string]$Port,
    [Parameter(Mandatory, ParameterSetName = 'Hid')]
    [ValidatePattern('^HID\\[^\\#]+\\[^\\#]+$')][string]$HidInstance,
    [Parameter(ParameterSetName = 'Hid')][ValidateRange(1, 128)][int]$BadgeLength = 10,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string[]]$Badges,
    [Parameter(ParameterSetName = 'Serial')][ValidateRange(1, 4000000)][int]$BaudRate = 9600,
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

foreach ($badge in $Badges) {
    if ([string]::IsNullOrWhiteSpace($badge) -or $badge -notmatch '\A[ -~]{1,128}\z') {
        throw 'Cada gafete debe tener entre 1 y 128 caracteres ASCII imprimibles.'
    }
}

if ($HidInstance) {
    $device = Get-PnpDevice -InstanceId $HidInstance -PresentOnly
    if ($device.Class -ne 'Keyboard') { throw 'La ruta debe corresponder a la lectora en la clase Keyboard.' }
    foreach ($badge in $Badges) {
        if ($badge.Length -ne $BadgeLength -or $badge -cnotmatch '\A[0-9A-Z]+\z') {
            throw 'En modo HID cada gafete debe tener la longitud indicada y contener dígitos o letras A-Z mayúsculas.'
        }
    }
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
$installedFiles = $false
$installedService = $false
$installedTask = $false
try {
    & dotnet publish (Join-Path $PSScriptRoot 'RfidLock.csproj') -c Release -r $Runtime --self-contained true -o $staging
    if ($LASTEXITCODE -ne 0) { throw 'Falló dotnet publish.' }

    New-Item -ItemType Directory -Path $destination | Out-Null
    $installedFiles = $true
    Copy-Item -Path (Join-Path $staging '*') -Destination $destination -Recurse
    @{
        Port = $Port
        BaudRate = $BaudRate
        HidInstance = $HidInstance
        BadgeLength = $BadgeLength
        Badges = @($Badges)
    } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'config.json') -Encoding UTF8

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
    Write-Host "Servicio iniciado. La pantalla de bloqueo se activará al iniciar sesión como $identity."
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
