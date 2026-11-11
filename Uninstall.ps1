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
$exe = Join-Path $destination 'RfidLock.exe'

# Setup runs independently of the scheduled task and can keep DLLs open.
Get-Process -Name $name -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe } |
    ForEach-Object {
        if (-not $_.HasExited) {
            try {
                Stop-Process -InputObject $_ -Force
            } catch {
                if (-not $_.HasExited) { throw }
            }
            if (-not $_.WaitForExit(5000)) {
                throw 'RfidLock.exe sigue abierto. Cierre el proceso y ejecute Uninstall.ps1 de nuevo.'
            }
        }
    }

for ($attempt = 1; $attempt -le 5; $attempt++) {
    if (-not (Test-Path -LiteralPath $destination)) { break }
    try {
        Remove-Item -LiteralPath $destination -Recurse -Force
        break
    } catch {
        if ($attempt -eq 5 -and (Test-Path -LiteralPath $destination)) { throw }
        Start-Sleep -Milliseconds 400
    }
}
