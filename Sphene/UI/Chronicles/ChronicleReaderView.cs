using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.User;
using Sphene.Services;
using Sphene.UI.Components;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Chronicles;

/// <summary>
/// The single Chronicle reading surface - used by the profile list cards, the
/// friend reader and the ChroniclePreviewUi live preview so the published
/// presentation and the draft preview can never diverge. Layout contract:
/// cover, then ONE header row (author identity left, visibility badge +
/// optional item actions as a single right-docked group), the title as the
/// primary line, then the body - full markdown in detail/preview, a bounded
/// (clipped, still fully formatted) excerpt with Show more/less in list cards.
/// </summary>
public static class ChronicleReaderView
{
    /// <summary>Resolves cover/author media through the community media cache.</summary>
    private static byte[]? CoverBytes(string? hash)
        => MarkdownComposer.MediaClient?.TryGetBytes(hash);

    /// <summary>Draws the reader. <paramref name="avatarWrap"/> is the resolved
    /// author avatar texture (may be null); <paramref name="ops"/> drives the
    /// discussion widget - null renders no comment affordance (previews and
    /// list cards). <paramref name="excerptLines"/> &gt; 0 bounds the body to
    /// that many rendered lines (full Markdown intact, clipped by a scroll-
    /// locked child region) and offers a Show more/less toggle through
    /// <paramref name="isExpanded"/>/<paramref name="toggleExpanded"/>.
    /// <paramref name="actions"/> is an optional right-docked control strip
    /// (edit/delete chips on the own list) inside the header's right group.</summary>
    public static void Draw(UserProfileBlogPostDto post, string authorUid,
        string authorDisplayName, IDalamudTextureWrap? avatarWrap, Vector4 accent,
        CommentThreadView comments, CommentThreadView.CommentOps? ops,
        string? previewNote = null, int excerptLines = 0,
        Func<long, bool>? isExpanded = null, Action<long>? toggleExpanded = null,
        Action? actions = null)
    {
        var scale = SpheneUI.Scale;
        if (previewNote != null)
        {
            SpheneUI.Layout.Callout(previewNote, FontAwesomeIcon.Eye,
                SpheneUI.Colors.TextMuted, title: "Draft preview");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }

        if (post.CoverMediaHash is { } hash)
        {
            var wrap = SpheneUI.Media.Texture($"chronicle-cover:{post.Id}:{hash}",
                () => CoverBytes(hash));
            if (wrap != null)
            {
                var dl = ImGui.GetWindowDrawList();
                var w = ImGui.GetContentRegionAvail().X;
                var h = MathF.Min(w * 0.38f, 220f * scale);
                ImGui.Dummy(new Vector2(w, h));
                SpheneUI.Media.ImageInRect(dl, wrap, ImGui.GetItemRectMin(),
                    ImGui.GetItemRectMax(), ImageFit.Cover, rounding: 8f * scale);
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            }
        }

        // Header row - author identity left; visibility badge, status badge
        // and the caller's action strip as one right-docked group. A table
        // does the geometry, so nothing can be pushed outside the card.
        DrawHeaderRow(post, authorDisplayName, avatarWrap, accent, actions);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        // Optional title - a real heading (display font, theme text color,
        // no underline); title-less posts skip the line entirely.
        if (!string.IsNullOrWhiteSpace(post.Title))
        {
            SpheneUI.Text.Big(post.Title);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }
        SpheneUI.Layout.Separator();
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        var expanded = isExpanded?.Invoke(post.Id) == true;
        if (!string.IsNullOrWhiteSpace(post.Body))
        {
            if (excerptLines > 0 && !expanded)
            {
                // Bounded excerpt: the SAME markdown renderer inside a
                // scroll-locked child - tags stay valid, styling survives,
                // content is clipped at the line budget instead of
                // plain-texted.
                var lineH = ImGui.GetTextLineHeight();
                var maxH = lineH * excerptLines
                    + ImGui.GetStyle().ItemSpacing.Y * (excerptLines - 1);
                var overflow = false;
                using (var clip = ImRaii.Child($"##chron-excerpt-{post.Id}",
                           new Vector2(-1f, maxH), border: false,
                           ImGuiWindowFlags.NoScrollbar))
                {
                    if (clip.Success)
                    {
                        MarkdownRenderer.RenderMarkdown(post.Body!, 0,
                            MarkdownComposer.MediaImageSource);
                        overflow = ImGui.GetScrollMaxY() > 1f;
                    }
                }
                if (overflow && toggleExpanded != null
                    && SpheneUI.Icons.TextButton(FontAwesomeIcon.AngleDown,
                        "Show more"))
                {
                    toggleExpanded(post.Id);
                }
            }
            else
            {
                MarkdownRenderer.RenderMarkdown(post.Body!, 0,
                    MarkdownComposer.MediaImageSource);
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                EntityChipList.Draw(post.Body);
                if (excerptLines > 0 && expanded && toggleExpanded != null
                    && SpheneUI.Icons.TextButton(FontAwesomeIcon.AngleUp,
                        "Show less"))
                {
                    toggleExpanded(post.Id);
                }
            }
        }
        else
        {
            SpheneUI.Text.MutedWrapped("This Chronicle has no body yet.");
        }

        // Discussion always renders when ops are bound - the excerpt
        // toggle only bounds the body, never the thread.
        if (ops != null)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            SpheneUI.Layout.Separator();
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            comments.Draw(CommentTargetKind.Blog, post.Id, authorUid, null, ops,
                post.CommentsLocked, post.CommentCount);
        }
    }

    /// <summary>[Avatar] Author · Date -- [Everyone][status] [actions]. A
    /// two-column table: the left column stretches with the author row, the
    /// right column is fixed to its content and therefore docked to the
    /// card's padded edge. No cursor math, no coordinate mixing.</summary>
    private static void DrawHeaderRow(UserProfileBlogPostDto post,
        string authorDisplayName, IDalamudTextureWrap? avatarWrap, Vector4 accent,
        Action? actions)
    {
        var avSize = ImGui.GetFrameHeight();
        var lineH = ImGui.GetTextLineHeight();
        var dl = ImGui.GetWindowDrawList();

        using var table = ImRaii.Table($"##chron-head-{post.Id}", 2,
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoPadInnerX);
        if (!table.Success)
        {
            return;
        }
        ImGui.TableSetupColumn("author", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("actions", ImGuiTableColumnFlags.WidthFixed);

        // - left group: avatar + name + date, vertically centered -
        ImGui.TableNextColumn();
        var pos = ImGui.GetCursorScreenPos();
        if (avatarWrap != null)
        {
            SpheneUI.Media.ImageInRect(dl, avatarWrap, pos,
                pos + new Vector2(avSize, avSize), ImageFit.Cover,
                rounding: avSize * 0.5f);
        }
        else
        {
            SpheneUI.Layout.IconPlaceholder(dl, pos, avSize,
                FontAwesomeIcon.FeatherAlt, accent, avSize * 0.5f);
        }
        ImGui.Dummy(new Vector2(avSize, avSize));
        ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY()
            + MathF.Max(0f, (avSize - lineH) * 0.5f));
        using (ImRaii.Group())
        {
            SpheneUI.Text.Plain(authorDisplayName);
            ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
            SpheneUI.Text.Muted(
                "· " + SpheneUI.Text.FormatDateTime(post.UpdatedAt.ToLocalTime()));
        }

        // - right group: badges + optional action strip, one docked unit -
        ImGui.TableNextColumn();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY()
            + MathF.Max(0f, (avSize - SpheneUI.Layout.BadgeHeight) * 0.5f));
        var visText = post.VisibilityScope switch
        {
            (int)BlogVisibilityScope.Pairs => "Friends",
            (int)BlogVisibilityScope.Private => "Only me",
            _ => "Everyone",
        };
        // Badge advances the cursor itself via SetCursorScreenPos but does
        // NOT register an ImGui item - SameLine here would re-anchor to an
        // item in another column. Advance with local cursor moves instead.
        SpheneUI.Layout.Badge(visText, SpheneUI.Colors.Accent with { W = 0.75f });
        var statusText = post.Pinned ? "Pinned"
            : post.Status == (int)ContentPublishStatus.Draft ? "Draft"
            : post.Status == (int)ContentPublishStatus.Scheduled ? "Scheduled"
            : null;
        if (statusText != null)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + SpheneUI.Spacing.X4.Scaled);
            SpheneUI.Layout.Badge(statusText, statusText switch
            {
                "Pinned" => SpheneUI.Palette.SpheneGold,
                "Scheduled" => SpheneUI.Palette.Warning,
                _ => SpheneUI.Colors.TextMuted,
            });
        }
        if (actions != null)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + SpheneUI.Spacing.X8.Scaled);
            actions();
        }
    }
}
