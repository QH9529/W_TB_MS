# GitHub 首次上传检查清单

## 仓库设置

- 将 GitHub 仓库名称从 `W_TB_jiankong` 修改为 `W_TB_MS`。
- 建议先设置为 Private。
- 将本地 `origin` 更新为新的仓库地址。
- 为 `main` 启用分支保护，禁止强制推送。
- 要求 Pull Request 通过 Windows build and release 检查后才能合并。

## 数据检查

- 确认 `4G串口.txt` 不进入提交。
- 确认曲线 LOG、Excel 和本机配置不进入提交。
- 检查截图、日志和文档中是否包含设备编号、客户信息或私有协议数据。
- 确认 `bin/`、`obj/`、本地 SDK 和发布包没有被提交。

## 首个稳定版本

- 执行 `scripts/Build-Release.ps1`。
- 验证测试全部通过。
- 从生成的 Setup.exe 完成一次实际安装。
- 使用 `scripts/Install-Version.ps1` 安装版本。
- 使用 `scripts/Restore-Version.ps1` 完成一次实际回档。
- 提交稳定基线后创建 `v1.0.0` 标签。

## 当前限制

准备阶段不得执行 `git push`、创建 GitHub Release 或修改远程仓库。完成仓库名称确认和敏感数据审查后再执行首次上传。

