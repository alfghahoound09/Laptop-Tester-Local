[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appProj = Join-Path $repoRoot 'src\LaptopTester.App\LaptopTester.App.csproj'
$testProj = Join-Path $repoRoot 'tests\LaptopTester.Tests\LaptopTester.Tests.csproj'
$slnPath = Join-Path $repoRoot 'LaptopTester.sln'
$issPath = Join-Path $repoRoot 'installer\LaptopTester.iss'

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -Raw (Join-Path $repoRoot 'Directory.Build.props')
    $Version = ($props.Project.PropertyGroup.Version | Select-Object -First 1).Trim()
}
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)') {
    throw "Version '$Version' must start with Major.Minor.Patch"
}
$assemblyVer = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
$commitSha = ''
try {
    $gitSha = (& git -C $repoRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($gitSha)) {
        $commitSha = $gitSha.Trim()
    }
}
catch { }
$builtAtUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$sourceRevProps = if (-not [string]::IsNullOrWhiteSpace($commitSha)) { @("-p:SourceRevisionId=$commitSha") } else { @() }

$publishRoot = Join-Path $repoRoot 'publish'
$portableDir = Join-Path $publishRoot "$Runtime-portable"
$installerInputDir = Join-Path $publishRoot "$Runtime-installer"
$distDir = Join-Path $repoRoot 'dist'

New-Item -ItemType Directory -Force -Path $portableDir, $installerInputDir, $distDir | Out-Null
Get-ChildItem -Path $distDir -Filter '*.exe' -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host "=== LaptopTester Release Build v$Version ($Runtime) ==="

if (-not $SkipTests) {
    dotnet test $slnPath -c Release --filter "Category!=Hardware&Category!=Audible"
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed." }
}

# 1. Portable single-file self-contained .exe
Write-Host "Publishing portable single-file executable..."
dotnet publish $appProj `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -p:AssemblyVersion=$assemblyVer `
    -p:FileVersion=$assemblyVer `
    @sourceRevProps `
    -o $portableDir
if ($LASTEXITCODE -ne 0) { throw "Portable publish failed." }

$portableSrc = Join-Path $portableDir 'LaptopTester.exe'
$portableDest = Join-Path $distDir "LaptopTester-$Version-$Runtime-portable.exe"
Copy-Item -Force $portableSrc $portableDest
Write-Host "Created portable exe: $portableDest"

# 2. Windows Setup Installer (.exe) via Inno Setup 6
if (-not $SkipInstaller) {
    $iscc = $null
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
    if (-not $iscc) {
        foreach ($candidate in @(
            "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        )) {
            if (Test-Path $candidate) { $iscc = $candidate; break }
        }
    }

    if ($iscc) {
        Write-Host "Publishing self-contained installer payload..."
        dotnet publish $appProj `
            -c Release `
            -r $Runtime `
            --self-contained true `
            -p:PublishSingleFile=false `
            -p:DebugType=none `
            -p:DebugSymbols=false `
            -p:Version=$Version `
            -p:AssemblyVersion=$assemblyVer `
            -p:FileVersion=$assemblyVer `
            @sourceRevProps `
            -o $installerInputDir
        if ($LASTEXITCODE -ne 0) { throw "Installer payload publish failed." }

        Write-Host "Compiling Windows installer via $iscc..."
        & $iscc "/DAppVersion=$Version" "/DFileVersion=$assemblyVer" "/DPublishDir=$installerInputDir" "/DOutputDir=$distDir" $issPath
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }
    }
    else {
        Write-Warning "Inno Setup 6 (ISCC.exe) not found; skipping Setup .exe generation. Portable .exe is ready in $distDir."
    }
}

# 3. Generate SHA256SUMS.txt (with commit SHA & binary SHA-256 metadata headers for same-version hotfix checks)
$checksumPath = Join-Path $distDir 'SHA256SUMS.txt'
$artifacts = Get-ChildItem -Path $distDir -Filter '*.exe' | Sort-Object Name
$headerLines = [System.Collections.Generic.List[string]]::new()
$headerLines.Add("# version: $Version")
if (-not [string]::IsNullOrWhiteSpace($commitSha)) {
    $headerLines.Add("# commit: $commitSha")
}
$headerLines.Add("# built_at: $builtAtUtc")

$installedAppExe = Join-Path $installerInputDir 'LaptopTester.exe'
if (Test-Path -LiteralPath $installedAppExe) {
    $appHash = (Get-FileHash -Path $installedAppExe -Algorithm SHA256).Hash.ToLowerInvariant()
    $headerLines.Add("# app_sha256: $appHash")
}
if (Test-Path -LiteralPath $portableDest) {
    $portHash = (Get-FileHash -Path $portableDest -Algorithm SHA256).Hash.ToLowerInvariant()
    $headerLines.Add("# portable_sha256: $portHash")
}

$fileLines = foreach ($file in $artifacts) {
    $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}
Set-Content -Path $checksumPath -Value ($headerLines + $fileLines) -Encoding UTF8
Write-Host "Checksums written to $checksumPath"
Get-ChildItem -Path $distDir | Select-Object Name, Length
