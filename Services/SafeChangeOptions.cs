namespace PowerTools.Services;

public sealed class SafeChangeOptions
{
    public const string SectionName = "SafeChanges";
    public string? WorkspaceRoot { get; set; }
    public string? PlanRoot { get; set; }
    public int MaxOperations { get; set; } = 100;
}
