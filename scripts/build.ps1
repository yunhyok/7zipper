param([switch]$Publish, [switch]$Test, [switch]$Installer)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source }
. "$PSScriptRoot\native-env.ps1"
& "$PSScriptRoot\fetch-native.ps1"
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'Dependency download failed.' }

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE" }
}
Invoke-Checked 'cmake' @('-S','src/Native','-B','build/native','-G','Ninja','-DCMAKE_BUILD_TYPE=Release')
Invoke-Checked 'cmake' @('--build','build/native','--parallel', [Math]::Min(8,[Environment]::ProcessorCount).ToString())
Invoke-Checked 'cmake' @('-S','src/Shell','-B','build/shell','-G','Ninja','-DCMAKE_BUILD_TYPE=Release','-DBUILD_TESTING=ON')
Invoke-Checked 'cmake' @('--build','build/shell','--parallel', [Math]::Min(8,[Environment]::ProcessorCount).ToString())
Invoke-Checked $dotnet @('build','src/App/SevenZipper.App.csproj','-c','Release','-p:Platform=AnyCPU','--nologo')
Copy-Item -LiteralPath 'build/native/SevenZipper.Native.dll','build/shell/SevenZipper.Shell.dll' -Destination 'src/App/bin/Release/net10.0-windows' -Force
if ($Test) {
    Invoke-Checked $dotnet @('run','--project','tests/SevenZipper.Tests','-c','Release','--property:Platform=AnyCPU')
    Invoke-Checked $dotnet @('run','--project','tests/SevenZipper.AppTests','-c','Release','--property:Platform=AnyCPU')
    Invoke-Checked 'ctest' @('--test-dir','build/shell','--output-on-failure')
    if (Test-Path -LiteralPath 'tests/native/smoke.py') { Invoke-Checked 'python' @('tests/native/smoke.py','build/native/SevenZipper.Native.dll') }
}
if ($Publish -or $Installer) {
    # A fresh versioned directory avoids publishing stale files from an earlier build.
    $publishDirectory = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/publish'))
    if (Test-Path -LiteralPath $publishDirectory) {
        $resolved = (Resolve-Path -LiteralPath $publishDirectory).Path
        if ($resolved -ne (Join-Path $projectRoot 'artifacts\publish')) { throw 'Unexpected publish path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    Invoke-Checked $dotnet @('publish','src/App/SevenZipper.App.csproj','-c','Release','-r','win-x64','--self-contained','true','-p:Platform=AnyCPU','-p:PublishSingleFile=false','-o',$publishDirectory,'--nologo')
    Copy-Item -LiteralPath 'build/native/SevenZipper.Native.dll','build/shell/SevenZipper.Shell.dll','LICENSE','README.md','THIRD-PARTY-NOTICES.md' -Destination $publishDirectory
    Copy-Item -LiteralPath 'assets/7zipper.ico' -Destination $publishDirectory
    $nugetDirectory = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $publishDirectory '7Zipper.runtimeconfig.json') -Raw | ConvertFrom-Json
    $licenses = Join-Path $publishDirectory 'licenses'
    New-Item -ItemType Directory -Force -Path $licenses | Out-Null
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        $package = Join-Path $nugetDirectory ($framework.name.ToLowerInvariant() + '.runtime.win-x64/' + $framework.version)
        $notices = Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '^LICENSE(\.TXT)?$|^THIRD-PARTY-NOTICES\.TXT$' }
        if (-not $notices) { throw "Runtime license missing: $package" }
        foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenses ($framework.name + '-' + $notice.Name)) }
    }
    New-Item -ItemType Directory -Force -Path 'dist' | Out-Null
    $portable = Join-Path $projectRoot 'dist/7Zipper-0.1.3-win-x64-portable.zip'
    Compress-Archive -Path "$publishDirectory\*" -DestinationPath $portable -Force
    if ($Installer) {
        $isccPath = Join-Path $projectRoot '.tools\inno\ISCC.exe'
        if (-not (Test-Path -LiteralPath $isccPath)) { $isccPath = (Get-Command ISCC.exe -ErrorAction Stop).Source }
        Invoke-Checked $isccPath @('installer/7Zipper.iss')
    }
    $checksums = Get-ChildItem -LiteralPath 'dist' -File | Where-Object { $_.Extension -in '.zip','.exe' } |
        ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name }
    $checksums | Set-Content -LiteralPath 'dist/SHA256SUMS.txt' -Encoding ascii
}
