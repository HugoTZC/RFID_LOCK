#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$name = 'RfidLock'

if (Get-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction SilentlyContinue) {
    Disable-ScheduledTask -TaskName $name -TaskPath '\' | Out-Null
    Stop-ScheduledTask -TaskName $name -TaskPath '\'
    Unregister-ScheduledTask -TaskName $name -TaskPath '\' -Confirm:$false
}

if (Get-Service -Name $name -ErrorAction SilentlyContinue) {
    Stop-Service -Name $name
    & sc.exe delete $name
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo eliminar el servicio.' }
}

$destination = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) $name
if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
