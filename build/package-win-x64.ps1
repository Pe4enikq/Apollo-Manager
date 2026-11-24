# ==============================================================================
# Apollo Manager — Self-Contained Windows x64 Release Packager (0.1.0-beta.1)
# ==============================================================================
# Publishes Apollo Manager.exe (--self-contained true -r win-x64) WITHOUT single-file
# bundling so collectible AssemblyLoadContext plugins and LGPL native libraries stay
# strictly isolated and replaceable.
#
# Outputs under codebase/dist/:
#   - ApolloManager-<Version>-win-x64-portable.zip
#   - ApolloManager-<Version>-win-x64-setup.exe (if Inno Setup 6 ISCC.exe is installed)
#   - SHA256SUMS.txt
# ==============================================================================
[CmdletBinding()]
param(
    [string]$Version = "0.1.0-beta.1",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$CodebaseRoot = (Resolve-Path (Join-Path $ScriptDir "..")).Path
$DistDir = Join-Path $CodebaseRoot "dist"
$StageDir = Join-Path $DistDir "stage-$Runtime"
$PluginsStageDir = Join-Path $StageDir "plugins"

Write-Host "==> Packaging Apollo Manager $Version ($Configuration / $Runtime)" -ForegroundColor Cyan
Write-Host "    CodebaseRoot : $CodebaseRoot"
Write-Host "    StageDir     : $StageDir"

# 1. Clean previous stage
if (Test-Path $StageDir) {
    Remove-Item -Recurse -Force $StageDir
}
New-Item -ItemType Directory -Path $StageDir -Force | Out-Null
New-Item -ItemType Directory -Path $PluginsStageDir -Force | Out-Null

# 2. Publish L3 AppShell host as self-contained win-x64 (non-single-file for ALC + LGPL boundary)
$AppShellProj = Join-Path $CodebaseRoot "src\L3.Presentation\Walkman.L3.AppShell\Walkman.L3.AppShell.csproj"
Write-Host "==> Publishing host: Apollo Manager.exe ..." -ForegroundColor Cyan
& dotnet publish $AppShellProj `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $StageDir `
    /p:PublishSingleFile=false `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    /p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "Host publish failed with exit code $LASTEXITCODE." }

$HostExe = Join-Path $StageDir "Apollo Manager.exe"
if (-not (Test-Path $HostExe)) {
    throw "Expected published executable not found: $HostExe"
}

# 3. Publish all 8 dynamically loaded L2/L3 plugins into plugins/<PluginName>/
$PluginProjects = @(
    "src\L2.Engines\Walkman.L2.Transport.Msc\Walkman.L2.Transport.Msc.csproj",
    "src\L2.Engines\Walkman.L2.Database.OmgV1\Walkman.L2.Database.OmgV1.csproj",
    "src\L2.Engines\Walkman.L2.Crypto.OpenMG\Walkman.L2.Crypto.OpenMG.csproj",
    "src\L2.Engines\Walkman.L2.Codec.Pipeline\Walkman.L2.Codec.Pipeline.csproj",
    "src\L2.Engines\Walkman.L2.Custom.NwmEngine\Walkman.L2.Custom.NwmEngine.csproj",
    "src\L2.Engines\Walkman.L2.Telemetry.Dumper\Walkman.L2.Telemetry.Dumper.csproj",
    "src\L3.Presentation\Walkman.L3.ScreensaverStudio\Walkman.L3.ScreensaverStudio.csproj",
    "src\L3.Presentation\Walkman.L3.DumpWizard\Walkman.L3.DumpWizard.csproj"
)

foreach ($relProj in $PluginProjects) {
    $projPath = Join-Path $CodebaseRoot $relProj
    $pluginName = [System.IO.Path]::GetFileNameWithoutExtension($projPath)
    $pluginOut = Join-Path $PluginsStageDir $pluginName
    Write-Host "==> Publishing plugin: $pluginName ..." -ForegroundColor DarkCyan
    & dotnet publish $projPath `
        -c $Configuration `
        --no-self-contained `
        -o $pluginOut `
        /p:DebugType=None `
        /p:DebugSymbols=false `
        /p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "Plugin publish failed for $pluginName (exit code $LASTEXITCODE)." }

    # Remove duplicate Walkman.Contracts.dll so ALC always delegates cleanly to host default context
    $dupContracts = Join-Path $pluginOut "Walkman.Contracts.dll"
    if (Test-Path $dupContracts) {
        Remove-Item -Force $dupContracts
    }
}

# 4. Stage manifests/devices.json
$ManifestStage = Join-Path $StageDir "manifests"
New-Item -ItemType Directory -Path $ManifestStage -Force | Out-Null
Copy-Item -Path (Join-Path $CodebaseRoot "manifests\devices.json") -Destination (Join-Path $ManifestStage "devices.json") -Force

# 5. Stage codecs/ (64-bit libmp3lame.dll + LGPL-2.0 notices)
$CodecsSrc = Join-Path $CodebaseRoot "codecs"
$CodecsStage = Join-Path $StageDir "codecs"
New-Item -ItemType Directory -Path $CodecsStage -Force | Out-Null
foreach ($item in @("libmp3lame.dll", "COPYING.LAME", "LICENSE_BOUNDARY.md")) {
    $srcFile = Join-Path $CodecsSrc $item
    if (Test-Path $srcFile) {
        Copy-Item -Path $srcFile -Destination (Join-Path $CodecsStage $item) -Force
    }
}

# 6. Stage tools/ (atracdenc + vgmstream + license boundary notices)
$ToolsSrc = Join-Path $CodebaseRoot "tools"
$ToolsStage = Join-Path $StageDir "tools"
New-Item -ItemType Directory -Path $ToolsStage -Force | Out-Null
Copy-Item -Path (Join-Path $ToolsSrc "LICENSE_BOUNDARY.md") -Destination (Join-Path $ToolsStage "LICENSE_BOUNDARY.md") -Force
Copy-Item -Path (Join-Path $ToolsSrc "atracdenc") -Destination (Join-Path $ToolsStage "atracdenc") -Recurse -Force
Copy-Item -Path (Join-Path $ToolsSrc "vgmstream") -Destination (Join-Path $ToolsStage "vgmstream") -Recurse -Force

# 7. Stage root LICENSE & THIRD_PARTY_NOTICES.md
Copy-Item -Path (Join-Path $CodebaseRoot "LICENSE") -Destination (Join-Path $StageDir "LICENSE") -Force
Copy-Item -Path (Join-Path $CodebaseRoot "THIRD_PARTY_NOTICES.md") -Destination (Join-Path $StageDir "THIRD_PARTY_NOTICES.md") -Force

# Remove any stray PDBs in staged tree
Get-ChildItem -Path $StageDir -Recurse -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force

# 8. Build portable .zip archive
$ZipName = "ApolloManager-$Version-$Runtime-portable.zip"
$ZipPath = Join-Path $DistDir $ZipName
if (Test-Path $ZipPath) {
    Remove-Item -Force $ZipPath
}

Write-Host "==> Creating portable archive: $ZipName ..." -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($StageDir, $ZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$ProducedArtifacts = [System.Collections.Generic.List[string]]::new()
$ProducedArtifacts.Add($ZipPath)

# 9. Build Inno Setup 6 installer if ISCC.exe is available
if (-not $SkipInstaller) {
    $IssPath = Join-Path $ScriptDir "ApolloManager.iss"
    $IsccCandidates = @(
        (Get-Command iscc -ErrorAction SilentlyContinue | ForEach-Object { $_.Source }),
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    ) | Where-Object { $_ -and (Test-Path $_) }

    $IsccExe = $IsccCandidates | Select-Object -First 1
    if ($IsccExe) {
        Write-Host "==> Compiling Inno Setup installer via $IsccExe ..." -ForegroundColor Cyan
        & $IsccExe "/DMyAppVersion=$Version" "/DStageDir=$StageDir" "/DOutDir=$DistDir" $IssPath
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }
        $SetupPath = Join-Path $DistDir "ApolloManager-$Version-$Runtime-setup.exe"
        if (Test-Path $SetupPath) {
            $ProducedArtifacts.Add($SetupPath)
        }
    } else {
        Write-Warning "Inno Setup 6 (ISCC.exe) not found on this machine. Skipping setup.exe compilation."
        Write-Host "    Install Inno Setup 6 via: winget install JRSoftware.InnoSetup" -ForegroundColor Yellow
        Write-Host "    Ready-to-compile script : $IssPath" -ForegroundColor Yellow
    }
}

# 10. Generate SHA256SUMS.txt
$ChecksumsPath = Join-Path $DistDir "SHA256SUMS.txt"
$ChecksumLines = foreach ($artifact in $ProducedArtifacts) {
    $hash = (Get-FileHash -Path $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
    $name = Split-Path -Leaf $artifact
    "$hash  $name"
}
Set-Content -Path $ChecksumsPath -Value $ChecksumLines -Encoding UTF8

Write-Host "==> Packaging complete!" -ForegroundColor Green
foreach ($artifact in $ProducedArtifacts) {
    $sizeMb = [Math]::Round((Get-Item $artifact).Length / 1MB, 2)
    Write-Host ("    {0} ({1} MB)" -f (Split-Path -Leaf $artifact), $sizeMb)
}
Write-Host "    SHA256SUMS.txt written to $ChecksumsPath"
