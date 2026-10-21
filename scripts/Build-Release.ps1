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
# 安装器源码纳入版本库（tools/setup-builder），不可放在被 .gitignore 忽略的 artifacts/ 下，
# 否则 CI 干净检出后找不到 SetupBuilder.csproj，构建会直接失败。
$setupBuilderPath = Join-Path $repositoryRoot "tools\setup-builder\SetupBuilder.csproj"
$artifactsRoot = Join-Path $repositoryRoot "artifacts"
$stagingRoot = Join-Path $artifactsRoot ".staging"
$setupPath = Join-Path $artifactsRoot "W_TB_MS-v$Version-Setup.exe"

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

# PowerShell 5.1 兼容：dotnet 会把进度/警告写入 stderr，在 $ErrorActionPreference = "Stop" 下
# 会被当作致命错误直接终止脚本（NativeCommandError），导致 restore 明明成功却构建失败。
# 因此原生命令统一在 Continue 下执行，成败由 $LASTEXITCODE 显式判定。
$ErrorActionPreference = "Continue"
& $dotnetCommand restore $solutionPath
$restoreExitCode = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($restoreExitCode -ne 0) { throw "依赖恢复失败。" }

if (-not $SkipTests) {
    $ErrorActionPreference = "Continue"
    & $dotnetCommand test $testProjectPath -c Release --no-restore
    $testExitCode = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($testExitCode -ne 0) { throw "测试失败，拒绝生成发布包。" }
}

# 1. 发布 x64 / x86 两个运行时目录
$publishPaths = @{}
foreach ($rt in @("win-x64", "win-x86")) {
    $publishDirectory = Join-Path $stagingRoot "W_TB_MS-v$Version-$rt"
    $publishPaths[$rt] = $publishDirectory
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

    $ErrorActionPreference = "Continue"
    & $dotnetCommand publish $projectPath `
        -c Release `
        -r $rt `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:Version=$Version `
        -o $publishDirectory
    $publishExitCode = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($publishExitCode -ne 0) { throw "发布失败（$rt）。" }

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
        try {
            Remove-Item -LiteralPath $payloadPath -Force -ErrorAction Stop
        }
        catch {
            # 旧 payload 删除失败时交由 Compress-Archive 覆盖，不终止本次构建
            Write-Warning "旧 payload 删除失败，将由压缩步骤覆盖：$payloadPath"
        }
    }
    Compress-Archive -Path (Join-Path $publishPaths[$rt] "*") -DestinationPath $payloadPath -CompressionLevel Optimal -Force
}

# 3. 同步安装程序源码中的版本号（界面标题、资源名等使用硬编码版本），必须在编译前执行
#    Program.cs 已纳入版本库，替换后的内容只在本次编译期间生效，编译结束（无论成败）立即还原，
#    避免每次构建都把源码改成当前版本、造成工作区出现无意义改动。
$programCsPath = Join-Path $setupBuilderDir "Program.cs"
$programCsOriginal = [IO.File]::ReadAllText($programCsPath)

try {
    $programCs = $programCsOriginal -replace 'v\d+\.\d+\.\d+', "v$Version"
    [IO.File]::WriteAllText($programCsPath, $programCs, [Text.UTF8Encoding]::new($false))

    # 4. 编译安装程序（payload 作为嵌入资源打进单个 Setup.exe）
    if (Test-Path -LiteralPath $setupPath) {
        try {
            Remove-Item -LiteralPath $setupPath -Force -ErrorAction Stop
        }
        catch {
            # 旧安装包删除失败时交由下面的 Move-Item -Force 覆盖
            Write-Warning "旧安装包删除失败，将被覆盖：$setupPath"
        }
    }
    $ErrorActionPreference = "Continue"
    & $dotnetCommand publish $setupBuilderPath `
        -c Release `
        -p:Version=$Version `
        -p:AssemblyName="W_TB_MS-v$Version-Setup" `
        -o $setupBuilderDir
    $setupExitCode = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($setupExitCode -ne 0) { throw "安装程序编译失败。" }
}
finally {
    [IO.File]::WriteAllText($programCsPath, $programCsOriginal, [Text.UTF8Encoding]::new($false))
}

$builtSetup = Join-Path $setupBuilderDir "W_TB_MS-v$Version-Setup.exe"
if (-not (Test-Path -LiteralPath $builtSetup)) {
    throw "未找到编译产物：$builtSetup"
}
Move-Item -LiteralPath $builtSetup -Destination $setupPath -Force

# 5. 清理中间产物。清理失败不应让一次已经成功的构建判定为失败（安装包此时已生成并落到 artifacts/）。
if (Test-Path -LiteralPath $stagingRoot) {
    try {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction Stop
    }
    catch {
        Write-Warning "临时目录清理失败（不影响已生成的安装包，可手动删除）：$stagingRoot"
    }
}

# 打包成功后清理 payload 中间产物（约 145 MB，Setup.exe 已嵌入无需保留）
foreach ($payloadPath in $payloadPaths.Values) {
    if (Test-Path -LiteralPath $payloadPath) {
        try {
            Remove-Item -LiteralPath $payloadPath -Force -ErrorAction Stop
        }
        catch {
            Write-Warning "payload 清理失败（可手动删除）：$payloadPath"
        }
    }
}

Write-Host "安装包: $setupPath"

# 显式以 0 退出：清理步骤里被捕获的删除失败会污染 $LASTEXITCODE，
# 若不重置，CI 可能把一次成功的构建判定为失败。
exit 0
