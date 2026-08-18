<#
.SYNOPSIS
  Update version across the Slay the Spire II Archipelago codebase.

.DESCRIPTION
  Takes a version string (e.g., "alpha-0.2.1" or "0.3.0"), extracts the semver
  part (major.minor.patch), and updates:
    - StS2AP.csproj:               ModVersion property
        - local.props.template:        ModVersion property
        - local.props (when present):  ModVersion property
    - world/spire2/archipelago.json: world_version field
    - client/StS2AP/Archipelago.json: version field
    - world/spire2/world.py:       mod_compat_version field

.PARAMETER Version
  Version string to use. Can include a prefix (e.g., "alpha-0.2.1").
  The semver (X.Y.Z) will be extracted.

.EXAMPLE
  .\scripts\release.ps1 -Version "0.3.0"
  .\scripts\release.ps1 -Version "alpha-0.2.1"
  .\scripts\release.ps1 -Version "alpha-0.2.1" -skipGitHub
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$skipGitHub
)

$ErrorActionPreference = "Stop"

# Windows PowerShell 5.1 defaults Set-Content to UTF-16. Write release-managed
# text as UTF-8 without a BOM consistently across Windows PowerShell and pwsh.
$Utf8NoBomEncoding = New-Object System.Text.UTF8Encoding($false)
function Set-Utf8NoBomContent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Content
    )

    [System.IO.File]::WriteAllText($Path, $Content, $Utf8NoBomEncoding)
}

# Resolve repo root
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..") | Select-Object -ExpandProperty Path
$ReleaseRemote = "upstream"
$ReleaseRepo = "dlueben1/Slay-the-Spire-2-Archipelago"
$ReleasePaths = @(
    "client/StS2AP/StS2AP.csproj"
    "client/StS2AP/local.props.template"
    "client/StS2AP/Archipelago.json"
    "world/spire2/archipelago.json"
    "world/spire2/world.py"
)

# Extract semver (X.Y.Z) from the input version string
if ($Version -match '(\d+\.\d+\.\d+)') {
    $SemVer = $matches[1]
    Write-Host "Input version: $Version" -ForegroundColor Cyan
    Write-Host "Extracted semver: $SemVer" -ForegroundColor Green
} else {
    Write-Error "Version '$Version' does not contain a valid semver pattern (X.Y.Z)"
    exit 1
}

# ~ Verify we are on the main branch ~
Write-Host "`nChecking current branch..." -ForegroundColor Cyan
$currentBranch = git -C $RepoRoot rev-parse --abbrev-ref HEAD 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to determine current branch. Is this a git repository?"
    exit 1
}
if ($currentBranch -ne 'main') {
    Write-Error "You must be on the 'main' branch to create a release. Current branch: '$currentBranch'"
    exit 1
}
Write-Host "  On branch: main" -ForegroundColor Green

# ~ Verify the release starts from official upstream/main without staged or
# release-file changes. Unstaged changes elsewhere remain local and are not
# included in the version commit. ~
Write-Host "`nChecking official release target and local Git state..." -ForegroundColor Cyan

$releaseRemoteUrl = git -C $RepoRoot remote get-url $ReleaseRemote 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Release remote '$ReleaseRemote' is not configured."
    exit 1
}
if ($releaseRemoteUrl -notmatch 'github\.com[:/]dlueben1/Slay-the-Spire-2-Archipelago(?:\.git)?$') {
    Write-Error "Remote '$ReleaseRemote' points to '$releaseRemoteUrl', not the official $ReleaseRepo repository."
    exit 1
}
Write-Host "  Release repository: $ReleaseRepo ($ReleaseRemote)" -ForegroundColor Green

git -C $RepoRoot diff --cached --quiet
$stagedDiffExit = $LASTEXITCODE
if ($stagedDiffExit -eq 1) {
    Write-Error "Staged changes are present. Unstage them before releasing so they cannot enter the release commit."
    exit 1
} elseif ($stagedDiffExit -ne 0) {
    Write-Error "Failed to inspect staged changes (exit code $stagedDiffExit)."
    exit 1
}

git -C $RepoRoot diff --quiet -- @ReleasePaths
$releasePathDiffExit = $LASTEXITCODE
if ($releasePathDiffExit -eq 1) {
    Write-Error "One or more release-managed version files already have local changes. Commit, stash, or revert them before releasing."
    exit 1
} elseif ($releasePathDiffExit -ne 0) {
    Write-Error "Failed to inspect release-managed files (exit code $releasePathDiffExit)."
    exit 1
}

