# Windows 安装程序

PowerTools 0.9.0 提供标准 x64 Windows 安装程序：

```text
PowerTools-Setup-0.9.0-win-x64.exe
```

## 安装行为

安装程序需要管理员权限，并执行以下操作：

- 将完整自包含桌面程序安装到 `C:\Program Files\PowerTools`
- 创建开始菜单快捷方式
- 可选创建桌面快捷方式
- 注册 Windows App Paths
- 在 Power BI Desktop 的 `External Tools` 目录生成 `PowerTools.pbitool.json`
- 将外部工具启动路径固定为安装目录中的 `PowerTools.Desktop.exe`

安装完成后重启 Power BI Desktop。打开任意 PBIX，在“外部工具”功能区点击 PowerTools，即可把当前本机模型的服务器和数据库参数传给 PowerTools。

安装包默认自包含 .NET 8 Desktop 和 ASP.NET Core 运行时，目标电脑不需要另行安装 .NET。Windows 仍需具备 Microsoft Edge WebView2 Runtime；Windows 10/11 与 Power BI Desktop 通常已经安装该运行时。

当前本地构建产物尚未使用商业代码签名证书签名，因此 Windows SmartScreen 可能显示“未知发布者”。正式对外发布时应使用受信任的 Authenticode 证书签名 Setup EXE。

## 升级与卸载

新版安装包使用固定应用标识，会覆盖升级现有安装。可以从 Windows“设置 → 应用 → 已安装的应用”卸载 PowerTools。

卸载程序会删除：

- `C:\Program Files\PowerTools` 下的安装文件
- 开始菜单和桌面快捷方式
- App Paths 注册
- Power BI Desktop 的 `PowerTools.pbitool.json` 外部工具清单

用户日志位于 `%LOCALAPPDATA%\PowerTools\Logs`，卸载时保留，便于故障追踪。

## 构建安装包

开发电脑需要 Inno Setup 6：

```powershell
winget install --id JRSoftware.InnoSetup --exact
.\scripts\build-installer.ps1 -Version 0.9.0
```

输出位置：

```text
artifacts\installer\PowerTools-Setup-0.9.0-win-x64.exe
```

构建默认执行自包含发布。若只需要体积更小、依赖目标电脑 .NET 8 的安装包，可使用：

```powershell
.\scripts\build-installer.ps1 -Version 0.9.0 -FrameworkDependent
```

已经生成便携包、只需重新编译安装器时使用 `-SkipPublish`。

## 自动验证

在管理员 PowerShell 中运行：

```powershell
.\scripts\test-installer.ps1
```

脚本会静默安装程序，验证安装文件、Power BI 外部工具清单和 `/health/live`，随后静默卸载并确认清理完成。测试前存在的 PowerTools 外部工具清单会在测试结束后恢复。

## 静默部署

企业软件分发可使用 Inno Setup 标准参数：

```powershell
PowerTools-Setup-0.9.0-win-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-
```

静默部署同样会自动注册 Power BI 外部工具。
