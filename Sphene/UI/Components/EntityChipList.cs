using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Sphene.API.Dto.Group;
using Sphene.UI.Design;
using Sphene.WebAPI;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Renders entity-reference tokens inside a markdown body as visual chips -
/// visually distinct from text, clickable through the hub navigation system.
/// Resolution always goes through <c>EntityResolveReferences</c> server-side;
/// invisible targets stay generic placeholders and never leak content.
/// </summary>
public static class EntityChipList
{
    /// <summary>Wired once from DI - performs the server-side resolve.</summary>
    public static ApiController? Api { get; set; }

    /// <summary>Host navigation hook: receives the resolved <c>NavKey</c>
    /// (<c>user:{uid}</c>, <c>community:{gid}</c>, <c>community:{gid}:post:{id}</c>...)
    /// and maps it onto <c>HubLocation</c>.</summary>
    public static Action<string>? Navigate { get; set; }

    // Display-side tokenizer - mirrors EntityReferenceService's shapes; the
    // server remains the only authority on what resolves.
    private static readonly Regex TokenRe = new(
        @"(?<![\w/])(?:[@#][A-Za-z0-9_.\-]{2,64}|(?:event|post|blog|announcement):[A-Za-z0-9_\-]{1,64}(?::\d{1,19})?)",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(250));

    private static readonly ConcurrentDictionary<string, Task<List<EntityCardDto>>> Cache
        = new(StringComparer.Ordinal);
    private const int CacheLimit = 64;

    /// <summary>Draws chips for every token found in <paramref name="body"/>.
    /// Call directly below the rendered markdown so chips sit with the text.</summary>
    public static void Draw(string? body)
    {
        if (Api == null || string.IsNullOrEmpty(body) ||
            body.IndexOfAny(['@', '#']) < 0 && !body.Contains(':'))
        {
            return;
        }
        var tokens = new List<string>();
        foreach (Match m in TokenRe.Matches(body))
        {
            if (!tokens.Contains(m.Value, StringComparer.Ordinal))
            {
                tokens.Add(m.Value);
            }
        }
        if (tokens.Count == 0) return;

        var key = string.Join('\n', tokens);
        var task = Cache.GetOrAdd(key, k => Api.EntityResolveReferences(tokens));
        if (Cache.Count > CacheLimit) Cache.Clear();
        if (task is not { IsCompleted: true } || !task.IsCompletedSuccessfully)
        {
            return; // chips appear next frame once resolved - placeholder meanwhile
        }

        var first = true;
        foreach (var card in task.Result)
        {
            if (!first) ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
            first = false;
            DrawChip(card);
        }
    }

    private static void DrawChip(EntityCardDto card)
    {
        var (icon, label) = card.Kind switch
        {
            Sphene.API.Data.Enum.EntityRefKind.User => (FontAwesomeIcon.User, card.Title ?? "unavailable"),
            Sphene.API.Data.Enum.EntityRefKind.Syncshell => (FontAwesomeIcon.Users, card.Title ?? "unavailable"),
            Sphene.API.Data.Enum.EntityRefKind.Event => (FontAwesomeIcon.Calendar, card.Title ?? "unavailable"),
            Sphene.API.Data.Enum.EntityRefKind.NewsPost => (FontAwesomeIcon.Newspaper, card.Title ?? "unavailable"),
            Sphene.API.Data.Enum.EntityRefKind.BlogPost => (FontAwesomeIcon.BookOpen, card.Title ?? "unavailable"),
            _ => (FontAwesomeIcon.Link, card.Title ?? "unavailable"),
        };

        var clickable = card.Resolved && card.NavKey != null && Navigate != null;
        if (clickable)
        {
            if (SpheneUI.Icons.TextButton(icon, label))
            {
                Navigate!(card.NavKey!);
            }
        }
        else
        {
            SpheneUI.Icons.Icon(icon, SpheneUI.Colors.TextSecondary);
            ImGui.SameLine(0f, SpheneUI.Spacing.X2.Scaled);
            SpheneUI.Text.Muted(card.Resolved ? label : "unavailable " + card.Kind.ToString().ToLowerInvariant());
        }
        if (card.Resolved && ImGui.IsItemHovered())
        {
            SpheneUI.AttachTooltip(card.Snippet ?? card.Token);
        }
    }
}
