namespace PowerTools;

public static class SampleProject
{
    public static ProjectSnapshot Create()
    {
        var tables = new List<ModelTable>
        {
            new("Sales", "销售事实表", false,
                new List<ModelColumn>
                {
                    new("Order Date", "dateTime", false, false, null, "订单日期"),
                    new("Product Key", "int64", true, false, null, null),
                    new("Customer Key", "int64", true, false, null, null),
                    new("Quantity", "int64", false, false, null, null),
                    new("Net Sales", "decimal", false, true, "[Quantity] * [Unit Price]", null)
                },
                new List<ModelMeasure>
                {
                    new("Revenue", "SUM(Sales[Net Sales])", "¥#,0", false, "销售收入", "核心指标"),
                    new("Revenue YoY", "DIVIDE([Revenue] - [Revenue PY], [Revenue PY])", "0.0%", false, null, "核心指标"),
                    new("Orders", "DISTINCTCOUNT(Sales[Order ID])", "#,0", false, "订单数", "核心指标")
                },
                Array.Empty<ModelHierarchy>(),
                new List<ModelPartition> { new("Sales", "import", "m", "let Source = Sql.Database(...) in Source") }),
            new("Date", "日期维度", false,
                new List<ModelColumn>
                {
                    new("Date", "dateTime", false, false, null, null),
                    new("Year", "int64", false, false, null, null),
                    new("Month", "string", false, false, null, null)
                },
                new List<ModelMeasure>(),
                new List<ModelHierarchy> { new("Calendar", new[] { "Year", "Month", "Date" }) },
                Array.Empty<ModelPartition>()),
            new("Product", "产品维度", false,
                new List<ModelColumn>
                {
                    new("Product Key", "int64", true, false, null, null),
                    new("Category", "string", false, false, null, null),
                    new("Product", "string", false, false, null, null)
                },
                Array.Empty<ModelMeasure>(), Array.Empty<ModelHierarchy>(), Array.Empty<ModelPartition>()),
            new("Customer", null, false,
                new List<ModelColumn>
                {
                    new("Customer Key", "int64", true, false, null, null),
                    new("Region", "string", false, false, null, null),
                    new("Customer", "string", false, false, null, null)
                },
                Array.Empty<ModelMeasure>(), Array.Empty<ModelHierarchy>(), Array.Empty<ModelPartition>())
        };

        var relationships = new List<ModelRelationship>
        {
            new("sales_date", "Sales", "Order Date", "Date", "Date", true, "oneDirection", "many", "one"),
            new("sales_product", "Sales", "Product Key", "Product", "Product Key", true, "oneDirection", "many", "one"),
            new("sales_customer", "Sales", "Customer Key", "Customer", "Customer Key", true, "oneDirection", "many", "one")
        };

        var overview = new ReportPage("ReportSection", "经营总览", 1280, 720, false, "FitToPage",
            new List<ReportVisual>
            {
                new("title", "经营分析驾驶舱", "textbox", 40, 24, 760, 62, 0, 0, false, Array.Empty<string>(), "sample"),
                new("revenue", "销售收入", "card", 40, 110, 270, 120, 1, 1, false, new[] { "Sales[Revenue]" }, "sample"),
                new("orders", "订单数", "card", 330, 110, 270, 120, 2, 2, false, new[] { "Sales[Orders]" }, "sample"),
                new("growth", "同比增长", "card", 620, 110, 270, 120, 3, 3, false, new[] { "Sales[Revenue YoY]" }, "sample"),
                new("period", "日期", "slicer", 920, 110, 320, 120, 4, 4, false, new[] { "Date[Date]" }, "sample"),
                new("trend", "收入趋势", "lineChart", 40, 260, 760, 400, 5, 5, false, new[] { "Date[Month]", "Sales[Revenue]" }, "sample"),
                new("category", "品类贡献", "donutChart", 830, 260, 410, 400, 6, 6, false, new[] { "Product[Category]", "Sales[Revenue]" }, "sample")
            });
        var detail = new ReportPage("ReportSectionDetail", "产品明细", 1280, 720, false, "FitToPage",
            new List<ReportVisual>
            {
                new("title2", "产品销售明细", "textbox", 40, 24, 800, 62, 0, 0, false, Array.Empty<string>(), "sample"),
                new("categoryFilter", "品类", "slicer", 40, 110, 280, 550, 1, 1, false, new[] { "Product[Category]" }, "sample"),
                new("productTable", "产品表现", "tableEx", 350, 110, 890, 550, 2, 2, false, new[] { "Product[Product]", "Sales[Revenue]", "Sales[Revenue YoY]" }, "sample")
            });

        var issues = new List<QualityIssue>
        {
            new("MODEL-001", "warning", "Model", "度量值缺少说明", "Revenue YoY 没有 description，维护者难以理解指标口径。", "Sales[Revenue YoY]"),
            new("REPORT-001", "info", "Report", "页面缺少导航", "产品明细页没有检测到页面导航器或返回按钮。", "产品明细", "ReportSectionDetail")
        };
        var calculationGroups = new[]
        {
            new CalculationGroup("时间智能", 10, true, new[]
            {
                new CalculationItem("本年累计", "CALCULATE(SELECTEDMEASURE(), DATESYTD('Date'[Date]))", null, 0, "本年累计"),
                new CalculationItem("上年同期", "CALCULATE(SELECTEDMEASURE(), SAMEPERIODLASTYEAR('Date'[Date]))", "SELECTEDMEASUREFORMATSTRING()", 1, null)
            })
        };
        var roles = new[]
        {
            new SecurityRole("区域经理", "read", new[] { new TablePermission("Customer", "Customer[Region] = USERPRINCIPALNAME()") }, Array.Empty<ObjectPermission>())
        };
        var dependencies = new[]
        {
            new ModelDependency("measure:Sales:Revenue YoY", "Sales[Revenue YoY]", "measure", "measure:Sales:Revenue", "Sales[Revenue]", "measure", "[Revenue]"),
            new ModelDependency("measure:Sales:Revenue", "Sales[Revenue]", "measure", "column:Sales:Net Sales", "Sales[Net Sales]", "column", "Sales[Net Sales]"),
            new ModelDependency("measure:Sales:Orders", "Sales[Orders]", "measure", "column:Sales:Order ID", "Sales[Order ID]", "column", "Sales[Order ID]"),
            new ModelDependency("calcitem:时间智能:本年累计", "时间智能[本年累计]", "calculationItem", "column:Date:Date", "Date[Date]", "column", "'Date'[Date]")
        };
        var bookmarks = new[]
        {
            new ReportBookmark("sample-overview", "经营总览", overview.Name, true, false,
                new[] { "revenue", "orders", "growth", "period", "trend", "category" },
                new[]
                {
                    new BookmarkVisualState(overview.Name, "trend", false, "lineChart", 1),
                    new BookmarkVisualState(overview.Name, "category", true, "donutChart", 0)
                }, 0, 1, true, "sample")
        };
        var bookmarkGroups = new[] { new BookmarkGroup("sample-group", "演示书签", new[] { "sample-overview" }, 0) };
        return new ProjectSnapshot("零售经营分析（示例）", "内置演示数据", "PBIP / PBIR", DateTimeOffset.Now, tables, relationships, calculationGroups, roles, dependencies, bookmarks, bookmarkGroups, new[] { overview, detail }, issues, Array.Empty<string>());
    }
}
