#Requires -Version 5.1
param(
    [string]$Version = '5.2.0',
    [string]$NuGetCache = (Join-Path $env:USERPROFILE '.nuget\packages')
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$build = Join-Path $root 'v5\BounceMod\bin\Release\net6.0'
$plugin = Join-Path $root "releases\stage-v$Version\BepInEx\plugins\BounceV5"
$stage = Join-Path $root "releases\stage-v$Version"
$zip = Join-Path $root "releases\BounceV5_v$Version.zip"

function Copy-Required {
    param([string]$Source, [string]$Destination)
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required file missing: $Source"
    }
    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Path $plugin -Force | Out-Null

Copy-Required (Join-Path $build 'BounceV5.dll') (Join-Path $plugin 'BounceV5.dll')
$naudio = @{
    'NAudio.dll' = 'naudio\2.2.1\lib\net6.0\NAudio.dll'
    'NAudio.Core.dll' = 'naudio.core\2.2.1\lib\netstandard2.0\NAudio.Core.dll'
    'NAudio.WinMM.dll' = 'naudio.winmm\2.2.1\lib\netstandard2.0\NAudio.WinMM.dll'
    'NAudio.Wasapi.dll' = 'naudio.wasapi\2.2.1\lib\netstandard2.0\NAudio.Wasapi.dll'
    'NAudio.Midi.dll' = 'naudio.midi\2.2.1\lib\netstandard2.0\NAudio.Midi.dll'
    'NAudio.Asio.dll' = 'naudio.asio\2.2.1\lib\netstandard2.0\NAudio.Asio.dll'
}
foreach ($name in $naudio.Keys) {
    Copy-Required (Join-Path $NuGetCache $naudio[$name]) (Join-Path $plugin $name)
}

Copy-Required (Join-Path $root 'DJ.mp3') (Join-Path $plugin 'assets\DJ.mp3')
Copy-Required (Join-Path $root 'the-rock-sound-effect.mp3') (Join-Path $plugin 'assets\the-rock-sound-effect.mp3')

# Keep this script ASCII-only so Windows PowerShell 5.1 reads it consistently.
$izayoi = ([string][char]0x5341 + [char]0x516D + [char]0x591C + [char]0x6708) + '.png'
$newMoon = ([string][char]0x65B0 + [char]0x6708) + '.png'
$fullMoon = ([string][char]0x6EE1 + [char]0x6708) + '.png'
$halfMoon = ([string][char]0x534A + [char]0x6708) + '.png'
foreach ($name in @($izayoi, $newMoon, $fullMoon, $halfMoon)) {
    Copy-Required (Join-Path $root "image\$name") (Join-Path $plugin "assets\image\$name")
}

foreach ($name in @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'CHANGELOG.md')) {
    Copy-Required (Join-Path $root $name) (Join-Path $stage $name)
}

if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$sizeMiB = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 2)
Write-Output "Packed: $zip ($sizeMiB MiB)"
