param(
    [string]$Version,
    [string]$DestDir
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$url = "https://github.com/git-for-windows/git/releases/download/v${Version}.windows.1/MinGit-${Version}-64-bit.zip"
$zip = Join-Path $env:TEMP 'mingit.zip'

Write-Host "Downloading MinGit v$Version from $url ..."
Invoke-WebRequest $url -OutFile $zip
Write-Host "Extracting to $DestDir ..."
Expand-Archive $zip -DestinationPath $DestDir -Force
Remove-Item $zip
Write-Host "MinGit v$Version ready."