Write-Host "  Fetching official upstream/main..." -ForegroundColor Cyan
git -C $RepoRoot fetch $ReleaseRemote "refs/heads/main:refs/remotes/$ReleaseRemote/main"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to fetch $ReleaseRemote/main."
    exit 1
}

$releaseBaseCommit = git -C $RepoRoot rev-parse "$ReleaseRemote/main" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to resolve $ReleaseRemote/main."
    exit 1
}
$localHead = git -C $RepoRoot rev-parse HEAD 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to resolve local HEAD."
    exit 1
}
if ($localHead -eq $releaseBaseCommit) {
    $resumingRelease = $false
    Write-Host "  Local HEAD matches $ReleaseRemote/main: $releaseBaseCommit" -ForegroundColor Green
} else {
    # A previous run may have created the controlled version-only commit and
    # local tag before a later publish step failed. Permit exactly that state.
    $localParent = git -C $RepoRoot rev-parse HEAD^ 2>&1
    $localSubject = git -C $RepoRoot log -1 --format=%s 2>&1
    $unexpectedLocalCommitPaths = @(git -C $RepoRoot diff-tree --no-commit-id --name-only -r HEAD | Where-Object { $_ -notin $ReleasePaths })
    $localCommitPaths = @(git -C $RepoRoot diff-tree --no-commit-id --name-only -r HEAD)
    if ($LASTEXITCODE -ne 0 -or
        $localParent -ne $releaseBaseCommit -or
        $localSubject -ne $Version -or
        $localCommitPaths.Count -eq 0 -or
        $unexpectedLocalCommitPaths.Count -gt 0) {
        Write-Error "Local HEAD is neither $ReleaseRemote/main nor the controlled '$Version' version-only commit based directly on it. Local-only commits will not be published."
        exit 1
    }
    $resumingRelease = $true
    Write-Host "  Resuming controlled release commit: $localHead" -ForegroundColor Yellow
}

git -C $RepoRoot show-ref --verify --quiet "refs/tags/$Version"
if ($LASTEXITCODE -eq 0) {
    $localTagCommit = git -C $RepoRoot rev-parse "refs/tags/$Version^{commit}" 2>&1
    if (-not $resumingRelease -or $LASTEXITCODE -ne 0 -or $localTagCommit -ne $localHead) {
        Write-Error "Local tag '$Version' already exists but does not identify the verified resumable release commit."
        exit 1
    }
    Write-Host "  Reusing local tag '$Version' at the verified release commit." -ForegroundColor Yellow
}

git -C $RepoRoot ls-remote --exit-code --tags $ReleaseRemote "refs/tags/$Version" | Out-Null
$remoteTagExit = $LASTEXITCODE
if ($remoteTagExit -eq 0) {
    Write-Error "Tag '$Version' already exists in $ReleaseRepo."
    exit 1
} elseif ($remoteTagExit -ne 2) {
    Write-Error "Failed to check whether tag '$Version' exists in $ReleaseRepo (exit code $remoteTagExit)."
    exit 1
}

if (-not $skipGitHub) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Error "GitHub CLI (gh) is required. Install from https://cli.github.com/"
        exit 1
    }

    $templatePath = Join-Path $PSScriptRoot "release-notes-template.md"
    if (-not (Test-Path $templatePath)) {
        Write-Error "Release notes template not found at $templatePath"
        exit 1
    }
}

Write-Host "`nUpdating files..." -ForegroundColor Cyan

