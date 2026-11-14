using System.IO;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

/// <summary>
/// Resolves region artwork for area-bound locations. Users drop images into
/// ConfigDirectory/area-art/ - the filename slug (lowercase, non-alphanumeric
/// folded to single dashes) only needs to appear inside the location slug,
/// so "shirogane-ward-3.png" covers "shirogane-ward-3-plot-14" and every
/// property in that ward, while "shirogane.png" covers the whole district.
/// </summary>
public static class AreaArt
{
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".webp" };

    /// <summary>Plugin config directory - set once at startup.</summary>
    public static string? ConfigDir { get; set; }

    public static string Slug(string name)
    {
        var raw = name.ToLowerInvariant();
        var sb = new StringBuilder(raw.Length);
        var dash = false;
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                dash = false;
            }
            else if (!dash && sb.Length > 0)
            {
                sb.Append('-');
                dash = true;
            }
        }
        return sb.ToString().TrimEnd('-');
    }

    /// <summary>"shirogane-ward-3-plot-14" · zero ids are skipped.</summary>
    public static string LocationSlug(string territoryName, uint wardId, uint houseId)
    {
        var sb = new StringBuilder(Slug(territoryName));
        if (wardId > 0)
        {
            sb.Append("-ward-").Append(wardId);
        }
        if (houseId is > 0 and <= 60)
        {
            sb.Append("-plot-").Append(houseId);
        }
        return sb.ToString();
    }

    /// <summary>"Hingashi - Shirogane" → "Shirogane" · falls back to the raw name.</summary>
    public static string TerritoryName(IReadOnlyDictionary<uint, string>? map, uint territoryId)
    {
        if (map != null && map.TryGetValue(territoryId, out var tName))
        {
            var parts = tName.Split(" - ", 2);
            return parts.Length == 2 ? parts[1] : tName;
        }
        return "territory-" + territoryId;
    }

    /// <summary>
    /// Loosely matches the slug against files in area-art/ · the longest file
    /// slug contained in (or containing) the location slug wins.
    /// </summary>
    public static IDalamudTextureWrap? Wrap(string? locationSlug)
    {
        if (string.IsNullOrWhiteSpace(locationSlug) || string.IsNullOrEmpty(ConfigDir))
        {
            return null;
        }
        var dir = Path.Combine(ConfigDir, "area-art");
        var slug = Slug(locationSlug);
        string? path = null;
        if (Directory.Exists(dir))
        {
            foreach (var ext in Extensions)
            {
                var exact = Path.Combine(dir, slug + ext);
                if (File.Exists(exact))
                {
                    path = exact;
                    break;
                }
            }
            if (path == null)
            {
                var best = 0;
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var fileSlug = Slug(Path.GetFileNameWithoutExtension(file));
                    if (fileSlug.Length > best
                        && (slug.Contains(fileSlug, StringComparison.Ordinal)
                            || fileSlug.Contains(slug, StringComparison.Ordinal)))
                    {
                        best = fileSlug.Length;
                        path = file;
                    }
                }
            }
        }
        return path == null
            ? null
            : SpheneUI.Media.Texture($"areaart:{Path.GetFileName(path)}",
                () => File.ReadAllBytes(path));
    }
}
