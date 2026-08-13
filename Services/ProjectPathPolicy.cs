using Microsoft.Extensions.Options;

namespace PowerTools.Services;

public sealed class ProjectPathPolicy(IOptions<ProjectAccessOptions> options)
{
    private readonly string[] _allowedRoots = options.Value.AllowedRoots
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(Normalize)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public string Resolve(string inputPath)
    {
        var path = Normalize(inputPath);
        if (_allowedRoots.Length == 0 || _allowedRoots.Any(root => IsWithin(path, root))) return path;
        throw new UnauthorizedAccessException("该项目目录不在 ProjectAccess:AllowedRoots 白名单中。");
    }

    public IReadOnlyList<string> AllowedRoots => _allowedRoots;

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))));

    private static bool IsWithin(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
