# 在 Power BI 中加载 PowerTools 数据

PowerTools 提供面向 Power Query 的只读 GET 接口。接口返回固定列定义和扁平行数据，不需要在 Power Query 中逐层展开完整项目快照。

## 1. 启动 PowerTools

```powershell
dotnet run --project .\PowerTools.csproj --urls http://localhost:5128
```

刷新 Power BI 查询时，PowerTools 必须保持运行。

## 2. 创建通用函数

在 Power BI Desktop 中选择“转换数据”→“新建源”→“空白查询”，打开“高级编辑器”，粘贴以下内容，并把查询命名为 `PowerToolsEntity`：

```powerquery
(PowerToolsUrl as text, ProjectPath as text, Entity as text, optional ForceRefresh as nullable logical) as table =>
let
    RefreshValue = if ForceRefresh = true then "true" else "false",
    Response =
        Json.Document(
            Web.Contents(
                PowerToolsUrl,
                [
                    RelativePath = "api/v1/powerquery/" & Entity,
                    Query = [
                        path = ProjectPath,
                        refresh = RefreshValue
                    ]
                ]
            )
        ),
    Columns = Response[columns],
    Rows = Response[rows],
    Result = Table.FromRecords(Rows, Columns, MissingField.UseNull)
in
    Result
```

仓库中也提供了可复制的 [`examples/PowerToolsEntity.pq`](../examples/PowerToolsEntity.pq)。

首次连接 `http://localhost:5128` 时，身份验证方式请选择“匿名”。

## 3. 创建参数

建议创建两个文本参数：

- `PowerToolsUrl`：`http://localhost:5128`
- `PowerBiProjectPath`：PBIP 项目根目录、`*.Report` 或 `*.SemanticModel` 目录

不要把个人本机路径硬编码进要公开共享的 PBIX 模板。

## 4. 创建模型表

例如，新建一个名为 `Measures` 的空白查询：

```powerquery
let
    Source = PowerToolsEntity(
        PowerToolsUrl,
        PowerBiProjectPath,
        "measures"
    ),
    Types = Table.TransformColumnTypes(
        Source,
        {
            {"tableName", type text},
            {"measureName", type text},
            {"expression", type text},
            {"formatString", type text},
            {"isHidden", type logical},
            {"description", type text},
            {"displayFolder", type text}
        }
    )
in
    Types
```

其他查询只需替换实体名称：

```powerquery
PowerToolsEntity(PowerToolsUrl, PowerBiProjectPath, "dependencies")
PowerToolsEntity(PowerToolsUrl, PowerBiProjectPath, "pages")
PowerToolsEntity(PowerToolsUrl, PowerBiProjectPath, "visuals")
PowerToolsEntity(PowerToolsUrl, PowerBiProjectPath, "bookmarks")
PowerToolsEntity(PowerToolsUrl, PowerBiProjectPath, "bookmark-states")
```

## 可用实体

| 实体 | 内容 |
|---|---|
| `summary` | 项目汇总指标 |
| `tables` | 模型表 |
| `columns` | 字段和计算字段 |
| `measures` | DAX 度量值 |
| `hierarchies` | 层次结构 |
| `hierarchy-levels` | 层次结构级别 |
| `partitions` | 模型分区 |
| `relationships` | 表关系 |
| `calculation-groups` | 计算组 |
| `calculation-items` | 计算项和动态格式表达式 |
| `roles` | 安全角色汇总 |
| `rls` | RLS 表筛选规则 |
| `ols` | OLS 对象权限 |
| `dependencies` | 模型对象上下游依赖 |
| `pages` | 报表页面 |
| `visuals` | 页面视觉对象和坐标 |
| `visual-fields` | 视觉对象字段绑定 |
| `report-quality` | 报表质量覆盖率、筛选器和特殊页面汇总 |
| `bookmarks` | 书签定义 |
| `bookmark-groups` | 书签组 |
| `bookmark-group-items` | 书签组成员 |
| `bookmark-targets` | 书签目标视觉对象 |
| `bookmark-states` | 书签视觉对象状态 |
| `issues` | 质量检查问题 |
| `storage-metrics` | VertiPaq 字段存储、基数和行数（实时模型） |
| `removal-candidates` | 字段与度量值删除候选和引用证据 |
| `measure-optimizations` | 度量值静态优化建议 |
| `warnings` | 解析提示 |

`pages` 额外提供 `filterCount`、`drillthroughFilterCount` 和 `isTooltip`；`visuals` 提供视觉筛选器、标题、替代文本、Tooltip、钻取交互和视觉组属性；`issues` 的 `targetId` 是可选的视觉对象定位键。

也可以访问以下地址查看实时实体目录和列名：

```text
http://localhost:5128/api/powerquery/entities
```

## 接口格式

```text
GET /api/v1/powerquery/{entity}?path={项目目录}&refresh=false
```

返回结构：

```json
{
  "entity": "measures",
  "description": "DAX 度量值",
  "projectName": "Sales",
  "projectFormat": "PBIR + TMDL",
  "scannedAt": "2026-08-13T10:00:00+08:00",
  "columns": ["tableName", "measureName", "expression"],
  "rows": [
    {
      "tableName": "Sales",
      "measureName": "Revenue",
      "expression": "SUM(Sales[Amount])"
    }
  ]
}
```

`columns` 即使在 `rows` 为空时也会存在，因此 Power Query 可以稳定创建表结构。

## 缓存与强制刷新

同一个项目的解析结果默认缓存 10 分钟。Power BI 中多张实体表刷新时会复用同一个快照，避免重复扫描项目。

需要立即重新读取磁盘文件时，可以调用：

```powerquery
PowerToolsEntity(PowerToolsUrl, PowerBiProjectPath, "measures", true)
```

建议只在一个查询中使用强制刷新，其余查询使用缓存，避免并发重复解析。

## Power BI Service 和网关

发布到 Power BI Service 后，云端无法直接访问开发电脑的 `localhost`。需要：

1. 在安装本地数据网关的 Windows 机器上运行 PowerTools。
2. 确保网关服务能够访问 PowerTools 地址和 PBIP 项目目录。
3. 在 Power BI Service 中为 Web 数据源配置网关映射，身份验证使用匿名。
4. 如果网关服务账户无法读取项目目录，为它授予只读权限。

生产环境建议将 PowerTools 作为 Windows 服务运行，并使用固定端口。不要把只监听本机的接口直接暴露到公网。

## 安全说明

- Power Query 接口只读取项目文件，不修改模型。
- 接口没有删除、覆盖或保存 PBIP 文件的能力。
- 项目路径会作为本地 HTTP 查询参数传递，建议只在受信任的机器或内网中使用。
- 如果未来开放局域网访问，应增加 API 密钥、允许目录白名单和 HTTPS。
