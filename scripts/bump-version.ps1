[CmdletBinding()]
param(
    [ValidateSet('patch', 'minor', 'major', 'custom')]
    [string]$Bump = 'patch',

    [string]$CustomVersion = '',

    [switch]$Tag,
    [switch]$Push
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repoRoot 'Directory.Build.props'

[xml]$xml = Get-Content -Raw $propsPath
$current = ($xml.Project.PropertyGroup.Version | Select-Object -First 1).Trim()
if ($current -notmatch '^(\d+)\.(\d+)\.(\d+)') {
    throw "Invalid current version '$current' in Directory.Build.props"
}

$major = [int]$Matches[1]
$minor = [int]$Matches[2]
$patch = [int]$Matches[3]

$newVersion = switch ($Bump) {
    'major'  { "$($major + 1).0.0" }
    'minor'  { "$major.$($minor + 1).0" }
    'patch'  { "$major.$minor.$($patch + 1)" }
    'custom' {
        if ([string]::IsNullOrWhiteSpace($CustomVersion)) {
            throw "Specify -CustomVersion when -Bump custom is used."
        }
        $CustomVersion.TrimStart('v')
    }
}

if ($newVersion -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z\.-]+)?$') {
    throw "Invalid semantic version '$newVersion'."
}

$xml.Project.PropertyGroup.Version = $newVersion
$xml.Save($propsPath)

Write-Host "Updated Directory.Build.props version: $current -> $newVersion"

if ($Tag -or $Push) {
    Push-Location $repoRoot
    try {
        git add Directory.Build.props
        git commit -m "chore(release): v$newVersion"
        git tag -a "v$newVersion" -m "Release v$newVersion"
        Write-Host "Committed and tagged v$newVersion"
        if ($Push) {
            git push origin HEAD
            git push origin "v$newVersion"
            Write-Host "Pushed commit and tag v$newVersion to origin"
        }
    }
    finally {
        Pop-Location
    }
}
