param([Parameter(Mandatory=$true)][string]$WasiBin)
$ErrorActionPreference = 'Stop'
$firmware = Join-Path (Split-Path $PSScriptRoot -Parent) '固件'
$core = Join-Path $firmware 'Board2_1Piano'
$vendor = Join-Path $firmware 'Board2_1Tests\vendor'
$site = Join-Path (Split-Path $firmware -Parent | Split-Path -Parent) 'docs\try'
$object = Join-Path ([IO.Path]::GetTempPath()) ("piano-try-" + [guid]::NewGuid() + ".o")
try {
    & (Join-Path $WasiBin 'clang.exe') -O2 -c (Join-Path $vendor 'cJSON.c') -o $object
    if ($LASTEXITCODE) { throw 'cJSON 编译失败' }
    & (Join-Path $WasiBin 'clang++.exe') -std=c++17 -O2 -fno-exceptions -mexec-model=reactor '-Wl,--export-memory' "-I$core" "-I$vendor" (Join-Path $PSScriptRoot 'TryAudio.cpp') (Join-Path $core 'PianoCore.cpp') $object -o (Join-Path $site 'audio.wasm')
    if ($LASTEXITCODE) { throw '试玩音频编译失败' }
} finally {
    if (Test-Path -LiteralPath $object) { Remove-Item -LiteralPath $object }
}
