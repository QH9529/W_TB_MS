[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,
    [string]$InstallRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "deployment"),
    [ValidateRange(1, 20)]
    [int]$KeepVersions = 5,
    [switch]$Start
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$package = (Resolve-Path -LiteralPath $PackagePath).Path
if ([IO.Path]::GetExtension($package) -ne ".zip") {
    throw "版本包必须是 ZIP 文件。"
}

$installRootPath = [IO.Path]::GetFullPath($InstallRoot)
$driveRoot = [IO.Path]::GetPathRoot($installRootPath)
if ($installRootPath.TrimEnd('\') -eq $driveRoot.TrimEnd('\')) {
    throw "安装目录不能是磁盘根目录。"
}

$hashPath = "$package.sha256"
if (Test-Path -LiteralPath $hashPath) {
    $expectedHash = ((Get-Content -LiteralPath $hashPath -Raw).Trim() -split '\s+')[0]
    $actualHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
    if (-not $actualHash.Equals($expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "版本包 SHA256 校验失败。"
    }
}

$versionsRoot = Join-Path $installRootPath "versions"
$currentPath = Join-Path $installRootPath "current"
$stagingPath = Join-Path $installRootPath (".staging-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $versionsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null

try {
    Expand-Archive -LiteralPath $package -DestinationPath $stagingPath -Force
    $newExecutable = Join-Path $stagingPath "W_TB_MS.exe"
    if (-not (Test-Path -LiteralPath $newExecutable)) {
        throw "版本包内未找到 W_TB_MS.exe。"
    }

    Get-Process W_TB_MS -ErrorAction SilentlyContinue | Stop-Process -Force
    $backupPath = $null
    if (Test-Path -LiteralPath $currentPath) {
        $backupPath = Join-Path $versionsRoot ("backup-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
        Move-Item -LiteralPath $currentPath -Destination $backupPath
    }

    try {
        Move-Item -LiteralPath $stagingPath -Destination $currentPath
    }
    catch {
        if ($backupPath -and (Test-Path -LiteralPath $backupPath) -and -not (Test-Path -LiteralPath $currentPath)) {
            Move-Item -LiteralPath $backupPath -Destination $currentPath
        }
        throw
    }

    Get-ChildItem -LiteralPath $versionsRoot -Directory |
        Sort-Object LastWriteTime -Descending |
        Select-Object -Skip $KeepVersions |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }

    Write-Host "已安装: $currentPath"
    if ($Start) {
        Start-Process -FilePath (Join-Path $currentPath "W_TB_MS.exe")
    }
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }
}

