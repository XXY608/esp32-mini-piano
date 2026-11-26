[CmdletBinding()]
param(
    [string]$ArduinoCli = 'D:\arduino\Arduino IDE\resources\app\lib\backend\resources\arduino-cli.exe',
    [string]$Zig,
    [string]$Node = (Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'),
    [string]$NodeModules = (Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\node_modules'),
    [string]$EvidenceDir
)
$ErrorActionPreference = 'Stop'
$sourceRoot = $PSScriptRoot
$projectRoot = Join-Path (Split-Path (Split-Path $sourceRoot -Parent) -Parent) 'ESP32-S3-mini-piano'
if (-not $EvidenceDir) { $EvidenceDir = Join-Path $projectRoot 'work-build\board2_1-validation' }
if (-not $Zig) { $Zig = Join-Path $projectRoot 'tool-staging\zig-native-tests\zig-windows-x86_64-0.13.0\zig.exe' }
if (-not (Test-Path -LiteralPath $ArduinoCli)) { throw 'Arduino CLI missing; supply -ArduinoCli. This script does not install or upgrade tools.' }
if (-not (Test-Path -LiteralPath $Zig)) { throw 'Project-local native test compiler missing; supply -Zig. Do not skip the native tests.' }
$workBuild = Join-Path $projectRoot 'work-build'
$nativeBuild = Join-Path $workBuild 'board2_1-native'
$boardBuild = Join-Path $workBuild 'board2_1-firmware'
New-Item -ItemType Directory -Force -Path $EvidenceDir,$nativeBuild,$boardBuild | Out-Null
$previousZigCache = $env:ZIG_GLOBAL_CACHE_DIR
$previousNodePath = $env:NODE_PATH
$previousSamples = $env:PIANO_SAMPLE_DIRECTORY
try {
    $env:ZIG_GLOBAL_CACHE_DIR = Join-Path $workBuild 'zig-cache'
    $env:PIANO_SAMPLE_DIRECTORY = Join-Path $workBuild 'Board2_1-sd-card\piano-samples'
    foreach ($id in 3..11) {
        if (!(Test-Path -LiteralPath (Join-Path $env:PIANO_SAMPLE_DIRECTORY "$id.hps"))) { throw 'Missing SD sample bank. Run Firmware/tools/prepare_soundfonts.py first; see firmware README.' }
    }
    $pianoSource = Join-Path $sourceRoot 'Board2_1Piano'
    & (Join-Path $sourceRoot 'Embed-WebUi.ps1')
    $tests = Join-Path $sourceRoot 'Board2_1Tests'
    $vendor = Join-Path $tests 'vendor'
    $object = Join-Path $nativeBuild 'cjson.o'
    & $Zig cc -DCJSON_HIDE_SYMBOLS -c (Join-Path $vendor 'cJSON.c') -o $object *> (Join-Path $EvidenceDir 'native-compile.log')
    if ($LASTEXITCODE -ne 0) { throw 'Native cJSON compilation failed; see native-compile.log' }
    $testExe = Join-Path $nativeBuild 'core-tests.exe'
    & $Zig c++ -std=c++17 -Wall -Wextra -Werror -DCJSON_HIDE_SYMBOLS "-I$pianoSource" "-I$vendor" (Join-Path $pianoSource 'PianoCore.cpp') (Join-Path $tests 'CoreTests.cpp') $object -o $testExe *>> (Join-Path $EvidenceDir 'native-compile.log')
    if ($LASTEXITCODE -ne 0) { throw 'Native core compilation failed; see native-compile.log' }
    $scoreFrames = Join-Path $EvidenceDir 'score-frames'
    New-Item -ItemType Directory -Force -Path $scoreFrames | Out-Null
    & $testExe $scoreFrames *> (Join-Path $EvidenceDir 'native-tests.log')
    if ($LASTEXITCODE -ne 0) { throw 'Core/JSON/synth/storage tests failed; see native-tests.log' }
    if (-not (Test-Path -LiteralPath $Node)) { throw 'Node missing; supply -Node for web validation.' }
    $env:NODE_PATH = $NodeModules
    & $Node (Join-Path $tests 'WebTests.cjs') $EvidenceDir *> (Join-Path $EvidenceDir 'web-tests.log')
    if ($LASTEXITCODE -ne 0) { throw 'Web/parser tests failed; see web-tests.log' }

    $core = Join-Path $env:LOCALAPPDATA 'Arduino15\packages\esp32\hardware\esp32\2.0.17'
    if (-not (Test-Path -LiteralPath $core)) { throw 'Pinned ESP32 Arduino Core 2.0.17 is required; no automatic upgrade is performed.' }
    $sdkJson = Join-Path $core 'tools\sdk\esp32s3\include\json\cJSON\cJSON.h'
    $sdkHeaderText = (Get-Content -LiteralPath $sdkJson -Raw).Replace("`r`n", "`n")
    $nativeHeaderText = (Get-Content -LiteralPath (Join-Path $vendor 'cJSON.h') -Raw).Replace("`r`n", "`n")
    if ($sdkHeaderText -cne $nativeHeaderText) { throw 'SDK/native-test cJSON headers differ; audit before changing the pinned dependencies.' }
    $libraryRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Arduino\libraries'
    $dependencies = @(
        @{Folder='Adafruit_ST7735_and_ST7789_Library'; Version='1.11.0'},
        @{Folder='Adafruit_GFX_Library'; Version='1.12.6'},
        @{Folder='Adafruit_BusIO'; Version='1.17.4'}
    )
    $arguments = @('compile','--fqbn','esp32:esp32:esp32s3:FlashSize=16M,FlashMode=qio,PSRAM=opi,PartitionScheme=default,CDCOnBoot=default,USBMode=hwcdc','--build-path',$boardBuild,'--warnings','all','--jobs','4')
    foreach ($dep in $dependencies) {
        $lib = Join-Path $libraryRoot $dep.Folder
        if (-not (Select-String -LiteralPath (Join-Path $lib 'library.properties') -Pattern ('^version=' + [regex]::Escape($dep.Version) + '$') -Quiet)) { throw ('Unexpected library version: ' + $dep.Folder) }
        $arguments += @('--library',$lib)
    }
    $arguments += @('--library',(Join-Path $core 'libraries\SD'),$pianoSource)
    & $ArduinoCli @arguments *> (Join-Path $EvidenceDir 'compile.log')
    if ($LASTEXITCODE -ne 0) { throw 'ESP32 compilation failed; see compile.log' }
    if (Select-String -LiteralPath (Join-Path $EvidenceDir 'compile.log') -Pattern 'warning:' -Quiet) { throw 'Compiler warnings require review; see compile.log' }
    $release = Join-Path $workBuild 'Board2_1-release'
    New-Item -ItemType Directory -Force -Path $release | Out-Null
    $binaryNames = @('Board2_1Piano.ino.bin','Board2_1Piano.ino.bootloader.bin','Board2_1Piano.ino.partitions.bin')
    foreach ($binaryName in $binaryNames) { Copy-Item -LiteralPath (Join-Path $boardBuild $binaryName) -Destination $release -Force }
    Copy-Item -LiteralPath (Join-Path $core 'tools\partitions\boot_app0.bin') -Destination $release -Force
    Copy-Item -LiteralPath (Join-Path $pianoSource 'README.md') -Destination (Join-Path $release 'README.md') -Force
    $fontLicenses = Join-Path $release 'fonts'
    New-Item -ItemType Directory -Force -Path $fontLicenses | Out-Null
    foreach ($license in @('LICENSE.txt','OFL-1.1.txt')) { Copy-Item -LiteralPath (Join-Path $pianoSource "fonts\$license") -Destination $fontLicenses -Force }
    $files = @($binaryNames + 'boot_app0.bin' | ForEach-Object {
      $artifact = Get-Item -LiteralPath (Join-Path $release $_)
      @{name=$artifact.Name;bytes=$artifact.Length;sha256=(Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $sourceFiles = @(Get-ChildItem -LiteralPath $pianoSource -File | Where-Object { $_.Extension -in '.cpp','.h','.ino','.csv' -and $_.Name -ne 'Board2_1Secrets.h' } | ForEach-Object {
      @{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $versionLine = Select-String -LiteralPath (Join-Path $pianoSource 'Board2_1Config.h') -Pattern 'kVersion\[\]\s*=\s*"([^"]+)"'
    if (!$versionLine) { throw 'Board firmware version missing from Board2_1Config.h.' }
    $manifest = @{version=$versionLine.Matches[0].Groups[1].Value;builtAtUtc=[DateTime]::UtcNow.ToString('o');fqbn='esp32:esp32:esp32s3:FlashSize=16M,FlashMode=qio,PSRAM=opi,PartitionScheme=default,CDCOnBoot=default,USBMode=hwcdc';files=$files;sources=$sourceFiles;nativeTests='native-tests.log';webTests='web-tests.log (mock API)';hardwareValidated=$false;uploaded=$false;offsets=@{'Board2_1Piano.ino.bootloader.bin'='0x0';'Board2_1Piano.ino.partitions.bin'='0x8000';'boot_app0.bin'='0xe000';'Board2_1Piano.ino.bin'='0x10000'}}
    $manifest.sdSamples = Get-Content -LiteralPath (Join-Path $env:PIANO_SAMPLE_DIRECTORY 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $release 'manifest.json') -Encoding utf8
    Get-Content -LiteralPath (Join-Path $EvidenceDir 'native-tests.log') -Tail 4
    Get-Content -LiteralPath (Join-Path $EvidenceDir 'compile.log') -Tail 5
    Get-Content -LiteralPath (Join-Path $EvidenceDir 'web-tests.log') -Tail 1
    Write-Output "Release: $release"
    Write-Output 'Compile/test only. No port selected, no upload, no device connection, no SD write.'
} finally { $env:ZIG_GLOBAL_CACHE_DIR = $previousZigCache; $env:NODE_PATH = $previousNodePath; $env:PIANO_SAMPLE_DIRECTORY = $previousSamples }
