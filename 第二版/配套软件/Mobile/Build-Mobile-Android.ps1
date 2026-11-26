$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $workRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $bundledDotnet = Join-Path $workRoot 'dotnet-sdk\dotnet.exe'
    # The workspace SDK is intentionally kept for the Windows app and does
    # not contain MAUI. Prefer the machine SDK installed by Visual Studio.
    $systemDotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    $systemSdkList = if ($systemDotnetCommand) { & $systemDotnetCommand.Source --list-sdks 2>$null | Out-String } else { '' }
    $dotnet = if ($systemDotnetCommand -and -not [string]::IsNullOrWhiteSpace($systemSdkList)) { $systemDotnetCommand.Source }
        elseif (Test-Path -LiteralPath $bundledDotnet) { $bundledDotnet }
        else { throw 'No dotnet SDK was found.' }
    $cliHome = Join-Path $PSScriptRoot 'bin\.dotnet-cli'
    New-Item -ItemType Directory -Path $cliHome -Force | Out-Null
    $env:DOTNET_CLI_HOME = $cliHome
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:APPDATA = Join-Path $PSScriptRoot 'bin\.appdata'
    New-Item -ItemType Directory -Path $env:APPDATA -Force | Out-Null
    $nugetConfig = Join-Path (Split-Path $PSScriptRoot -Parent) 'NuGet.Config'
    $coreProject = Join-Path $PSScriptRoot 'src\HarmonicaPlayer.Mobile.Core\HarmonicaPlayer.Mobile.Core.csproj'
    $testProject = Join-Path $PSScriptRoot 'tests\HarmonicaPlayer.Mobile.Core.Tests\HarmonicaPlayer.Mobile.Core.Tests.csproj'
    $appProject = Join-Path $PSScriptRoot 'src\HarmonicaPlayer.Mobile\HarmonicaPlayer.Mobile.csproj'
    $testDll = Join-Path $PSScriptRoot 'tests\HarmonicaPlayer.Mobile.Core.Tests\bin\Release\net10.0\HarmonicaPlayer.Mobile.Core.Tests.dll'

    & $dotnet build $coreProject -c Release --configfile $nugetConfig --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw 'Mobile Core build failed.' }
    & $dotnet build $testProject -c Release --no-restore --configfile $nugetConfig
    if ($LASTEXITCODE -ne 0) { throw 'Mobile Core test build failed.' }
    & $dotnet $testDll
    if ($LASTEXITCODE -ne 0) { throw 'Mobile Core tests failed.' }

    $workloads = (& $dotnet workload list | Out-String)
    # Visual Studio Installer on Windows normally reports the Android pack as
    # "android"; the standalone CLI uses "maui-android". Accept both forms.
    $hasAndroidWorkload = $workloads -match '(?im)^\s*(android|maui-android|maui)\s+'
    if (-not $hasAndroidWorkload) {
        Write-Warning 'MAUI Android workload is not installed; Core passed, Android build skipped.'
        exit 0
    }

    & $dotnet build $appProject -f net10.0-android -c Release --configfile $nugetConfig --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw 'MAUI Android build failed.' }
    Write-Host 'MAUI Android build completed.'
}
finally { Pop-Location }
