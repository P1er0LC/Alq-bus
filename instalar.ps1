# Instala Depas en %LOCALAPPDATA%\Programs\Depas, con accesos en el escritorio y en el menú Inicio.
# Se puede volver a correr para actualizar: cierra la app si está abierta (los avisos y la sesión de Facebook se quedan).
$ErrorActionPreference = 'Stop'
$destino = Join-Path $env:LOCALAPPDATA 'Programs\Depas'
Get-Process Depas -ErrorAction SilentlyContinue | Stop-Process
dotnet publish (Join-Path $PSScriptRoot 'Depas.csproj') -c Release -o $destino --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar Depas.' }
$shell = New-Object -ComObject WScript.Shell
$carpetas = @([Environment]::GetFolderPath('Desktop'), (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'))
foreach ($carpeta in $carpetas) {
    $acceso = $shell.CreateShortcut((Join-Path $carpeta 'Depas.lnk'))
    $acceso.TargetPath = Join-Path $destino 'Depas.exe'
    $acceso.WorkingDirectory = $destino
    $acceso.IconLocation = (Join-Path $destino 'Depas.exe') + ',0'
    $acceso.Description = 'Busca departamentos en alquiler en Marketplace, Adondevivir y Urbania'
    $acceso.Save()
}
"Instalado en $destino"
