param([Parameter(Mandatory)][string]$Installer)

$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) { throw 'Run the installer smoke test in 64-bit PowerShell.' }
$setup = (Resolve-Path -LiteralPath $Installer).Path
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$scratch = Join-Path $root "artifacts\installer-check-$([Guid]::NewGuid().ToString('N'))"
$directory = Join-Path $scratch 'Custom 7Zipper location'
$classes = 'HKCU:\Software\Classes'
$verb = "$classes\AllFilesystemObjects\shell\7Zipper"
$clsid = '{9DF247FA-3897-4193-8F57-58062056862D}'
$registration = "$classes\CLSID\$clsid"
$progId = '7Zipper.Archive'
$uninstallEntry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8E2C4C73-1712-46F1-956D-B919014935AD}_is1'
$extensions = '.7z', '.zip'
$defaults = @{}
$logIndex = 0

function Read-RegistryValue($Path, $Name) {
    if (Test-Path -LiteralPath $Path) { (Get-Item -LiteralPath $Path).GetValue($Name) }
}

function Test-Integration($Expected) {
    if ((Test-Path -LiteralPath $verb) -ne $Expected -or
        (Test-Path -LiteralPath $registration) -ne $Expected -or
        (Test-Path -LiteralPath "$classes\$progId") -ne $Expected) {
        throw "Explorer or ProgID registration differs from expected state: $Expected"
    }
    if ($Expected -and ((Read-RegistryValue "$registration\InprocServer32" '') -ne (Join-Path $directory 'SevenZipper.Shell.dll') -or
                       (Read-RegistryValue $verb 'ExplorerCommandHandler') -ne $clsid -or
                       (Read-RegistryValue $verb 'MultiSelectModel') -ne 'Player' -or
                       (Read-RegistryValue "$classes\$progId\shell\open\command" '') -ne
                           ('"' + (Join-Path $directory '7Zipper.exe') + '" "%1"'))) {
        throw 'Explorer registration does not point to the expected server.'
    }
    foreach ($extension in $extensions) {
        $typeKey = "$classes\$extension"
        $openWithKey = "$typeKey\OpenWithProgids"
        $registered = (Test-Path -LiteralPath $openWithKey) -and $progId -in (Get-Item -LiteralPath $openWithKey).GetValueNames()
        if ($registered -ne $Expected -or (Read-RegistryValue $typeKey '') -cne $defaults[$extension]) {
            throw "Open with registration or the default app was changed unexpectedly: $extension"
        }
    }
}

function Invoke-InstallerProcess($Executable, [string[]]$Arguments) {
    $log = Join-Path $scratch "run-$([int]++$script:logIndex).log"
    $run = Start-Process -FilePath $Executable -ArgumentList ($Arguments + ('/LOG="' + $log + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($run.ExitCode -ne 0) { throw "$Executable exit code $($run.ExitCode). See $log" }
    Remove-Item -LiteralPath $log
}

if (Test-Path -LiteralPath $uninstallEntry) { throw 'Existing per-user 7Zipper installation; skipped smoke test.' }
foreach ($extension in $extensions) { $defaults[$extension] = Read-RegistryValue "$classes\$extension" '' }
Test-Integration $false
$silent = '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
$setupFlags = $silent + '/CURRENTUSER', '/SP-', '/NOICONS', '/NOCLOSEAPPLICATIONS'
$uninstaller = Join-Path $directory 'unins000.exe'
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    Invoke-InstallerProcess $setup ($setupFlags + '/TASKS=contextmenu,openwith', ('/DIR="' + $directory + '"'))
    foreach ($file in '7Zipper.exe','SevenZipper.Native.dll','SevenZipper.Shell.dll','7zipper.ico','THIRD-PARTY-NOTICES.md') {
        if (-not (Test-Path -LiteralPath (Join-Path $directory $file) -PathType Leaf)) { throw "Missing installed file: $file" }
    }
    Test-Integration $true
    Invoke-InstallerProcess $setup ($setupFlags + '/MERGETASKS=!contextmenu,!openwith')
    Test-Integration $false
    Invoke-InstallerProcess $setup ($setupFlags + '/TASKS=contextmenu,openwith')
    Test-Integration $true
}
finally {
    if (Test-Path -LiteralPath $uninstaller) { Invoke-InstallerProcess $uninstaller $silent }
    if ((Test-Path -LiteralPath $scratch) -and -not (Get-ChildItem -Force -LiteralPath $scratch)) {
        Remove-Item -LiteralPath $scratch
    }
}
Test-Integration $false
if ((Test-Path -LiteralPath $directory) -or (Test-Path -LiteralPath $uninstallEntry)) { throw 'Uninstall left registered installation data.' }
'Installer smoke passed: custom per-user path, task upgrades, default-app preservation and uninstall.'
