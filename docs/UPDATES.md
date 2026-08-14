# GitHub 更新与增量升级

PowerTools 0.11.0 起在桌面窗口顶部提供“检查更新”。应用启动后也会在后台检查一次；失败不会影响模型分析。

0.10.x 及更早版本本身没有更新器，因此第一次升级到 0.11.0 仍需运行完整安装包。安装 0.11.0 后，后续版本才可直接在软件内使用增量更新。

## 更新选择

更新服务比较当前程序集版本和 GitHub 最新 Release：

1. 优先读取不消耗 GitHub REST API 配额的 `PowerTools-Update-win-x64.json` 通道清单。
2. 通道清单不存在时，尝试 GitHub Releases REST API。
3. REST API 被限流时，通过 `/releases/latest` 重定向识别最新版本，至少保留手工更新提示。
4. Release 中存在精确的 `当前版本 → 最新版本` 增量包时使用增量更新。
5. 没有匹配基线时自动回退到完整 Setup；不会跨版本错误套用增量包。

资产命名约定：

```text
PowerTools-Update-win-x64.json
PowerTools-Delta-<from>-to-<to>-win-x64.zip
PowerTools-Setup-<version>-win-x64.exe
PowerTools-Desktop-<version>-win-x64.zip
```

## 安全边界

- 下载地址由服务根据受信任的 GitHub Release 元数据选择，前端不能传入任意 URL。
- 只接受 `https://github.com` Release 资产。
- 下载限制文件大小，并核对资产长度和 SHA-256。
- 增量 ZIP 内的每个文件再次按清单校验 SHA-256，拒绝绝对路径、`..`、清单外 payload 和超大解压内容。
- 更新器复制到暂存目录后独立运行，等待桌面和本地服务退出，再修改安装目录。
- Windows 安装在 `Program Files` 时会显示管理员授权提示。
- 被覆盖或移除的文件保存在 `%LOCALAPPDATA%\PowerTools\UpdateBackups`。应用中途失败会按文件恢复。
- 下载暂存位于 `%LOCALAPPDATA%\PowerTools\Updates`，更新日志位于 `%LOCALAPPDATA%\PowerTools\UpdateLogs`。

增量更新只修改 PowerTools 安装目录，不会触碰 PBIX、PBIP、TMDL、工作区、更新备份或用户报表数据。

## 发布增量包

标准发布命令：

```powershell
.\scripts\build-release-assets.ps1 -Version 0.12.0 `
  -BaseVersion 0.11.0 `
  -BaselineDirectory .\artifacts\baseline-0.11.0
```

脚本会生成：

- 完整安装程序
- 当前版本便携基线 ZIP
- 相对上一版本的增量 ZIP
- 更新通道 JSON

`build-update-package.ps1` 对基线和当前便携目录逐文件计算 SHA-256，只把新增或变化文件放入 `payload`，并把不再存在的路径写入 `removedFiles`。后续 Release 必须保留便携基线资产，否则下个版本只能回退到完整安装包。

仓库包含手工触发的 GitHub Actions `Release` 工作流。输入 `version` 和可选 `base_version` 后，工作流会下载上一版本便携包、构建增量资产并创建 Release。首次启用或基线资产缺失时留空 `base_version`。

## 配置

```json
{
  "Updates": {
    "Enabled": true,
    "RepositoryOwner": "muxiaoqi007",
    "RepositoryName": "powertools",
    "ApiBaseUrl": "https://api.github.com",
    "ChannelManifestName": "PowerTools-Update-win-x64.json",
    "CacheMinutes": 15,
    "MaximumDownloadMegabytes": 512
  }
}
```

部署在封闭网络时可将 `Enabled` 设为 `false`。GitHub Release REST 响应中的资产 `digest` 用法见 [GitHub Release assets 文档](https://docs.github.com/en/rest/releases/assets)。
