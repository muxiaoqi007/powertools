# 安全修改与回滚

P2 引入了安全修改工作区。它不是“自动删除器”，而是把静态分析得到的候选对象先隔离到一个独立 PBIP/TMDL 副本中，供 Power BI Desktop 验证。

## 当前支持范围

- 仅支持磁盘上的 PBIP/TMDL 项目。
- 支持对状态为 `candidate` 的字段和度量值执行“隔离隐藏”：在副本 TMDL 对象块中增加 `isHidden`。
- `blocked` 对象、实时 PBIX/TOM 模型和 `model.bim` 始终拒绝写入。
- 不删除任何字段、度量值或源文件；也不调用 TOM `SaveChanges`。

## 操作流程

1. 打开 PBIP/TMDL 项目，在“模型 → 安全修改”选择候选对象。
2. 生成计划。服务会再次解析项目、复核候选状态、定位 TMDL 文件并计算源项目 SHA-256 指纹。
3. 核对风险、文件和操作清单，输入界面显示的完整 `APPLY <计划编号>`。
4. PowerTools 将项目复制到 `%LOCALAPPDATA%\PowerTools\Workspaces`，先建立逐文件备份，再原子写入副本。
5. 用 Power BI Desktop 打开副本，验证刷新、视觉对象、书签、RLS、计算组和下游消费者。
6. 如需撤销，在同一计划中输入 `ROLLBACK <计划编号>`。PowerTools 会从备份恢复副本；工作区和审计记录继续保留。

源项目在整个流程中不会被写入。若源项目在生成计划后有任何文件变化，应用阶段会因指纹不一致而中止，必须重新扫描并生成计划。

## 文件与审计

默认目录：

```text
%LOCALAPPDATA%\PowerTools\ChangePlans\<plan-id>.json
%LOCALAPPDATA%\PowerTools\Workspaces\<project>-<time>-<id>\
  └─ .powertools\
       ├─ audit.json
       └─ backups\<plan-id>\...
```

计划记录源路径、源指纹、风险证据、目标文件、状态转换、工作区路径和每次成功或失败事件。应用和回滚都按计划编号加锁，避免同一计划并发执行。

可以在 `appsettings.json` 中配置受控目录和单计划上限：

```json
{
  "SafeChanges": {
    "WorkspaceRoot": "D:\\PowerToolsWorkspaces",
    "PlanRoot": "D:\\PowerToolsPlans",
    "MaxOperations": 100
  }
}
```

不要把 `WorkspaceRoot` 或 `PlanRoot` 配置到待分析项目内部，否则项目指纹会纳入这些文件，造成不必要的漂移。

## API

- `POST /api/v1/changes/plan`：生成不可直接执行的预览计划。
- `POST /api/v1/changes/apply`：校验确认短语和源指纹后，在隔离副本应用。
- `POST /api/v1/changes/rollback`：从计划备份恢复隔离副本。
- `GET /api/v1/changes/{planId}`：读取持久化计划和审计状态。

所有端点也保留 `/api` 兼容前缀。目录读取仍受 `ProjectAccess:AllowedRoots` 控制。

## 验证清单

- Power BI Desktop 能正常打开隔离副本。
- 所有分区刷新成功，关键度量值结果一致。
- 页面、书签、Tooltip、钻取和字段参数正常。
- RLS / OLS 角色测试通过。
- Thin report、Excel/XMLA、分页报表和外部 DAX 查询未引用候选对象。
- 使用“版本比较”审查副本与源项目的预期差异。

只有完成上述验证后，才应由开发者在 Git 分支中人工决定是否永久移除对象。PowerTools 当前不会执行永久删除。
