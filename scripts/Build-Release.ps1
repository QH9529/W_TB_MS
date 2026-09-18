[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet("win-x86", "win-x64")]
    [string]$Runtime = "win-x64",
    # 跳过测试（快速本地构建时使用）
    [switch]$SkipTests
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
$projectPath = Join-Path $repositoryRoot "W_TB_MS\W_TB_MS.csproj"
$testProjectPath = Join-Path $repositoryRoot "W_TB_MS.Tests\W_TB_MS.Tests.csproj"
$setupBuilderPath = Join-Path $repositoryRoot "artifacts\setup-builder\SetupBuilder.csproj"
$artifactsRoot = Join-Path $repositoryRoot "artifacts"
$stagingRoot = Join-Path $artifactsRoot ".staging"
$setupPath = Join-Path $artifactsRoot "W_TB_MS-v$Version-Setup.exe"

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

& $dotnetCommand restore $solutionPath
if ($LASTEXITCODE -ne 0) { throw "依赖恢复失败。" }

if (-not $SkipTests) {
    & $dotnetCommand test $testProjectPath -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "测试失败，拒绝生成发布包。" }
}

# 1. 发布 x64 / x86 两个运行时目录
$publishPaths = @{}
foreach ($rt in @("win-x64", "win-x86")) {
    $publishDirectory = Join-Path $stagingRoot "W_TB_MS-v$Version-$rt"
    $publishPaths[$rt] = $publishDirectory
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

    & $dotnetCommand publish $projectPath `
        -c Release `
        -r $rt `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:Version=$Version `
        -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw "发布失败（$rt）。" }

    [IO.File]::WriteAllText(
        (Join-Path $publishDirectory "VERSION.txt"),
        "$Version`r`n",
        [Text.UTF8Encoding]::new($false))
}

# 2. 将两个运行时目录压缩为 payload，供安装程序嵌入
$setupBuilderDir = Split-Path -Parent $setupBuilderPath
$payloadPaths = @{}
foreach ($rt in @("win-x64", "win-x86")) {
    $payloadPath = Join-Path $setupBuilderDir "payload-$rt.zip"
    $payloadPaths[$rt] = $payloadPath
    if (Test-Path -LiteralPath $payloadPath) {
        Remove-Item -LiteralPath $payloadPath -Force
    }
    Compress-Archive -Path (Join-Path $publishPaths[$rt] "*") -DestinationPath $payloadPath -CompressionLevel Optimal
}

# 3. 同步安装程序源码中的版本号（界面标题、资源名等使用硬编码版本），必须在编译前执行
$programCsPath = Join-Path $setupBuilderDir "Program.cs"
$programCs = [IO.File]::ReadAllText($programCsPath)
$programCs = $programCs -replace 'v\d+\.\d+\.\d+', "v$Version"
[IO.File]::WriteAllText($programCsPath, $programCs, [Text.UTF8Encoding]::new($false))

# 4. 编译安装程序（payload 作为嵌入资源打进单个 Setup.exe）
if (Test-Path -LiteralPath $setupPath) {
    Remove-Item -LiteralPath $setupPath -Force
}
& $dotnetCommand publish $setupBuilderPath `
    -c Release `
    -p:Version=$Version `
    -p:AssemblyName="W_TB_MS-v$Version-Setup" `
    -o $setupBuilderDir
if ($LASTEXITCODE -ne 0) { throw "安装程序编译失败。" }

$builtSetup = Join-Path $setupBuilderDir "W_TB_MS-v$Version-Setup.exe"
if (-not (Test-Path -LiteralPath $builtSetup)) {
    throw "未找到编译产物：$builtSetup"
}
Move-Item -LiteralPath $builtSetup -Destination $setupPath -Force

Remove-Item -LiteralPath $stagingRoot -Recurse -Force

Write-Host "安装包: $setupPath"
