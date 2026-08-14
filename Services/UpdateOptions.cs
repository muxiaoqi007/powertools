namespace PowerTools.Services;

public sealed class UpdateOptions
{
    public const string SectionName = "Updates";
    public bool Enabled { get; set; } = true;
    public string RepositoryOwner { get; set; } = "muxiaoqi007";
    public string RepositoryName { get; set; } = "powertools";
    public string ApiBaseUrl { get; set; } = "https://api.github.com";
    public string ChannelManifestName { get; set; } = "PowerTools-Update-win-x64.json";
    public string? StagingRoot { get; set; }
    public string? CurrentVersionOverride { get; set; }
    public int CacheMinutes { get; set; } = 15;
    public int MaximumDownloadMegabytes { get; set; } = 512;
}
