# Stages bundled MinGit + git-lfs into ./mingit so the build can copy them next to the exe.
# CI invokes this; local devs run the same script — keeps both paths in sync.
# Versions are pinned to match what the resolver expects at runtime.

[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'mingit')
)

$ErrorActionPreference = 'Stop'

# PS 5.1 defaults to TLS 1.0 which GitHub release downloads will reject. pwsh 7+ is fine,
# but force TLS 1.2 unconditionally so the script works on either.
[Net.ServicePointManager]::SecurityProtocol =
    [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$mingitVersion = '2.49.0'
$lfsVersion    = '3.6.1'
# Try GitHub directly first (fast on CI runners); fall back to the project's CDN proxy
# which mirrors the same paths and is reachable from regions where github.com is flaky.
$mingitUrls = @(
    "https://github.com/git-for-windows/git/releases/download/v$mingitVersion.windows.1/MinGit-$mingitVersion-64-bit.zip",
    "https://sts-dl.rkto.cc/git-for-windows/git/releases/download/v$mingitVersion.windows.1/MinGit-$mingitVersion-64-bit.zip"
)
$lfsUrls = @(
    "https://github.com/git-lfs/git-lfs/releases/download/v$lfsVersion/git-lfs-windows-amd64-v$lfsVersion.zip",
    "https://sts-dl.rkto.cc/git-lfs/git-lfs/releases/download/v$lfsVersion/git-lfs-windows-amd64-v$lfsVersion.zip"
)

function Invoke-DownloadWithFallback {
    param([string[]]$Urls, [string]$OutFile, [string]$Label)
    foreach ($url in $Urls) {
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            try {
                Write-Host "  -> $url (attempt $attempt)"
                Invoke-WebRequest -Uri $url -OutFile $OutFile -UseBasicParsing
                return
            } catch {
                Write-Warning "     failed: $($_.Exception.Message)"
                if (Test-Path $OutFile) { Remove-Item -Force $OutFile }
                if ($attempt -lt 2) { Start-Sleep -Seconds 2 }
            }
        }
    }
    throw "Failed to download $Label from any source."
}

if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# work in a temp dir so a partial run doesn't leave half-extracted zips beside the build tree
$work = Join-Path ([System.IO.Path]::GetTempPath()) "fetch-mingit-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    $mingitZip = Join-Path $work 'mingit.zip'
    Write-Host "Downloading MinGit $mingitVersion..."
    Invoke-DownloadWithFallback -Urls $mingitUrls -OutFile $mingitZip -Label "MinGit $mingitVersion"
    Write-Host "Extracting MinGit -> $OutDir"
    Expand-Archive -Path $mingitZip -DestinationPath $OutDir -Force

    $lfsZip     = Join-Path $work 'gitlfs.zip'
    $lfsExtract = Join-Path $work 'lfs'
    Write-Host "Downloading git-lfs $lfsVersion..."
    Invoke-DownloadWithFallback -Urls $lfsUrls -OutFile $lfsZip -Label "git-lfs $lfsVersion"
    Write-Host "Extracting git-lfs..."
    Expand-Archive -Path $lfsZip -DestinationPath $lfsExtract -Force
    $lfsExe = Get-ChildItem $lfsExtract -Recurse -Filter git-lfs.exe | Select-Object -First 1
    if (-not $lfsExe) { throw "git-lfs.exe not found in extracted archive" }
    $lfsTarget = Join-Path $OutDir 'mingw64\bin\git-lfs.exe'
    Copy-Item $lfsExe.FullName -Destination $lfsTarget -Force

    # prune dead weight:
    #   cmd/                       — thin git.exe launcher; we spawn mingw64/bin/git.exe directly
    #   mingw64/share/locale/      — git's UI translations; never surfaced through IPC
    #   usr/share/locale/          — msys utility translations; same story
    #   mingw64/bin/scalar.exe     — standalone tool for huge mono-repos; git itself doesn't call it,
    #                                we don't either. 14 MB of unique LZX-incompressible bytes.
    Remove-Item -Recurse -Force (Join-Path $OutDir 'cmd')                  -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force (Join-Path $OutDir 'mingw64\share\locale') -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force (Join-Path $OutDir 'usr\share\locale')     -ErrorAction SilentlyContinue
    Remove-Item -Force          (Join-Path $OutDir 'mingw64\bin\scalar.exe') -ErrorAction SilentlyContinue

    Write-Host "MinGit + git-lfs ready at $OutDir"
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
