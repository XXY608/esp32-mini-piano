param(
    [Parameter(Mandatory=$true)][string]$PublishedDirectory,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$BuildStamp
)
$ErrorActionPreference = 'Stop'
$source = Split-Path $PSScriptRoot -Parent
$workspace = Split-Path (Split-Path $source -Parent) -Parent
$compiler = Join-Path $workspace 'work\build-tools\InnoSetup\ISCC.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Install official Inno Setup 6.7.3 into work/build-tools/InnoSetup before building the installer.' }
# Keep build paths below Win32 MAX_PATH, including the pinned packages' deeply
# nested license filenames. Installed paths use the shorter per-user default.
$payload = Join-Path $workspace "work\package-$BuildStamp"
$output = Join-Path $workspace 'work\bin\Release'
New-Item -ItemType Directory -Path $payload,$output -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PublishedDirectory '电子琴.exe') -Destination $payload
foreach ($name in @('安装说明.txt','第三方组件说明.txt')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $payload
}
Copy-Item -LiteralPath (Join-Path $source '使用说明.txt') -Destination $payload
$dotnet = Join-Path $workspace 'work\dotnet-sdk'
Copy-Item -LiteralPath (Join-Path $dotnet 'LICENSE.txt') -Destination (Join-Path $payload 'dotnet-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $dotnet 'ThirdPartyNotices.txt') -Destination (Join-Path $payload 'dotnet-THIRD-PARTY-NOTICES.txt')
$python = Join-Path $workspace 'work\lyrics-alignment\venv\Scripts\python.exe'
& $python (Join-Path $PSScriptRoot 'stage_runtime.py') --workspace $workspace --target $payload
if ($LASTEXITCODE -ne 0) { throw 'Standalone runtime staging failed.' }
& (Join-Path $payload 'work\lyrics-alignment\python\python.exe') -B (Join-Path $PSScriptRoot 'verify_runtime.py') $payload
if ($LASTEXITCODE -ne 0) { throw 'Standalone runtime validation failed.' }
& $compiler /Qp ("/DPayload=" + $payload) ("/DAppVersion=" + $Version) ("/DOutputDirectory=" + $output) (Join-Path $PSScriptRoot 'HarmonicaPlayer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $output '电子琴安装程序.exe'
Get-FileHash -LiteralPath $installer -Algorithm SHA256 | Format-List
Write-Host "Standalone installer: $installer"
Write-Host "Payload: $payload"