# ~ Update StS2AP.csproj ModVersion ~
$csprojPath = Join-Path $RepoRoot "client\StS2AP\StS2AP.csproj"
if (-not (Test-Path $csprojPath)) {
    Write-Error "File not found: $csprojPath"
    exit 1
}
$csprojContent = Get-Content $csprojPath -Raw
$csprojPattern = '<ModVersion Condition=".*?">[^<]*</ModVersion>'
$csprojReplacement = "<ModVersion Condition=`"'`$(ModVersion)' == ''`">$SemVer</ModVersion>"
$csprojNew = $csprojContent -replace $csprojPattern, $csprojReplacement
if ($csprojNew -ne $csprojContent) {
    Set-Utf8NoBomContent -Path $csprojPath -Content $csprojNew
    Write-Host "  Updated: StS2AP.csproj (ModVersion)" -ForegroundColor Green
} elseif ($csprojContent -match $csprojPattern) {
    Write-Host "  Already up to date: StS2AP.csproj (ModVersion)" -ForegroundColor Yellow
} else {
    Write-Warning "  No match found in StS2AP.csproj"
}

# ~ Update local.props.template ModVersion ~
$localPropsTemplatePath = Join-Path $RepoRoot "client\StS2AP\local.props.template"
if (-not (Test-Path $localPropsTemplatePath)) {
    Write-Error "File not found: $localPropsTemplatePath"
    exit 1
}
$localPropsTemplateContent = Get-Content $localPropsTemplatePath -Raw
$localPropsTemplatePattern = '<ModVersion>[^<]*</ModVersion>'
$localPropsTemplateReplacement = "<ModVersion>$SemVer</ModVersion>"
$localPropsTemplateNew = $localPropsTemplateContent -replace $localPropsTemplatePattern, $localPropsTemplateReplacement
if ($localPropsTemplateNew -ne $localPropsTemplateContent) {
    Set-Utf8NoBomContent -Path $localPropsTemplatePath -Content $localPropsTemplateNew
    Write-Host "  Updated: local.props.template (ModVersion)" -ForegroundColor Green
} elseif ($localPropsTemplateContent -match $localPropsTemplatePattern) {
    Write-Host "  Already up to date: local.props.template (ModVersion)" -ForegroundColor Yellow
} else {
    Write-Warning "  No match found in local.props.template"
}

# ~ Update local.props ModVersion ~
$localPropsPath = Join-Path $RepoRoot "client\StS2AP\local.props"
if (-not (Test-Path $localPropsPath)) {
    Write-Warning "  local.props not found at $localPropsPath - skipping."
} else {
    $localPropsContent = Get-Content $localPropsPath -Raw
    $localPropsPattern = '<ModVersion>[^<]*</ModVersion>'
    $localPropsReplacement = "<ModVersion>$SemVer</ModVersion>"
    $localPropsNew = $localPropsContent -replace $localPropsPattern, $localPropsReplacement
    if ($localPropsNew -ne $localPropsContent) {
        Set-Utf8NoBomContent -Path $localPropsPath -Content $localPropsNew
        Write-Host "  Updated: local.props (ModVersion)" -ForegroundColor Green
    } elseif ($localPropsContent -match $localPropsPattern) {
        Write-Host "  Already up to date: local.props (ModVersion)" -ForegroundColor Yellow
    } else {
        Write-Warning "  No match found in local.props"
    }
}

# ~ Update world/spire2/archipelago.json world_version ~
$worldJsonPath = Join-Path $RepoRoot "world\spire2\archipelago.json"
if (-not (Test-Path $worldJsonPath)) {
    Write-Error "File not found: $worldJsonPath"
    exit 1
}
$worldJsonContent = Get-Content $worldJsonPath -Raw
$worldJsonPattern = '"world_version"\s*:\s*"[^"]+"'
$worldJsonReplacement = "`"world_version`": `"$SemVer`""
$worldJsonNew = $worldJsonContent -replace $worldJsonPattern, $worldJsonReplacement
if ($worldJsonNew -ne $worldJsonContent) {
    Set-Utf8NoBomContent -Path $worldJsonPath -Content $worldJsonNew
    Write-Host "  Updated: world/spire2/archipelago.json (world_version)" -ForegroundColor Green
} elseif ($worldJsonContent -match $worldJsonPattern) {
    Write-Host "  Already up to date: world/spire2/archipelago.json (world_version)" -ForegroundColor Yellow
} else {
    Write-Warning "  No match found in world/spire2/archipelago.json"
}

# ~ Update client/StS2AP/Archipelago.json version ~
$clientJsonPath = Join-Path $RepoRoot "client\StS2AP\Archipelago.json"
if (-not (Test-Path $clientJsonPath)) {
    Write-Error "File not found: $clientJsonPath"
    exit 1
}
$clientJsonContent = Get-Content $clientJsonPath -Raw
$clientJsonPattern = '"version"\s*:\s*"[^"]+"'
$clientJsonReplacement = "`"version`": `"$SemVer`""
$clientJsonNew = $clientJsonContent -replace $clientJsonPattern, $clientJsonReplacement
if ($clientJsonNew -ne $clientJsonContent) {
    Set-Utf8NoBomContent -Path $clientJsonPath -Content $clientJsonNew
    Write-Host "  Updated: client/StS2AP/Archipelago.json (version)" -ForegroundColor Green
} elseif ($clientJsonContent -match $clientJsonPattern) {
    Write-Host "  Already up to date: client/StS2AP/Archipelago.json (version)" -ForegroundColor Yellow
} else {
    Write-Warning "  No match found in client/StS2AP/Archipelago.json"
}

