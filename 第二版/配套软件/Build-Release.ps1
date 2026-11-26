param([switch]$Installer, [switch]$NoPackage)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $bundledDotnet = Join-Path (Split-Path $PSScriptRoot -Parent) 'dotnet-sdk\dotnet.exe'
    $dotnet = if (Test-Path -LiteralPath $bundledDotnet) {
        $bundledDotnet
    } else {
        (Get-Command dotnet -ErrorAction Stop).Source
    }

    $version = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'HarmonicaPlayer.csproj') -Raw)).Project.PropertyGroup.Version
    $buildStamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $pianoName = [string]::Concat([char]0x7535, [char]0x5B50, [char]0x7434)
    $workspaceRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $workspaceExe = Join-Path $workspaceRoot ($pianoName + '.exe')
    $soundFontRoot = Join-Path $workspaceRoot 'work\soundfonts'
    foreach ($asset in @('GeneralUser-GS.sf2', 'Salamander.sf2', 'Piano-Hall.json', 'fluidsynth\bin\libfluidsynth-3.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $soundFontRoot $asset))) {
            throw "SoundFont runtime asset missing: $asset. See SoundFonts.md."
        }
    }
    $workspacePdb = Join-Path $workspaceRoot ($pianoName + '.pdb')
    $legacyWorkspaceExe = Join-Path $workspaceRoot 'HarmonicaPlayer.exe'
    $legacyWorkspacePdb = Join-Path $workspaceRoot 'HarmonicaPlayer.pdb'
    $verifyRoot = Join-Path $PSScriptRoot "bin\Release\verify-$buildStamp"
    $parserOutput = Join-Path $verifyRoot 'parser'
    $smokeOutput = Join-Path $verifyRoot 'windows-smoke'
    $parserOutputArgument = '-p:OutputPath=' + $parserOutput
    $smokeOutputArgument = '-p:OutputPath=' + $smokeOutput
    $dotnetCliHome = Join-Path $PSScriptRoot 'bin\.dotnet-cli'
    $appDataHome = Join-Path $PSScriptRoot 'bin\.appdata'

    $oldTestProcesses = @(Get-Process -Name ParserTests,WindowsSmokeTests -ErrorAction SilentlyContinue)
    if ($oldTestProcesses.Count -gt 0) {
        throw 'Old parser or Windows smoke test processes are running. Close them and retry; this script never terminates processes.'
    }

    New-Item -ItemType Directory -Path $verifyRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $dotnetCliHome -Force | Out-Null
    New-Item -ItemType Directory -Path $appDataHome -Force | Out-Null
    $env:DOTNET_CLI_HOME = $dotnetCliHome
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:APPDATA = $appDataHome

    $alignmentPython = Join-Path $workspaceRoot 'work\lyrics-alignment\venv\Scripts\python.exe'
    if (Test-Path -LiteralPath $alignmentPython) {
        & $alignmentPython .\tools\lyrics-alignment\tests\test_alignment.py
        if ($LASTEXITCODE -ne 0) { throw 'Lyrics alignment tests failed. Publishing cancelled.' }
    }

    & $dotnet restore .\HarmonicaPlayer.csproj -r win-x64 --configfile .\NuGet.Config --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw 'Application restore failed. Publishing cancelled.' }
    & $dotnet restore .\Tests\ParserTests.csproj --configfile .\NuGet.Config --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw 'ParserTests restore failed. Publishing cancelled.' }
    & $dotnet restore .\Tests\WindowsSmoke\WindowsSmokeTests.csproj --configfile .\NuGet.Config --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw 'WindowsSmoke restore failed. Publishing cancelled.' }

    & $dotnet build .\Tests\ParserTests.csproj -c Release --no-restore -p:UseAppHost=false $parserOutputArgument
    if ($LASTEXITCODE -ne 0) { throw 'ParserTests build failed. Publishing cancelled.' }
    $parserDll = Join-Path $parserOutput 'ParserTests.dll'
    if (!(Test-Path -LiteralPath $parserDll)) { throw 'ParserTests.dll missing. Publishing cancelled.' }
    & $dotnet $parserDll
    if ($LASTEXITCODE -ne 0) { throw 'ParserTests failed. Publishing cancelled.' }

    & $dotnet build .\Tests\WindowsSmoke\WindowsSmokeTests.csproj -c Release --no-restore -p:UseAppHost=false $smokeOutputArgument
    if ($LASTEXITCODE -ne 0) { throw 'WindowsSmoke build failed. Publishing cancelled.' }
    $smokeDll = Join-Path $smokeOutput 'WindowsSmokeTests.dll'
    if (!(Test-Path -LiteralPath $smokeDll)) { throw 'WindowsSmokeTests.dll missing. Publishing cancelled.' }
    & $dotnet $smokeDll
    if ($LASTEXITCODE -ne 0) { throw 'Windows close tests failed. Publishing cancelled.' }

    $outputDir = Join-Path $PSScriptRoot "bin\Release\publish-$version-$buildStamp"
    & $dotnet publish .\HarmonicaPlayer.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $outputDir
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed. ZIP was not created.' }
    $publishedExe = Join-Path $outputDir ($pianoName + '.exe')
    if (!(Test-Path -LiteralPath $publishedExe)) { throw 'EXE missing.' }

    $running = @(Get-Process -Name $pianoName -ErrorAction SilentlyContinue) +
        @(Get-Process -Name 'HarmonicaPlayer' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) { throw 'The current or legacy application is running. Close it before updating.' }
    # The local inference environment is installed separately under work. Keep
    # its small first-party entry scripts in sync with the canonical source.
    $lyricsHelperSource = Join-Path $PSScriptRoot 'tools\lyrics-alignment'
    $lyricsHelperTarget = Join-Path $workspaceRoot 'work\lyrics-alignment'
    New-Item -ItemType Directory -Path $lyricsHelperTarget -Force | Out-Null
    Get-ChildItem -LiteralPath $lyricsHelperSource -File | Where-Object { $_.Extension -in '.py', '.json' } |
        Copy-Item -Destination $lyricsHelperTarget -Force
    $lyricsModelFolder = Join-Path $lyricsHelperTarget 'models\TIFA-1.0'
    if (Test-Path -LiteralPath $lyricsModelFolder) {
        Copy-Item -LiteralPath (Join-Path $lyricsHelperSource 'g2p-zh.yaml') -Destination $lyricsModelFolder -Force
    }
    Copy-Item -LiteralPath $publishedExe -Destination $workspaceExe -Force
    $publishedPdb = Join-Path $outputDir ($pianoName + '.pdb')
    if (Test-Path -LiteralPath $publishedPdb) {
        Copy-Item -LiteralPath $publishedPdb -Destination $workspacePdb -Force
    }
    if (Test-Path -LiteralPath $legacyWorkspaceExe) { Remove-Item -LiteralPath $legacyWorkspaceExe -Force }
    if (Test-Path -LiteralPath $legacyWorkspacePdb) { Remove-Item -LiteralPath $legacyWorkspacePdb -Force }

    if ($NoPackage) {
        Write-Host "Workspace EXE updated (no installer or ZIP): $workspaceExe"
        return
    }
    if ($Installer) {
        & (Join-Path $PSScriptRoot 'installer\Build-Installer.ps1') -PublishedDirectory $outputDir -Version $version -BuildStamp $buildStamp
        Write-Host "Workspace EXE: $workspaceExe"
        Write-Host 'Full validation, workspace update and standalone installer completed.'
        return
    }
    $packageDir = Join-Path $PSScriptRoot "bin\Release\package-$version-$buildStamp"
    New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
    Copy-Item -LiteralPath $publishedExe -Destination $packageDir
    $usageFile = Join-Path $PSScriptRoot ([string]::Concat([char]0x4F7F, [char]0x7528, [char]0x8BF4, [char]0x660E, '.txt'))
    Copy-Item -LiteralPath $usageFile $packageDir
    $zipPath = Join-Path $PSScriptRoot ("bin\Release\" + $pianoName + " $version-win-x64.zip")
    Compress-Archive -Path (Join-Path $packageDir '*') -DestinationPath $zipPath -Force
    Write-Host "Workspace EXE: $workspaceExe"
    Write-Host "ZIP: $zipPath"
    Write-Host 'Build, parser tests, Windows close tests and workspace EXE update completed.'
}
finally {
    Pop-Location
}
