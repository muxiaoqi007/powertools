# PowerTools 桌面版

## 标准安装

普通用户推荐运行 `PowerTools-Setup-0.11.0-win-x64.exe`，不需要手动复制整个便携目录。安装程序会自动注册 Power BI 外部工具，并内置 GitHub 增量更新器，详细说明见 [Windows 安装程序](INSTALLER.md)和[GitHub 更新](UPDATES.md)。

## 推荐启动方式

使用桌面发布包根目录中的 `PowerTools.Desktop.exe`。不要直接双击 `server/PowerTools.exe`，后者是 Power Query、浏览器模式和后台服务使用的引擎。

桌面入口会：

- 启动隐藏的本地分析服务
- 自动选择空闲的 `127.0.0.1` 端口
- 等待 `/health/ready` 就绪
- 在 WebView2 独立窗口内显示界面
- 防止重复启动多个桌面实例
- 关闭窗口时同步关闭后台服务
- 将后端 JSON 日志写入 `%LOCALAPPDATA%\PowerTools\Logs`

## 环境要求

- Windows 10/11 x64
- Microsoft Edge WebView2 Runtime
- 框架依赖包需要 .NET 8 Desktop Runtime 和 ASP.NET Core Runtime

如需目标电脑无需安装 .NET，可构建自包含包：

```powershell
.\scripts\publish-desktop.ps1 -SelfContained
```

## 构建桌面包

```powershell
.\scripts\publish-desktop.ps1
```

输出目录：

```text
artifacts/desktop-win-x64/
├─ PowerTools.Desktop.exe
└─ server/
   └─ PowerTools.exe
```

桌面包必须整体复制，不能只复制根目录的 EXE。

## 注册为 Power BI 外部工具

标准安装程序会自动完成注册。只有使用便携包时，才需要双击桌面发布包中的 `install-external-tool.cmd`，确认 Windows 管理员提示；也可以在管理员 PowerShell 中运行 `.\scripts\register-external-tool.ps1`。重启 Power BI Desktop，打开 PBIX 后在“外部工具”功能区点击 PowerTools，程序会以只读方式加载当前模型。

使用 `.\scripts\register-external-tool.ps1 -Unregister` 取消注册。注册文件保存了桌面 EXE 的绝对路径，移动发布目录后需重新注册。实时连接只接受 `localhost`、`127.0.0.1` 或 `::1`。

## 浏览器和 Power Query 模式

需要固定端口供 Power Query 使用时，直接运行服务引擎：

```powershell
.\server\PowerTools.exe --urls http://127.0.0.1:5128
```

桌面入口使用随机端口，适合交互分析；Power Query 建议用固定端口后台服务。

## 诊断

- 存活检查：`/health/live`
- 就绪检查：`/health/ready`
- 诊断信息：`/api/v1/diagnostics`
- 日志目录：`%LOCALAPPDATA%\PowerTools\Logs`

接口同时保留旧 `/api/...` 路径，并提供稳定的 `/api/v1/...` 路径。