# ~ Update world/spire2/world.py mod_compat_version ~
$worldPyPath = Join-Path $RepoRoot "world\spire2\world.py"
if (-not (Test-Path $worldPyPath)) {
    Write-Error "File not found: $worldPyPath"
    exit 1
}
$worldPyContent = Get-Content $worldPyPath -Raw
$worldPyPattern = '(mod_compat_version\s*=\s*")[^"]+"'
$worldPyReplacement = "`${1}$SemVer`""
$worldPyNew = $worldPyContent -replace $worldPyPattern, $worldPyReplacement
if ($worldPyNew -ne $worldPyContent) {
    Set-Utf8NoBomContent -Path $worldPyPath -Content $worldPyNew
    Write-Host "  Updated: world/spire2/world.py (mod_compat_version)" -ForegroundColor Green
} elseif ($worldPyContent -match $worldPyPattern) {
    Write-Host "  Already up to date: world/spire2/world.py (mod_compat_version)" -ForegroundColor Yellow
} else {
    Write-Warning "  No match found in world/spire2/world.py"
}

# ~ Commit version bump ~
# Stage only the files we just modified and create a commit titled with the version.
# This commit will be used as the tagged commit for the release.
Write-Host "`nCommitting version bump..." -ForegroundColor Cyan
git -C $RepoRoot add `
    "client/StS2AP/StS2AP.csproj" `
    "client/StS2AP/local.props.template" `
    "client/StS2AP/Archipelago.json" `
    "world/spire2/archipelago.json" `
    "world/spire2/world.py"
$gitAddExit = $LASTEXITCODE
if ($gitAddExit -ne 0) {
    Write-Error "git add failed (exit code $gitAddExit)."
    exit 1
}
git -C $RepoRoot diff --cached --quiet
$gitDiffExit = $LASTEXITCODE
if ($gitDiffExit -eq 0) {
    Write-Host "  Version already committed; using current HEAD." -ForegroundColor Yellow
} elseif ($gitDiffExit -eq 1) {
    git -C $RepoRoot commit --message $Version
    $gitCommitExit = $LASTEXITCODE
    if ($gitCommitExit -ne 0) {
        Write-Error "git commit failed (exit code $gitCommitExit)."
        exit 1
    }

    $commitParent = git -C $RepoRoot rev-parse HEAD^ 2>&1
    if ($LASTEXITCODE -ne 0 -or $commitParent -ne $releaseBaseCommit) {
        Write-Error "The version commit is not based directly on the verified $ReleaseRemote/main commit. Nothing has been pushed."
        exit 1
    }
    $unexpectedCommitPaths = @(git -C $RepoRoot diff-tree --no-commit-id --name-only -r HEAD | Where-Object { $_ -notin $ReleasePaths })
    if ($unexpectedCommitPaths.Count -gt 0) {
        Write-Error "The version commit contains unexpected paths: $($unexpectedCommitPaths -join ', '). Nothing has been pushed."
        exit 1
    }
    Write-Host "  Committed: $Version" -ForegroundColor Green
} else {
    Write-Error "git diff failed (exit code $gitDiffExit)."
    exit 1
}

# ~ Sync world source into Archipelago repo ~
Write-Host "`nSyncing world source into Archipelago repo..." -ForegroundColor Cyan
$archRepoRoot = Resolve-Path (Join-Path $RepoRoot "..") | Select-Object -ExpandProperty Path
$archWorldsDir = Join-Path $archRepoRoot "Archipelago\worlds"
$archSpire2Dir = Join-Path $archWorldsDir "spire2"

if (Test-Path $archSpire2Dir) {
    Remove-Item -Recurse -Force $archSpire2Dir
    Write-Host "  Deleted: $archSpire2Dir" -ForegroundColor Green
}

