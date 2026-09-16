# W_TB_MS

W_TB_MS 是用于 W 系列热泵设备的 Windows Modbus RTU 监控工具。

## 主要功能

- 串口连接、自动轮询和通信日志
- 寄存器参数查看与写入
- 参数、故障和运行状态曲线
- 24 小时曲线保留及历史文件查看
- 每 6 小时自动生成 LOG 和 Excel 存档
- 关闭前未存档数据提醒

## 开发环境

- Windows 10/11
- .NET 8 SDK
- Visual Studio 2022 或 `dotnet` CLI
- Git for Windows

## 本地验证

```powershell
dotnet restore W_TB_MS.sln
dotnet build W_TB_MS.sln -c Release --no-restore
dotnet test W_TB_MS.Tests/W_TB_jiankong.Tests.csproj -c Release --no-build
```

## 生成稳定软件包

```powershell
./scripts/Build-Release.ps1
```

输出位于 `artifacts/`，包含版本 ZIP 和 SHA256 校验文件。发布包支持 `win-x86` 和 `win-x64` 自包含文件夹，不依赖目标电脑预装 .NET。按指定架构生成示例：

```powershell
./scripts/Build-Release.ps1 -Runtime win-x86
./scripts/Build-Release.ps1 -Runtime win-x64
```

安装器使用 x86 引导程序，可在两种 Windows 系统上运行，并在 64 位系统自动安装 x64 程序。

## 安装和回档

安装版本包：

```powershell
./scripts/Install-Version.ps1 -PackagePath ./artifacts/W_TB_MS-v1.0.6-win-x64.zip -Start
```

查看本地可回档版本：

```powershell
./scripts/Restore-Version.ps1
```

恢复指定版本：

```powershell
./scripts/Restore-Version.ps1 -Version <版本目录名> -Start
```

回档脚本独立于主程序运行，因此主程序无法启动时仍可恢复。存档数据和用户配置位于程序目录之外，不会随程序回档而删除。

## GitHub 发布流程

`main` 仅用于稳定版本。功能修改应从独立分支提交，并通过 Pull Request 合并。推送 `v*` 标签后，GitHub Actions 会执行测试、生成 Windows 软件包并创建 GitHub Release。

上传前必须检查通信抓包、LOG、Excel 和其他设备数据是否已被排除。
