[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet("win-x86", "win-x64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $repositoryRoot ".dotnet-cli"
$env:NUGET_PACKAGES = Join-Path $repositoryRoot ".nuget-packages"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Content -LiteralPath (Join-Path $repositoryRoot "VERSION") -Raw).Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') {
    throw "版本号格式无效: $Version"
}
if (Test-Path -LiteralPath (Join-Path $repositoryRoot ".dotnet-sdk\dotnet.exe")) {
    $dotnetCommand = Join-Path $repositoryRoot ".dotnet-sdk\dotnet.exe"
}
else {
    $systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $dotnetCommand = if ($systemDotnet) { $systemDotnet.Source } else { $null }
}
if ([string]::IsNullOrWhiteSpace($dotnetCommand) -or -not (& $dotnetCommand --list-sdks)) {
    throw "未找到 .NET SDK，请先安装 .NET 8 SDK。"
}

$solutionPath = Join-Path $repositoryRoot "W_TB_MS.sln"
$projectPath = Join-Path $repositoryRoot "W_TB_MS\W_TB_jiankong.csproj"
$testProjectPath = Join-Path $repositoryRoot "W_TB_MS.Tests\W_TB_jiankong.Tests.csproj"
$artifactsRoot = Join-Path $repositoryRoot "artifacts"
$stagingRoot = Join-Path $artifactsRoot ".staging"
$publishDirectory = Join-Path $stagingRoot "W_TB_MS-v$Version-$Runtime"
$packagePath = Join-Path $artifactsRoot "W_TB_MS-v$Version-$Runtime.zip"
$hashPath = "$packagePath.sha256"

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

& $dotnetCommand restore $solutionPath
if ($LASTEXITCODE -ne 0) { throw "依赖恢复失败。" }

& $dotnetCommand test $testProjectPath -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "测试失败，拒绝生成发布包。" }

& $dotnetCommand publish $projectPath `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "发布失败。" }

[IO.File]::WriteAllText(
    (Join-Path $publishDirectory "VERSION.txt"),
    "$Version`r`n",
    [Text.UTF8Encoding]::new($false))

foreach ($path in @($packagePath, $hashPath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}
Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $packagePath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(
    $hashPath,
    "$hash  $([IO.Path]::GetFileName($packagePath))`r`n",
    [Text.UTF8Encoding]::new($false))

Remove-Item -LiteralPath $stagingRoot -Recurse -Force
Write-Host "发布包: $packagePath"
Write-Host "SHA256: $hash"
