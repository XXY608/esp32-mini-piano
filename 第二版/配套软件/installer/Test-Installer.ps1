param([string]$Installer, [string]$InstalledDirectory)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
if (!$Installer) { $Installer = Join-Path $workspace 'work\bin\Release\电子琴安装程序.exe' }
$testRoot = if ($InstalledDirectory) { Split-Path (Resolve-Path -LiteralPath $InstalledDirectory).Path -Parent }
    else { Join-Path $workspace ('work\installer-smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$installed = if ($InstalledDirectory) { (Resolve-Path -LiteralPath $InstalledDirectory).Path }
    else { Join-Path $testRoot '独立 安装' }
if (!$installed.StartsWith((Join-Path $workspace 'work\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installer smoke tests must use a work/ directory, never a user installation.'
}
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
function Run-Setup([string]$Path, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru -WindowStyle Hidden -Wait
    if ($process.ExitCode -ne 0) { throw "Installer returned $($process.ExitCode): $Path" }
}
$arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',('/DIR="' + $installed + '"'))
if (!$InstalledDirectory) { Run-Setup $Installer ($arguments + ('/LOG="' + (Join-Path $testRoot 'install.log') + '"')) }
$settingsPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) '电子琴\settings.json'
$settingsBefore = if (Test-Path -LiteralPath $settingsPath) { [IO.File]::ReadAllBytes($settingsPath) } else { $null }
$player = Start-Process -FilePath (Join-Path $installed '电子琴.exe') -PassThru -WindowStyle Hidden
try {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 250
        $player.Refresh()
        if ($player.HasExited) { throw 'Installed application exited during startup.' }
        if ($player.MainWindowTitle -like '*电子琴 2.1*') { break }
    }
    if ($player.MainWindowTitle -notlike '*电子琴 2.1*') { throw 'Installed self-contained WPF window did not start.' }
    $blocked = Start-Process -FilePath $Installer -ArgumentList ($arguments + ('/LOG="' + (Join-Path $testRoot 'blocked-update.log') + '"')) -PassThru -WindowStyle Hidden -Wait
    $player.Refresh()
    if ($blocked.ExitCode -eq 0 -or $player.HasExited) { throw 'Updating a running player was not safely blocked.' }
    Write-Host 'PASS installed WPF startup and running-player update protection.'
}
finally {
    if (!$player.HasExited) {
        if (!$player.CloseMainWindow() -or !$player.WaitForExit(10000)) {
            throw 'The test-owned player must be closed before completing installer tests.'
        }
    }
    if ($null -ne $settingsBefore) { [IO.File]::WriteAllBytes($settingsPath, $settingsBefore) }
    elseif (Test-Path -LiteralPath $settingsPath) { Remove-Item -LiteralPath $settingsPath }
}
$python = Join-Path $installed 'work\lyrics-alignment\python\python.exe'
$previousHome = $env:PYTHONHOME
$previousPath = $env:PYTHONPATH
$previousIoEncoding = $env:PYTHONIOENCODING
try {
    $env:PYTHONHOME = 'Z:\missing-external-python'
    $env:PYTHONPATH = 'Z:\missing-external-packages'
    $env:PYTHONIOENCODING = 'utf-8'
    & $python -B (Join-Path $PSScriptRoot 'verify_runtime.py') $installed
    if ($LASTEXITCODE -ne 0) { throw 'Installed runtime failed with external Python deliberately unavailable.' }
    $songs = Join-Path $installed '简谱'
    New-Item -ItemType Directory -Path $songs -Force | Out-Null
    $score = Join-Path $songs '保留测试_源谱 BPM：80.txt'
    $binding = Join-Path $songs '保留测试_源谱 BPM：80.lyrics.json'
    [IO.File]::WriteAllText($score, '1 2 3 4', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($binding, '{"userData":"must survive upgrade and uninstall"}')
    $hash = (Get-FileHash -LiteralPath $score -Algorithm SHA256).Hash
    Run-Setup $Installer ($arguments + ('/LOG="' + (Join-Path $testRoot 'upgrade.log') + '"'))
    if ((Get-FileHash -LiteralPath $score -Algorithm SHA256).Hash -ne $hash -or !(Test-Path -LiteralPath $binding)) {
        throw 'Upgrade changed user scores or bindings.'
    }
    Run-Setup (Join-Path $installed 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $testRoot 'uninstall.log') + '"'))
    if ((Test-Path -LiteralPath (Join-Path $installed '电子琴.exe')) -or
        (Get-FileHash -LiteralPath $score -Algorithm SHA256).Hash -ne $hash -or !(Test-Path -LiteralPath $binding)) {
        throw 'Uninstall did not remove the program while preserving user files.'
    }
    Write-Host 'PASS standalone install, isolated Python/native/model loading, upgrade, uninstall and retained user files.'
    Write-Host "Evidence: $testRoot"
}
finally {
    $env:PYTHONHOME = $previousHome
    $env:PYTHONPATH = $previousPath
    $env:PYTHONIOENCODING = $previousIoEncoding
}
