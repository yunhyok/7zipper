$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$portable = Join-Path $projectRoot '.tools\msvc\setup_x64.bat'
if (Test-Path -LiteralPath $portable) {
    $environmentLines = & $env:ComSpec /d /s /c "`"`"$portable`" && set`""
    if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize the native compiler.' }
    foreach ($line in $environmentLines) {
        $pos = $line.IndexOf('=')
        if ($pos -gt 0) { [Environment]::SetEnvironmentVariable($line.Substring(0, $pos), $line.Substring($pos + 1), 'Process') }
    }
} elseif (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio Build Tools with the Desktop development with C++ workload.' }
    $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $installation) { throw 'MSVC C++ tools were not found.' }
    $vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
    $environmentLines = & $env:ComSpec /d /s /c "`"`"$vcvars`" >nul && set`""
    foreach ($line in $environmentLines) {
        $pos = $line.IndexOf('=')
        if ($pos -gt 0) { [Environment]::SetEnvironmentVariable($line.Substring(0, $pos), $line.Substring($pos + 1), 'Process') }
    }
}
foreach ($relative in @('.tools\python\bin', '.tools\python\cmake\data\bin')) {
    $toolDirectory = Join-Path $projectRoot $relative
    if (Test-Path -LiteralPath $toolDirectory) { $env:PATH = $toolDirectory + ';' + $env:PATH }
}
