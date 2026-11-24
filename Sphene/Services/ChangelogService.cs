using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.Json;

namespace Sphene.Services;

public sealed class ChangelogService
{
    private readonly ILogger<ChangelogService> _logger;
    private readonly HttpClient _httpClient;
    private const string DefaultUrl = "https://raw.githubusercontent.com/SpheneDev/repo/refs/heads/main/changelogs/sphene.json";

    public ChangelogService(ILogger<ChangelogService> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task<string?> GetChangelogTextForVersionAsync(string version, CancellationToken ct = default, bool requireExactVersion = false)
    {
        var url = DefaultUrl;

        try
        {
            _logger.LogDebug("Fetching release changelog from {url}", url);
            using var resp = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            using var jsonDoc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = jsonDoc.RootElement;
            if (!root.TryGetProperty("changelogs", out var changelogs) || changelogs.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("Changelog JSON did not contain an array 'changelogs'");
                return null;
            }

            JsonElement? selected = null;
            if (requireExactVersion)
            {
                var requestedNormalized = ParseVersionSafe(version);
                JsonElement? normalizedMatch = null;
                JsonElement? coreMatch = null;
                foreach (var item in changelogs.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    string v = item.TryGetProperty("version", out var vProp) && vProp.ValueKind == JsonValueKind.String
                        ? vProp.GetString() ?? string.Empty
                        : string.Empty;

                    if (!string.IsNullOrEmpty(v) && string.Equals(v, version, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = item;
                        break;
                    }

                    var parsedCandidate = ParseVersionSafe(v);
                    if (normalizedMatch == null && parsedCandidate.Equals(requestedNormalized))
                    {
                        normalizedMatch = item;
                    }
                    if (coreMatch == null && SameCoreVersion(parsedCandidate, requestedNormalized))
                    {
                        coreMatch = item;
                    }
                }

                selected ??= normalizedMatch ?? coreMatch;
            }
            else
            {
                var requestedVersion = ParseVersionSafe(version);
                Version bestVersion = new Version(0, 0, 0, 0);
                foreach (var item in changelogs.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    string v = item.TryGetProperty("version", out var vProp) && vProp.ValueKind == JsonValueKind.String
                        ? vProp.GetString() ?? string.Empty
                        : string.Empty;

                    if (!string.IsNullOrEmpty(v) && string.Equals(v, version, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = item;
                        break;
                    }

                    var parsed = ParseVersionSafe(v);
                    if (parsed <= requestedVersion && parsed > bestVersion)
                    {
                        bestVersion = parsed;
                        selected = item;
                    }
                }
            }

            if (selected is null)
            {
                if (requireExactVersion)
                {
                    _logger.LogDebug("No changelog entry found for version {version}", version);
                }
                else
                {
                    _logger.LogWarning("No matching or fallback changelog entry found");
                }
                return null;
            }

            var builder = new System.Text.StringBuilder();
            if (selected.Value.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String)
            {
                var title = titleProp.GetString();
                if (!string.IsNullOrWhiteSpace(title)) builder.AppendLine(title!.Trim());
            }

            if (selected.Value.TryGetProperty("description", out var descProp) && descProp.ValueKind == JsonValueKind.String)
            {
                var description = descProp.GetString();
                if (!string.IsNullOrWhiteSpace(description)) builder.AppendLine(description!.Trim());
            }

            if (selected.Value.TryGetProperty("changes", out var changesProp) && changesProp.ValueKind == JsonValueKind.Array)
            {
                builder.AppendLine();
                foreach (var change in changesProp.EnumerateArray())
                {
                    if (change.ValueKind == JsonValueKind.String)
                    {
                        var line = change.GetString();
                        if (!string.IsNullOrWhiteSpace(line)) builder.AppendLine($"• {line}");
                    }
                    else if (change.ValueKind == JsonValueKind.Object)
                    {
                        string text = string.Empty;
                        if (change.TryGetProperty("description", out var dProp) && dProp.ValueKind == JsonValueKind.String)
                            text = dProp.GetString() ?? string.Empty;

                        var subLines = new List<string>();
                        var hasSubArray = change.TryGetProperty("sub", out var subProp) && subProp.ValueKind == JsonValueKind.Array;
                        if (hasSubArray)
                            subLines = ParseSubLines(subProp);

                        if (hasSubArray && subLines.Count == 0)
                            continue;

                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            builder.AppendLine($"• {text}");
                            foreach (var subLine in subLines)
                            {
                                builder.AppendLine($"  • {subLine}");
                            }
                        }
                    }
                }
            }

            return builder.ToString().Trim();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching release changelog JSON");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parse error in release changelog");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch or parse release changelog JSON");
            return null;
        }
    }

    private readonly Dictionary<string, byte[]?> _imageCache = new(StringComparer.Ordinal);

    /// <summary>Fetches an image referenced by a changelog field (banner,
    /// gallery). Relative paths resolve against the changelog repo; a leading
    /// "repo/" prefix (repo-folder style) is stripped. Cached per URL.</summary>
    /// <summary>Base URL for repo-relative changelog assets - derived from
    /// the changelog URL's directory (".../changelogs/" → repo root).</summary>
    private static string ResolveImageUrl(string path)
    {
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
        var p = path.TrimStart('/');
        if (p.StartsWith("repo/", StringComparison.Ordinal))
        {
            p = p[5..];
        }
        var baseUrl = DefaultUrl.Substring(0,
            DefaultUrl.LastIndexOf("changelogs/", StringComparison.Ordinal));
        return baseUrl + p;
    }

    public byte[]? GetCachedImage(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = ResolveImageUrl(url);
        if (_imageCache.TryGetValue(url, out var cached)) return cached;
        _imageCache[url] = null;
        _ = Task.Run(async () =>
        {
            try
            {
                _imageCache[url] = await _httpClient.GetByteArrayAsync(url)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Changelog image fetch failed: {url}", url);
            }
        });
        return null;
    }

public async Task<List<ReleaseChangelogViewEntry>> GetChangelogEntriesAsync(CancellationToken ct = default)
    {
        var url = DefaultUrl;
        var result = new List<ReleaseChangelogViewEntry>();
        try
        {
            _logger.LogDebug("Fetching release changelog entries from {url}", url);
            using var resp = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            using var jsonDoc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = jsonDoc.RootElement;
            if (!root.TryGetProperty("changelogs", out var changelogs) || changelogs.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("Changelog JSON did not contain an array 'changelogs'");
                return result;
            }

            foreach (var item in changelogs.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var entry = new ReleaseChangelogViewEntry();
                entry.Version = item.TryGetProperty("version", out var vProp) && vProp.ValueKind == JsonValueKind.String
                    ? vProp.GetString() ?? string.Empty
                    : string.Empty;
                entry.Title = item.TryGetProperty("title", out var tProp) && tProp.ValueKind == JsonValueKind.String
                    ? (tProp.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                entry.Date = item.TryGetProperty("date", out var dateProp) && dateProp.ValueKind == JsonValueKind.String
                    ? (dateProp.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                entry.Description = item.TryGetProperty("description", out var dProp) && dProp.ValueKind == JsonValueKind.String
                    ? (dProp.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                entry.IsPrerelease = item.TryGetProperty("isPrerelease", out var prProp) && prProp.ValueKind == JsonValueKind.True;
                // structure v3 · optional presentation fields, all absent-tolerant
                if (item.TryGetProperty("banner", out var bProp) && bProp.ValueKind == JsonValueKind.String)
                    entry.Banner = bProp.GetString() ?? string.Empty;
                if (item.TryGetProperty("author", out var aProp) && aProp.ValueKind == JsonValueKind.String)
                    entry.Author = aProp.GetString() ?? string.Empty;
                if (item.TryGetProperty("pinned", out var pProp) && pProp.ValueKind == JsonValueKind.True)
                    entry.Pinned = true;
                if (item.TryGetProperty("highlights", out var hProp) && hProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var h in hProp.EnumerateArray())
                    {
                        var hv = new ReleaseHighlightView();
                        if (h.TryGetProperty("icon", out var hi) && hi.ValueKind == JsonValueKind.String)
                            hv.Icon = hi.GetString() ?? string.Empty;
                        if (h.TryGetProperty("title", out var ht) && ht.ValueKind == JsonValueKind.String)
                            hv.Title = ht.GetString() ?? string.Empty;
                        if (h.TryGetProperty("text", out var hx) && hx.ValueKind == JsonValueKind.String)
                            hv.Text = hx.GetString() ?? string.Empty;
                        if (hv.Title.Length > 0) entry.Highlights.Add(hv);
                    }
                }
                if (item.TryGetProperty("gallery", out var gProp) && gProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var g in gProp.EnumerateArray())
                    {
                        if (g.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(g.GetString()))
                            entry.Gallery.Add(g.GetString()!);
                    }
                }
                if (item.TryGetProperty("links", out var lProp) && lProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var l in lProp.EnumerateArray())
                    {
                        var lv = new ReleaseLinkView();
                        if (l.TryGetProperty("label", out var ll) && ll.ValueKind == JsonValueKind.String)
                            lv.Label = ll.GetString() ?? string.Empty;
                        if (l.TryGetProperty("url", out var lu) && lu.ValueKind == JsonValueKind.String)
                            lv.Url = lu.GetString() ?? string.Empty;
                        if (lv.Label.Length > 0 && lv.Url.Length > 0) entry.Links.Add(lv);
                    }
                }
                ExtractLegacyTitleDate(entry);

                if (item.TryGetProperty("changes", out var changesProp) && changesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var change in changesProp.EnumerateArray())
                    {
                        var view = new ReleaseChangeView();
                        if (change.ValueKind == JsonValueKind.String)
                        {
                            var line = change.GetString();
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                view.Text = line!.Trim();
                                entry.Changes.Add(view);
                            }
                        }
                        else if (change.ValueKind == JsonValueKind.Object)
                        {
                            string text = string.Empty;
                            if (change.TryGetProperty("description", out var cdProp) && cdProp.ValueKind == JsonValueKind.String)
                                text = cdProp.GetString() ?? string.Empty;

                            var subLines = new List<string>();
                            var hasSubArray = change.TryGetProperty("sub", out var subProp) && subProp.ValueKind == JsonValueKind.Array;
                            if (hasSubArray)
                                subLines = ParseSubLines(subProp);

                            if (hasSubArray && subLines.Count == 0)
                                continue;

                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                view.Text = text.Trim();
                                foreach (var subLine in subLines)
                                {
                                    view.Sub.Add(subLine);
                                }

                                entry.Changes.Add(view);
                            }
                        }
                    }
                }

                result.Add(entry);
            }

