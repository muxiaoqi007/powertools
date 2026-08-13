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

后端只读取本机文件并返回内存快照。前端不保存项目数据，API 也没有写入或删除端点。

## 目录职责

- `Program.cs`：应用启动、静态文件和本地解析 API。
- `Services/PowerBiProjectParser.cs`：PBIP/PBIR/TMDL/model.bim 解析器。
- `Services/ProjectSnapshotCache.cs`：为 Power Query 多实体刷新复用项目快照。
- `Services/PowerQueryExportService.cs`：将嵌套快照映射为固定列的扁平实体。
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
- `page.json`：页面名称、尺寸、显示状态
- `visual.json`：视觉对象类型、位置、尺寸、层级和字段绑定

书签来自 `definition/bookmarks`：

- `bookmarks.json`：书签顺序和分组
- `*.bookmark.json`：活动页面、目标视觉对象、筛选器、数据状态及视觉对象组显隐

解析器允许尾随逗号和注释。单个书签不是严格 JSON 时，会以容错模式提取名称、页面、选项和目标视觉对象，并在快照中生成提示。

## 书签与布局关联

书签通过 `activeSection` 关联 PBIR 页面，通过视觉对象名称关联 `visual.json`。只有页面标识匹配时才允许预览，防止把旧书签状态应用到当前无关页面。

## 数据安全

- 不连接云端 Power BI 服务。
- 不上传 PBIP/PBIR/TMDL 文件。
- 不修改或删除源文件。
- 本机路径仅用于当前请求，不写入仓库或配置。
- 发布目录、构建缓存和测试数据默认被 Git 忽略。

## Power Query API

`GET /api/powerquery/{entity}` 接收项目路径并返回 `columns` 与 `rows`。列定义独立于数据行，即使实体为空也能保持稳定结构。同一规范化项目路径的快照缓存 10 分钟；`refresh=true` 可强制重新扫描。