$localSpire2Dir = Join-Path $RepoRoot "world\spire2"
Copy-Item -Path $localSpire2Dir -Destination $archWorldsDir -Recurse -Force
Write-Host "  Copied: world\spire2 -> $archWorldsDir" -ForegroundColor Green

# ~ Build APWorld ~
Write-Host "`nBuilding APWorld..." -ForegroundColor Cyan
$launcherPath = Join-Path $archRepoRoot "Archipelago\Launcher.py"
if (-not (Test-Path $launcherPath)) {
    Write-Error "Launcher.py not found at $launcherPath"
    exit 1
}
# Temporarily lower error preference so Python stderr warnings don't abort the script
$prevPref = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$archDir2 = Join-Path $archRepoRoot "Archipelago"
Push-Location $archDir2
py -3.13 $launcherPath "Build APWorlds" "Slay the Spire II"
$apworldExitCode = $LASTEXITCODE
Pop-Location
$ErrorActionPreference = $prevPref
if ($apworldExitCode -ne 0) {
    Write-Error "APWorld build failed (exit code $apworldExitCode)."
    exit 1
}
Write-Host "  APWorld build succeeded." -ForegroundColor Green

# ~ Copy spire2.apworld to dist (for C# build to pick up) ~
$apworldSource = Join-Path $archRepoRoot "Archipelago\build\apworlds\spire2.apworld"
$distDir = Join-Path $RepoRoot "dist"
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
if (Test-Path $apworldSource) {
    Copy-Item -Path $apworldSource -Destination $distDir -Force
    Write-Host "  Copied: spire2.apworld to $distDir" -ForegroundColor Green
} else {
    Write-Error "spire2.apworld not found at $apworldSource after successful build."
    exit 1
}

# ~ Build C# client ~
Write-Host "`nBuilding C# client (Release)..." -ForegroundColor Cyan
$csprojPath = Join-Path $RepoRoot "client\StS2AP\StS2AP.csproj"
$buildResult = dotnet build $csprojPath -c Release 2>&1
$buildExitCode = $LASTEXITCODE

if ($buildExitCode -ne 0) {
    Write-Host ($buildResult | Out-String) -ForegroundColor Red
    Write-Error "Build failed (exit code $buildExitCode). Check that local.props is configured."
    exit 1
}
Write-Host "  Build succeeded." -ForegroundColor Green

# ~ Locate the .pck file from the mods output directory ~
$msbuildJson = dotnet msbuild $csprojPath -getProperty:ModsOutputDir -getProperty:ModName 2>$null | ConvertFrom-Json
$modsOutputDir = $msbuildJson.Properties.ModsOutputDir
$modName = $msbuildJson.Properties.ModName
$pckPath = Join-Path $modsOutputDir "$modName.pck"

if (-not (Test-Path $pckPath)) {
    Write-Warning "  .pck file not found at $pckPath - it will not be included in the zip."
    Write-Warning "  Ensure Godot is installed and GodotExePath is set in local.props."
}

# ~ Prepare release artifacts ~
Write-Host "`nPreparing files for the new release..." -ForegroundColor Cyan
$outputDir = Join-Path $RepoRoot "client\StS2AP\bin\Release\net9.0"
$zipPath = Join-Path $distDir "sts2-client.zip"

if (-not (Test-Path $outputDir)) {
    Write-Error "Build output directory not found: $outputDir"
    exit 1
}

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

# Collect DLLs and config from build output, excluding debug artifacts and game-provided DLLs
$filesToZip = Get-ChildItem -Path $outputDir -File | Where-Object {
    $_.Extension -notin @('.pdb', '.xml') -and
    $_.Name -notlike '*.deps.json' -and
    $_.Name -notin @('sts2.dll', '0Harmony.dll', 'GodotSharp.dll')
}

# Include the .pck from the mods output directory
if (Test-Path $pckPath) {
    $filesToZip = @($filesToZip) + @(Get-Item $pckPath)
}

if (-not $filesToZip) {
    Write-Error "No files found to zip in $outputDir"
    exit 1
}

# Create a temporary directory with Archipelago subfolder for zipping
$tempDir = Join-Path $env:TEMP "sts2release-$(Get-Random)"
$archDir = Join-Path $tempDir "Archipelago"
New-Item -ItemType Directory -Force -Path $archDir | Out-Null

