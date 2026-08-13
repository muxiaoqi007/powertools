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
- 模型对象搜索、关系清单、项目指标总览
- 轻量最佳实践检查：缺少度量值说明、非活动关系、对象越界、大面积重叠等
- 完全本地分析，不上传报表内容

## 运行

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
- [架构与解析范围](docs/ARCHITECTURE.md)
- [版本记录](CHANGELOG.md)

## 安全与支持范围

PowerTools 当前只读取项目文件并在内存中生成分析快照，不会修改、删除或上传 Power BI 项目内容。当前版本优先支持 Power BI Project（PBIP）、Enhanced Report Format（PBIR）、TMDL 与 `model.bim`。经典二进制 `.pbix/.pbit` 并非普通 ZIP，建议先由 Power BI Desktop 另存为 PBIP 项目，再加载其目录。

## 构建发布

```powershell
dotnet build
dotnet publish -c Release -o publish
```

发布目录中的 `PowerTools.exe` 可直接启动。`publish/`、编译产物和本机测试数据已加入 `.gitignore`，不会提交到仓库。
