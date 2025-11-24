using Sphene.API.Data;

using System.Text.RegularExpressions;

namespace Sphene.PlayerData.Data;

public partial class FileReplacement
{
    public FileReplacement(string[] gamePaths, string filePath)
    {
        GamePaths = gamePaths.Select(g => g.Replace('\\', '/').ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        ResolvedPath = filePath.Replace('\\', '/');
    }

    public HashSet<string> GamePaths { get; init; }

    public bool HasFileReplacement => GamePaths.Count >= 1 && GamePaths.Any(p => !string.Equals(p, ResolvedPath, StringComparison.Ordinal));

    public string Hash { get; set; } = string.Empty;
    public bool IsFileSwap => !IsLocalPath(ResolvedPath) && GamePaths.All(p => !IsLocalPath(p));
    public string ResolvedPath { get; init; }

    public FileReplacementData ToFileReplacementDto()
    {
        return new FileReplacementData
        {
            GamePaths = [.. GamePaths],
            Hash = Hash,
            FileSwapPath = IsFileSwap ? ResolvedPath : string.Empty,
        };
    }

    public override string ToString()
    {
        return $"HasReplacement:{HasFileReplacement},IsFileSwap:{IsFileSwap} - {string.Join(",", GamePaths)} => {ResolvedPath}";
    }

    private static bool IsLocalPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 3) return false;
        var c0 = path[0];
        var c1 = path[1];
        var c2 = path[2];
        return char.IsLetter(c0) && c1 == ':' && (c2 == '/' || c2 == '\\');
    }
}
