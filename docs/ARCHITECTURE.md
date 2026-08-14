# 架构与解析范围

## 总体结构

PowerTools 是一个 .NET 8 本地 Web 应用：

```text
浏览器界面
    ↓ HTTP/JSON
ASP.NET Core Minimal API
    ↓
PowerBiProjectParser
    ├─ PBIR 报表解析
    ├─ TMDL / model.bim 模型解析
    ├─ DAX 引用与依赖提取
    └─ 质量规则分析
```

默认分析链路只读取本机文件并返回内存快照。P2 的安全修改链路有独立端点，只允许把经过风险门禁的变更写入 PowerTools 管理的隔离工作区；源项目和实时 PBIX 始终只读。

Windows 桌面版由 WPF + WebView2 承载同一前端，启动一个隐藏的 ASP.NET Core 子进程。桌面窗口和服务生命周期绑定，并使用随机回环端口。

## 目录职责

- `Program.cs`：应用启动、静态文件和本地解析 API。
- `Services/PowerBiProjectParser.cs`：PBIP/PBIR/TMDL/model.bim 解析器。
- `Services/LivePowerBiModelService.cs`：使用 TOM 读取当前 Power BI Desktop 模型，并以 DMV 采集 VertiPaq 存储统计；失败时降级为纯元数据快照。
- `Services/ProjectSnapshotCache.cs`：为 Power Query 多实体刷新复用项目快照。
- `Services/PowerQueryExportService.cs`：将嵌套快照映射为固定列的扁平实体。
- `Services/SnapshotComparisonService.cs`：按稳定对象键比较两个项目快照。
- `Services/ProjectPathPolicy.cs`：限制允许读取的项目根目录。
- `Services/SafeChangeService.cs`：修改计划、源指纹、隔离复制、备份、原子写入、审计和回滚。
- `Models/ProjectSnapshot.cs`：统一的模型、报表、书签和质量检查数据结构。
- `wwwroot/`：单页管理界面、依赖图、书签管理器和布局画布。
- `docs/`：使用与架构文档。

## 模型解析

模型解析优先读取 SemanticModel `definition` 目录中的 TMDL 文件；如果不存在，则尝试 `model.bim`。支持：

- 表、字段、计算字段、度量值、层次结构和分区
- 表关系
- 计算组和计算项
- RLS 表权限与 OLS 对象权限
- DAX 表/字段/度量值引用及上下游依赖

## PBIR 报表解析

页面来自 `definition/pages`：

- `pages.json`：页面顺序
- `report.json`：报表级筛选器
- `page.json`：页面名称、尺寸、显示状态、页面/钻取筛选器和 Tooltip 属性
- `visual.json`：视觉对象类型、位置、尺寸、层级、字段绑定、视觉筛选器、标题、替代文本和交互属性

书签来自 `definition/bookmarks`：

- `bookmarks.json`：书签顺序和分组
- `*.bookmark.json`：活动页面、目标视觉对象、筛选器、数据状态及视觉对象组显隐

解析器允许尾随逗号和注释。单个书签不是严格 JSON 时，会以容错模式提取名称、页面、选项和目标视觉对象，并在快照中生成提示。

## 书签与布局关联

书签通过 `activeSection` 关联 PBIR 页面，通过视觉对象名称关联 `visual.json`。只有页面标识匹配时才允许预览，防止把旧书签状态应用到当前无关页面。

## 报表质量分析

质量分析在统一快照生成后执行，并把每个问题关联到页面标识和可选的视觉对象标识。前端可据此从质量列表直接定位到布局画布。布局规则会排除隐藏对象、装饰对象、视觉组和书签明确管理的动态叠放层，以控制静态检查误报。

质量结果只描述证据和建议，不包含自动修改 PBIR 的写入流程。完整规则和边界见 [报表质量规则](REPORT_QUALITY.md)。

## 数据安全

- 不连接云端 Power BI 服务。
- 不上传 PBIP/PBIR/TMDL 文件。
- 不修改或删除源文件。
- 安全修改只写受控隔离副本，不直接删除模型对象。
- 本机路径仅用于当前请求，不写入仓库或配置。
- 发布目录、构建缓存和测试数据默认被 Git 忽略。

## Power Query API

`GET /api/powerquery/{entity}` 接收项目路径并返回 `columns` 与 `rows`。列定义独立于数据行，即使实体为空也能保持稳定结构。同一规范化项目路径的快照缓存 10 分钟；`refresh=true` 可强制重新扫描。

从 0.6.0 起同时提供 `/api/v1` 版本化接口，旧 `/api` 路径保持兼容。

## 可用性保护

- 解析前等待项目文件短暂稳定，减少读取 PBIP 半保存状态。
- 新解析失败时返回上一次成功快照并附加警告。
- 同一项目首次并发请求合并为一次解析。
- 全局固定窗口限流与响应压缩。
- JSON 结构化日志；桌面壳收集到本地日志文件。
- 存活、就绪和缓存诊断接口。
- 修改计划持久化、每计划互斥锁、确认短语、源指纹漂移检查和逐文件回滚。

## 安全修改事务边界

`POST /api/v1/changes/plan` 会重新解析项目并只接受 `candidate` 字段或度量值。计划保存对象证据、相对 TMDL 路径和项目内容指纹。`apply` 在确认短语匹配且指纹未变化时，才把项目复制到受控工作区，备份目标文件并以同目录临时文件 + 原子替换写入 `isHidden`。`rollback` 只允许恢复位于受控工作区内、且属于该计划的备份文件。

该链路不删除工作区、备份或审计文件，失败时也不会触碰源项目。详细用户流程见[安全修改与回滚](SAFE_CHANGES.md)。

首次并发请求通过 `Lazy<Task<ProjectSnapshot>>` 合并为一次扫描。缓存同时记录项目文件数量和最新修改时间，检测到磁盘变化会自动重新解析。

## 目录白名单

`appsettings.json` 中 `ProjectAccess:AllowedRoots` 为空时保持本机开发兼容；部署到网关或局域网时应配置允许读取的根目录：

```json
{
  "ProjectAccess": {
    "AllowedRoots": ["D:\\PowerBIProjects"]
  }
}
```

项目路径必须等于白名单根目录或位于其子目录中，越界请求返回 HTTP 403。

## 版本比较

`POST /api/project/compare` 并发解析基线和当前项目，按表、字段、度量值、关系、计算组/项、角色、依赖、页面、视觉对象和书签的稳定键比较。对象内容经过 SHA-256 摘要识别修改，接口只返回变更清单，不写入任何项目。