            // Sort descending by version
            result.Sort((a, b) => ParseVersionSafe(b.Version).CompareTo(ParseVersionSafe(a.Version)));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching release changelog entries");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parse error in release changelog entries");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch or parse release changelog entries");
        }

        return result;
    }

    private static List<string> ParseSubLines(JsonElement subProp)
    {
        var lines = new List<string>();
        foreach (var sub in subProp.EnumerateArray())
        {
            if (sub.ValueKind == JsonValueKind.String)
            {
                var line = sub.GetString();
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line.Trim());
                continue;
            }

            if (sub.ValueKind != JsonValueKind.Object)
                continue;

            if (sub.TryGetProperty("subcategory", out var subcategoryProp) && subcategoryProp.ValueKind == JsonValueKind.String &&
                sub.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
            {
                var subcategory = (subcategoryProp.GetString() ?? string.Empty).Trim();
                var addedSubcategoryHeading = false;
                foreach (var item in itemsProp.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        continue;

                    var itemText = (item.GetString() ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(itemText))
                        continue;

                    if (!string.IsNullOrWhiteSpace(subcategory) && !addedSubcategoryHeading)
                    {
                        lines.Add($"{subcategory}:");
                        addedSubcategoryHeading = true;
                    }

                    lines.Add(itemText);
                }

                continue;
            }

            if (sub.TryGetProperty("description", out var descriptionProp) && descriptionProp.ValueKind == JsonValueKind.String)
            {
                var line = descriptionProp.GetString();
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line.Trim());
            }
        }

        return lines;
    }

    // Legacy v1-era entries carry the date inside the title as "[YY-MM-DD]"
    // or "[YYYY-MM-DD]" - move it into Date and keep only the trailing title.
    private static void ExtractLegacyTitleDate(ReleaseChangelogViewEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Date) || string.IsNullOrWhiteSpace(entry.Title))
            return;
        var t = entry.Title.Trim();
        if (!t.StartsWith('['))
            return;
        var end = t.IndexOf(']');
        if (end <= 1)
            return;
        var parts = t.Substring(1, end - 1).Trim().Split('-');
        if (parts.Length != 3)
            return;
        foreach (var p in parts)
        {
            if (p.Length == 0 || p.Length > 4 || !p.All(char.IsDigit))
            {
                return;
            }
        }
        if (parts[0].Length != 2 && parts[0].Length != 4)
            return;
        var year = parts[0].Length == 2 ? $"20{parts[0]}" : parts[0];
        entry.Date = $"{year}-{parts[1].PadLeft(2, '0')}-{parts[2].PadLeft(2, '0')}";
        entry.Title = t[(end + 1)..].Trim();
    }

    private static bool SameCoreVersion(Version a, Version b)
        => a.Major == b.Major && a.Minor == b.Minor && a.Build == b.Build;

    private static Version ParseVersionSafe(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return new Version(0,0,0,0);
        try
        {
            var parts = v.Trim().Split('-', StringSplitOptions.RemoveEmptyEntries);
            var core = parts[0].Trim();
            if (core.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                core = core[1..].Trim();
            }
            var parsed = Version.Parse(core);
            var major = parsed.Major < 0 ? 0 : parsed.Major;
            var minor = parsed.Minor < 0 ? 0 : parsed.Minor;
            var build = parsed.Build < 0 ? 0 : parsed.Build;
            var revision = parsed.Revision < 0 ? 0 : parsed.Revision;
            return new Version(major, minor, build, revision);
        }
        catch
        {
            return new Version(0,0,0,0);
        }
    }
}