try {
    # Copy files into Archipelago folder
    foreach ($file in $filesToZip) {
        Copy-Item -Path $file.FullName -Destination $archDir -Force
    }

    # Zip the Archipelago folder directly (so zip contains Archipelago > files)
    Compress-Archive -Path $archDir -DestinationPath $zipPath -Force
    if (-not (Test-Path $zipPath)) {
        Write-Error "Failed to create $zipPath"
        exit 1
    }
    $fileCount = $filesToZip.Count
    Write-Host "  Created: sts2-client.zip [$fileCount files]" -ForegroundColor Green
} finally {
    # Clean up temp directory
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
}

# Verify spire2.apworld is in dist (should have been copied earlier)
$apworldPath = Join-Path $distDir "spire2.apworld"
if (-not (Test-Path $apworldPath)) {
    Write-Warning "  spire2.apworld not found in dist folder."
}

# ~ Tag the version commit ~
# Tag HEAD (the version-bump commit we just created) with the release version,
# or reuse the verified tag from an interrupted release run.
git -C $RepoRoot show-ref --verify --quiet "refs/tags/$Version"
if ($LASTEXITCODE -eq 0) {
    $localTagCommit = git -C $RepoRoot rev-parse "refs/tags/$Version^{commit}" 2>&1
    $currentHead = git -C $RepoRoot rev-parse HEAD 2>&1
    if ($LASTEXITCODE -ne 0 -or $localTagCommit -ne $currentHead) {
        Write-Error "Existing local tag '$Version' does not point to the verified release commit."
        exit 1
    }
    Write-Host "  Reusing existing local tag: $Version" -ForegroundColor Yellow
} else {
    git -C $RepoRoot tag $Version HEAD
    $gitTagExit = $LASTEXITCODE
    if ($gitTagExit -ne 0) {
        Write-Error "git tag failed (exit code $gitTagExit)."
        exit 1
    }
    Write-Host "  Tagged HEAD as: $Version" -ForegroundColor Green
}

if ($skipGitHub) {
    Write-Host "`nSkipping GitHub push and release (-skipGitHub specified)." -ForegroundColor Yellow
    Write-Host "  Commit and tag '$Version' created locally only." -ForegroundColor Yellow
} else {
    # ~ Push only the release tag, then create the GitHub Release. The tag
    # carries the controlled version-only commit while protected main remains
    # unchanged. ~
    Write-Host "`nPushing release tag to GitHub..." -ForegroundColor Cyan

    git -C $RepoRoot push $ReleaseRemote "refs/tags/$Version"
    $gitPushTagExit = $LASTEXITCODE
    if ($gitPushTagExit -ne 0) {
        Write-Error "git push tag failed (exit code $gitPushTagExit)."
        exit 1
    }
    Write-Host "  Pushed: tag $Version" -ForegroundColor Green

    # ~ Create GitHub Release ~
    Write-Host "`nCreating GitHub release..." -ForegroundColor Cyan

    # Generate release notes from template
    $releaseNotes = (Get-Content $templatePath -Raw) -replace '\{\{VERSION\}\}', $Version

    $releaseNotesFile = Join-Path $env:TEMP "sts2-release-notes-$(Get-Random).md"
    Set-Utf8NoBomContent -Path $releaseNotesFile -Content $releaseNotes

    try {
        # Upload only the two intended release artifacts. Other local files in
        # dist must never be published implicitly.
        $assetArgs = @($zipPath, $apworldPath)
        $missingAssets = @($assetArgs | Where-Object { -not (Test-Path $_) })
        if ($missingAssets.Count -gt 0) {
            Write-Error "Required release assets are missing: $($missingAssets -join ', ')"
            exit 1
        }

        # Create the release
        gh release create $Version @assetArgs --repo $ReleaseRepo --title $Version --notes-file $releaseNotesFile --latest
        $ghReleaseExit = $LASTEXITCODE
        if ($ghReleaseExit -ne 0) {
            Write-Error "GitHub release creation failed for $ReleaseRepo (exit code $ghReleaseExit)."
            exit 1
        }

        Write-Host "  Release '$Version' created in $ReleaseRepo and marked as latest." -ForegroundColor Green
        Write-Host "  Don't forget to update the Changelist in the release notes on GitHub!" -ForegroundColor Yellow
    } finally {
        Remove-Item $releaseNotesFile -ErrorAction SilentlyContinue
    }
}

Write-Host "`nDone!" -ForegroundColor Green
