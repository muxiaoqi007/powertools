# PowerTools

本地运行的 Power BI 模型管理与报表页面布局检查工具。产品思路参考 PowerOps 的分析、模型、关系、依赖与最佳实践模块，并增加了按真实坐标还原 PBIR 页面布局的画布。

## 当前功能

- 打开 PBIP 项目根目录、`*.Report` 或 `*.SemanticModel` 目录
- 解析 PBIR 页面和 `visual.json`，按页面原始尺寸、X/Y、宽高和 Z 层级绘制画布
- 查看视觉对象类型、字段绑定、隐藏状态、位置、尺寸和 Tab 顺序
- 解析 TMDL / `model.bim` 中的表、字段、度量值、层次结构、分区和关系
- 完整解析单行、多行及三反引号围栏形式的 DAX 度量值表达式
- 计算组目录：优先级、计算项表达式、动态格式字符串和空表达式检查
- RLS / OLS 安全中心：角色、模型权限、表筛选和对象权限
- DAX 度量值全文搜索、表达式查看及引用对象提取
- 模型分组导航：模型对象、度量值、计算组和模型依赖统一收纳在模型模块
- 交互式模型依赖图：度量值到度量值、度量值到字段、计算项到模型对象；支持上下游、展开层级、搜索、拖动和缩放
- PBIR 书签管理：识别书签顺序、分组、活动页面、目标视觉对象、筛选器和视觉对象显隐状态
- 书签与页面布局联动：在画布中切换书签，直观看到视觉对象的显示和隐藏位置
- 孤立书签检查：识别书签引用的页面或视觉对象已不在当前 PBIR 定义中的情况
- 报表质量中心：按页面与类别检查标题、替代文本、Tab 顺序、视觉密度、网格对齐、越界、重叠、Tooltip 和钻取返回入口
- 报表质量问题可一键定位到页面画布及具体视觉对象；书签控制的叠放层会自动排除，降低误报
- 解析报表级、页面级和视觉级筛选器，并汇总标题与替代文本覆盖率
- 项目版本比较：识别模型对象、DAX、关系、安全、依赖、页面、视觉对象和书签的新增、删除与修改
- 模型优化：字段/度量删除候选的证据和风险分级，以及基于 Microsoft 与 SQLBI 公开最佳实践的 DAX 静态优化建议
- Power BI Desktop 实时只读连接：通过外部工具参数和 TOM 直接读取当前打开模型
- VertiPaq 存储分析：字段行数、基数、数据/字典占用、总占用及候选回收空间
- 模型对象搜索、关系清单、项目指标总览
- 模型与报表最佳实践检查：缺少度量值说明、非活动关系、可访问性、导航、筛选器和页面布局等
- 完全本地分析，不上传报表内容
- Power Query 多实体并发刷新缓存、文件变更自动失效和可配置目录白名单

## 运行

推荐运行 `PowerTools-Setup-0.9.0-win-x64.exe` 完成标准安装。安装程序会安装到 `C:\Program Files\PowerTools`、创建开始菜单入口并自动注册 Power BI 外部工具；卸载时同步清理注册。详见[安装程序文档](docs/INSTALLER.md)。

不希望安装时，也可以使用桌面便携包中的 `PowerTools.Desktop.exe`，它会自动打开独立窗口并隐藏后台服务。详见[桌面版文档](docs/DESKTOP.md)。

分析当前打开的 PBIX：发布后双击桌面包中的 `install-external-tool.cmd`（或运行 `.\scripts\register-external-tool.ps1`），重启 Power BI Desktop 后从“外部工具”功能区点击 PowerTools。

开发或 Power Query 固定端口模式：

```powershell
dotnet run --project .\PowerTools.csproj
```

浏览器打开控制台显示的本地地址（通常为 `http://localhost:5000`）。首次进入会显示内置示例，随后可在顶部输入本机 Power BI 项目目录。

也可双击：

```text
start-powertools.cmd
```

## 文档

- [使用指南](docs/USER_GUIDE.md)
- [Power Query 接入指南](docs/POWER_QUERY.md)
- [报表质量规则](docs/REPORT_QUALITY.md)
- [模型优化规则与安全边界](docs/MODEL_OPTIMIZER.md)
- [桌面版启动与发布](docs/DESKTOP.md)
- [Windows 安装程序](docs/INSTALLER.md)
- [架构与解析范围](docs/ARCHITECTURE.md)
- [版本记录](CHANGELOG.md)

## 安全与支持范围

PowerTools 只读项目文件或 Power BI Desktop 本机 Analysis Services 模型，并在内存中生成分析快照，不会调用 TOM `SaveChanges`、删除或上传内容。PBIX 可通过 Power BI“外部工具”直接分析模型；页面布局与书签仍需 PBIP/PBIR 项目目录。

## 构建发布

```powershell
dotnet build
.\scripts\build-installer.ps1
```

标准安装包输出到 `artifacts\installer`。便携包可使用 `.\scripts\publish-desktop.ps1` 单独生成。`artifacts/`、编译产物和本机测试数据已加入 `.gitignore`，不会提交到仓库。
