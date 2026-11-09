# Packages Replay Mod as a Thunderstore-style zip that Gale / r2modman can import as a local mod:
#   manifest.json, icon.png, README.md at the root, and plugins/ReplayMod.dll, which the mod manager
#   installs to BepInEx/plugins/<Author>-Replay_Mod/.
# Usage: pwsh tools/package.ps1 [-Author Gargantua1866]   ->   dist/<Author>-Replay_Mod-<version>.zip
param([string]$Author = "Gargantua1866")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "src\ReplayMod"

# version from the plugin source (Plugin.Version)
$version = [regex]::Match((Get-Content (Join-Path $proj "Plugin.cs") -Raw), 'Version\s*=\s*"([0-9.]+)"').Groups[1].Value
if (-not $version) { throw "Plugin.Version not found" }

# No PDB path in the DLL: it would publish this PC's folder names. Stamp the file version too.
dotnet build -c Release $proj -p:SkipDeploy=true -p:DebugType=none -p:Version=$version -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$dll = Join-Path $proj "bin\Release\ReplayMod.dll"

$stage = Join-Path $root "dist\stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage "plugins") | Out-Null
Copy-Item $dll (Join-Path $stage "plugins")
Copy-Item (Join-Path $PSScriptRoot "package\icon.png") $stage
Copy-Item (Join-Path $root "README.md") $stage
Copy-Item (Join-Path $root "CHANGELOG.md") $stage
[ordered]@{
    name           = "Replay_Mod"
    author         = $Author # Gale shows this for a zip imported locally; a store install shows the team name
    version_number = $version
    website_url    = "https://github.com/Gargantua1866/Valheim-Replay-Mod"
    description    = "Records gameplay and plays it back in a separate world, where you fly a free camera, build camera paths and export MP4 video."
    dependencies   = @("denikson-BepInExPack_Valheim-5.4.2351")
} | ConvertTo-Json | Set-Content (Join-Path $stage "manifest.json") -Encoding utf8NoBOM

$zip = Join-Path $root "dist\$Author-Replay_Mod-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force
Write-Host "Packaged $zip ($([math]::Round((Get-Item $zip).Length / 1KB, 1)) KB)"
