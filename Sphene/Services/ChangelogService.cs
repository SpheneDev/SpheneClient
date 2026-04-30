using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.Json;

namespace Sphene.Services;

public sealed class ChangelogService
{
    private readonly ILogger<ChangelogService> _logger;
    private readonly HttpClient _httpClient;
    private const string DefaultUrl = "https://sphene.online/sphene_changelogs/changelog.json";

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

                    if (normalizedMatch == null && ParseVersionSafe(v).Equals(requestedNormalized))
                    {
                        normalizedMatch = item;
                    }
                }

                if (selected == null && normalizedMatch != null)
                {
                    selected = normalizedMatch;
                }
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
                _logger.LogWarning("No matching or fallback changelog entry found");
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch or parse release changelog JSON");
            return null;
        }
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
