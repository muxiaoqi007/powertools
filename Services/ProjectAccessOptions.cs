namespace PowerTools.Services;

public sealed class ProjectAccessOptions
{
    public const string SectionName = "ProjectAccess";
    public string[] AllowedRoots { get; set; } = Array.Empty<string>();
}
