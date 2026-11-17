$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$portable = Join-Path $projectRoot '.tools\msvc\setup_x64.bat'
$compilerSetup = $null
if (Test-Path -LiteralPath $portable) {
    $compilerSetup = $portable
} elseif (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio Build Tools with the Desktop development with C++ workload.' }
    $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $installation) { throw 'MSVC C++ tools were not found.' }
    $compilerSetup = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
}
if ($compilerSetup) {
    # Pass cmd's quoted command verbatim; PowerShell 5 and 7 quote native arguments differently.
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $env:ComSpec
    $start.Arguments = '/d /s /c ""' + $compilerSetup + '" >nul && set"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $compilerProcess = [Diagnostics.Process]::Start($start)
    $output = $compilerProcess.StandardOutput.ReadToEndAsync()
    $errors = $compilerProcess.StandardError.ReadToEndAsync()
    $compilerProcess.WaitForExit()
    if ($compilerProcess.ExitCode -ne 0) { throw ('Cannot initialize MSVC: ' + $errors.Result) }
    foreach ($line in $output.Result -split "`r?`n") {
        $pos = $line.IndexOf('=')
        if ($pos -gt 0) { [Environment]::SetEnvironmentVariable($line.Substring(0, $pos), $line.Substring($pos + 1), 'Process') }
    }
    $compilerProcess.Dispose()
}
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw 'MSVC environment initialization did not expose cl.exe.' }
$env:CC = 'cl.exe'
$env:CXX = 'cl.exe'
foreach ($relative in @('.tools\python\bin', '.tools\python\cmake\data\bin')) {
    $toolDirectory = Join-Path $projectRoot $relative
    if (Test-Path -LiteralPath $toolDirectory) { $env:PATH = $toolDirectory + ';' + $env:PATH }
}
