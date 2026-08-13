// Power Query 通用函数
// 在 Power BI 中新建空白查询，打开高级编辑器，粘贴此文件内容，
// 并将查询命名为 PowerToolsEntity。

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
