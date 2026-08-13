# 模型优化规则与安全边界

PowerTools 的模型优化模块是只读静态分析器。它不连接执行引擎、不采集生产查询日志，也不直接修改 TMDL。

## 删除候选的含义

每个字段和度量值都会得到以下状态之一：

- `blocked`：当前 PBIP 项目内检测到明确引用，不能删除。
- `candidate`：当前项目内未检测到引用，仅作为人工审查候选。

阻断证据包括：

- 度量值、计算字段和计算项 DAX 依赖
- PBIR 视觉对象字段绑定
- 表关系的起止字段
- 层次结构级别
- RLS 表筛选表达式
- OLS 对象权限

静态项目分析无法完整发现：

- 连接同一语义模型的其他 PBIX thin report
- Excel Analyze in Excel 和其他 XMLA 客户端
- 外部 DAX 查询与分页报表
- 动态字符串或元数据驱动引用
- 当前解析范围之外的 SortByColumn、字段参数或特殊注解
- 尚未保存进 PBIP 的 Power BI Desktop 修改

因此，`candidate` 不等于“保证可以删除”。建议流程是：创建 Git 分支、手工移除对象、打开 Power BI Desktop 验证、刷新模型、运行关键报表和外部消费者测试，然后通过版本比较审查变更。

## 度量优化规则

| 规则 | 检查内容 | 依据 |
|---|---|---|
| `DAX-001` | 长复杂表达式未使用变量 | Microsoft DAX variables |
| `DAX-002` | 分母可能为零或 BLANK 时使用 `/` | Microsoft DIVIDE guidance |
| `DAX-003` | CALCULATE/CALCULATETABLE 中使用 FILTER | Microsoft Power BI guidance |
| `DAX-004` | 可评估 COUNTROWS 替代 COUNT | Microsoft Power BI guidance |
| `DAX-005` | HASONEVALUE/VALUES 可评估 SELECTEDVALUE | Microsoft Power BI guidance |
| `DAX-006` | 嵌套迭代器 | SQLBI Optimizing DAX |
| `DAX-007` | 同一度量值重复引用且没有 VAR | Microsoft DAX variables |
| `DAX-008` | SWITCH 与变量可能影响分支裁剪 | SQLBI SWITCH optimization |
| `DAX-009` | 度量值引用带 Home Table | Microsoft reference guidance |

此外还检查业务说明和格式字符串。

## 如何理解优先级

优先级用于排列值得先检查的表达式，不代表预计加速百分比。真正的性能结论需要结合：

- Power BI Performance Analyzer
- DAX Studio Server Timings
- 查询计划与 xmSQL
- VPAX / VertiPaq Analyzer 模型统计
- 实际筛选上下文与数据分布

PowerTools 只实现 Microsoft 和 SQLBI 公开方法论对应的可解释规则，没有复制 DAX Optimizer 的专有规则库。

## 参考资料

- [DAX Optimizer documentation](https://docs.daxoptimizer.com/)
- [SQLBI DAX Internals optimization notes](https://docs.sqlbi.com/dax-internals/optimization-notes/)
- [SQLBI SWITCH optimization](https://docs.sqlbi.com/dax-internals/optimization-notes/switch-optimization)
- [Microsoft Power BI guidance](https://learn.microsoft.com/power-bi/guidance/)
- [Microsoft: Use variables](https://learn.microsoft.com/dax/best-practices/dax-variables)
- [Microsoft: Column and measure references](https://learn.microsoft.com/dax/best-practices/dax-column-measure-references)
