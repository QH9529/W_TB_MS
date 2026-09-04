[CmdletBinding()]
param(
    [string]$Version,
    [string]$InstallRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "deployment"),
    [switch]$Start
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$installRootPath = [IO.Path]::GetFullPath($InstallRoot)
$driveRoot = [IO.Path]::GetPathRoot($installRootPath)
if ($installRootPath.TrimEnd('\') -eq $driveRoot.TrimEnd('\')) {
    throw "安装目录不能是磁盘根目录。"
}

$versionsRoot = Join-Path $installRootPath "versions"
$currentPath = Join-Path $installRootPath "current"
$availableVersions = @(Get-ChildItem -LiteralPath $versionsRoot -Directory -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending)

if ([string]::IsNullOrWhiteSpace($Version)) {
    if ($availableVersions.Count -eq 0) {
        Write-Host "没有可回档版本。"
    }
    else {
        Write-Host "可回档版本："
        $availableVersions | ForEach-Object { Write-Host "  $($_.Name)" }
    }
    return
}

$selectedVersion = $availableVersions | Where-Object Name -eq $Version | Select-Object -First 1
if (-not $selectedVersion) {
    throw "找不到回档版本: $Version"
}
if (-not (Test-Path -LiteralPath (Join-Path $selectedVersion.FullName "W_TB_MS.exe"))) {
    throw "所选版本不完整，缺少 W_TB_MS.exe。"
}

$stagingPath = Join-Path $installRootPath (".rollback-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
Get-ChildItem -LiteralPath $selectedVersion.FullName -Force |
    Copy-Item -Destination $stagingPath -Recurse -Force

Get-Process W_TB_MS -ErrorAction SilentlyContinue | Stop-Process -Force
$failedVersionPath = $null
if (Test-Path -LiteralPath $currentPath) {
    $failedVersionPath = Join-Path $versionsRoot ("replaced-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
    Move-Item -LiteralPath $currentPath -Destination $failedVersionPath
}

try {
    Move-Item -LiteralPath $stagingPath -Destination $currentPath
}
catch {
    if ($failedVersionPath -and (Test-Path -LiteralPath $failedVersionPath) -and -not (Test-Path -LiteralPath $currentPath)) {
        Move-Item -LiteralPath $failedVersionPath -Destination $currentPath
    }
    throw
}

Write-Host "已恢复版本: $Version"
if ($Start) {
    Start-Process -FilePath (Join-Path $currentPath "W_TB_MS.exe")
}

