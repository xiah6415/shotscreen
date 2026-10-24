# Builds, signs and uploads 拾光 for Windows to the GitHub Release v<VERSION> (the counterpart of scripts/release.sh).
# If the macOS release for this version already exists, the Windows files are added to it; otherwise the release is
# created. The app's update check picks the files up.
#   powershell -ExecutionPolicy Bypass -File windows/scripts/release.ps1             # notes = commit subjects since the previous tag
#   powershell -ExecutionPolicy Bypass -File windows/scripts/release.ps1 -Notes notes.md
#   powershell -ExecutionPolicy Bypass -File windows/scripts/release.ps1 -DryRun     # build only, publish nothing
#   ... -Yes skips the confirmation question (when it was already given, e.g. to an assistant running the script)
param([string]$Notes, [switch]$DryRun, [switch]$Yes)
$ErrorActionPreference = 'Stop'
$repo = 'xiah6415/shotscreen'

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Push-Location $root
try {
    $version = (Get-Content VERSION -Raw).Trim()
    $tag = "v$version"
    if (-not $DryRun) {
        if (git status --porcelain) { throw "Commit or stash changes first: the release must match a commit" }
        git fetch --quiet origin
        $branch = git rev-parse --abbrev-ref HEAD
        git merge-base --is-ancestor HEAD "origin/$branch"
        if ($LASTEXITCODE -ne 0) { throw "Push $branch first" }
    }

    & (Join-Path $PSScriptRoot 'publish.ps1') -RequireSignature
    $exe = Join-Path $root "windows\dist\Glean-$version-win-x64.exe"
    $files = @($exe, "$exe.sha256")

    if ($DryRun) { Write-Output "Dry run: nothing published"; return }

    gh release view $tag --repo $repo --json assets --jq '.assets[].name' 2>$null | Set-Variable existing
    $releaseExists = $LASTEXITCODE -eq 0
    if ($releaseExists -and ($existing -contains (Split-Path $exe -Leaf))) { throw "$tag already has the Windows build; bump VERSION" }

    if (-not $Yes) {
        $answer = Read-Host ($(if ($releaseExists) { "Add the Windows build to the existing $tag on github.com/$repo? [y/N]" } else { "Create $tag on github.com/$repo with the Windows build? [y/N]" }))
        if ($answer -notmatch '^[yY]') { Write-Output "Cancelled"; exit 1 }
    }

    if ($releaseExists) {
        gh release upload $tag @files --repo $repo
    } else {
        $notesFile = Join-Path $root "windows\dist\notes-$version.md"
        if ($Notes) { Copy-Item $Notes $notesFile -Force }
        else {
            $previous = git describe --tags --abbrev=0 --match 'v*' 2>$null
            if ($previous) { git log --no-merges --format='- %s' "$previous..HEAD" | Set-Content $notesFile -Encoding UTF8 }
            else { git log --no-merges --format='- %s' | Set-Content $notesFile -Encoding UTF8 }
        }
        gh release create $tag @files --repo $repo --target (git rev-parse HEAD) --title "拾光 Glean $version" --notes-file $notesFile
    }
    if ($LASTEXITCODE -ne 0) { throw "gh failed" }
    Write-Output "Published the Windows build to $tag"
} finally {
    Pop-Location
}
