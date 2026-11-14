using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Sphene.API.Data.Enum;
using Sphene.API.Data;
using Sphene.API.Dto.Group;
using Sphene.Services;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Shared comment thread widget - mounted under news posts, event detail and
/// blog pages. Owns per-target state (roots page, reply caches, composer and
/// edit buffers); every network call goes through <see cref="CommentOps"/>
/// supplied by the host so community and blog surfaces share one impl.
/// Depth-2 threading mirrors the server: replies flatten under their root.
/// </summary>
public sealed class CommentThreadView
{
    /// <summary>Host-bound async operations for one comment surface.</summary>
    public sealed class CommentOps
    {
        public Func<long?, Task<CommentPageDto?>>? LoadRoots { get; set; }
        public Func<long, long?, Task<CommentPageDto?>>? LoadReplies { get; set; }
        public Func<CommentUpsertDto, Task<CommunityCommentDto?>>? Save { get; set; }
        public Func<long, Task<bool>>? Delete { get; set; }
        public Func<CommentModerationDto, Task<CommunityCommentDto?>>? Moderate { get; set; }
        public Func<ReactionSetDto, Task<ReactionSummaryDto>>? SetReaction { get; set; }
        public Func<long, Task<ReactionSummaryDto?>>? LoadCommentSummary { get; set; }
        public Func<long, Task<ReactionSummaryDto?>>? LoadTargetSummary { get; set; }
        public Func<ReportCreateDto, Task<bool>>? Report { get; set; }
        public Func<string, string?>? DisplayName { get; set; }
        /// <summary>Resolves the author's community profile for the inline
        /// avatar - null keeps the text-only fallback.</summary>
        public Func<string, SpheneProfileData?>? Profile { get; set; }
        /// <summary>Member/friend identities for @-mention suggestions -
        /// forwarded from the host edit ops; null hides the picker.</summary>
        public Func<IReadOnlyList<MarkdownComposer.MentionCandidate>>? MentionCandidates { get; set; }
    }

    private sealed class ThreadState
    {
        public bool Loaded, Loading;
        public List<CommunityCommentDto> Roots = [];
        public readonly Dictionary<long, List<CommunityCommentDto>> Replies = [];
        public readonly HashSet<long> Expanded = [];
        public long? NextCursor;
        public string Draft = string.Empty;
        public long? ReplyTo;
        public string ReplyDraft = string.Empty;
        public long? EditingId;
        public string EditDraft = string.Empty;
        public readonly Dictionary<long, ReactionSummaryDto> CommentSummaries = [];
        public ReactionSummaryDto? TargetSummary;
        public bool TargetSummaryLoaded;
        public readonly HashSet<long> SummariesLoading = [];

        /// <summary>Drops all loaded content so the next Draw refetches.
        /// Expander state and in-progress drafts survive.</summary>
        public void Reset()
        {
            Loaded = false;
            Loading = false;
            Roots = [];
            Replies.Clear();
            NextCursor = null;
            CommentSummaries.Clear();
            TargetSummary = null;
            TargetSummaryLoaded = false;
            SummariesLoading.Clear();
        }
    }

    private readonly Dictionary<string, ThreadState> _threads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MarkdownComposer> _composers = new(StringComparer.Ordinal);

    /// <summary>Drops the cached thread for one target - the next Draw
    /// reloads roots, replies and reaction summaries.</summary>
    public void Invalidate(CommentTargetKind kind, long targetId)
    {
        if (_threads.TryGetValue($"{(int)kind}:{targetId}", out var st)) st.Reset();
    }

    /// <summary>Drops every cached thread - for pushes without a precise
    /// target (reaction changes) and whole-profile refreshes.</summary>
    public void InvalidateAll()
    {
        foreach (var st in _threads.Values) st.Reset();
    }

    /// <summary>Invalidates the thread matching a pushed community change;
    /// pushes without a mappable section/target (reactions, page-wide
    /// refreshes) drop every thread for safety.</summary>
    public void InvalidateForChange(CommunityChangedDto change)
    {
        var kind = change.Section switch
        {
            SyncshellCommunitySection.News => CommentTargetKind.News,
            SyncshellCommunitySection.Events => CommentTargetKind.Event,
            _ => (CommentTargetKind?)null
        };
        if (kind is { } k && change.ContentId is { } id)
        {
            Invalidate(k, id);
        }
        else
        {
            InvalidateAll();
        }
    }

    /// <summary>Draws the thread for one target. <paramref name="commentCount"/>
    /// is the host row's denormalized count (badge); <paramref name="locked"/>
    /// the host's thread-lock flag.</summary>
    public void Draw(CommentTargetKind kind, long targetId, string? ownerUid,
        string? groupGid, CommentOps ops, bool locked, int commentCount)
    {
        var key = $"{(int)kind}:{targetId}";
        if (!_threads.TryGetValue(key, out var st))
        {
            _threads[key] = st = new ThreadState();
        }
        if (!st.Loaded && !st.Loading && ops.LoadRoots != null)
        {
            st.Loading = true;
            var captured = st;
            _ = Task.Run(async () =>
            {
                var page = await ops.LoadRoots(null).ConfigureAwait(false);
                if (page != null)
                {
                    captured.Roots = page.Roots;
                    captured.NextCursor = page.NextCursor;
                    foreach (var kv in page.ReplyPreviews)
                    {
                        captured.Replies[kv.Key] = kv.Value;
                    }
                }
                captured.Loaded = true;
                captured.Loading = false;
            });
        }

        ImGui.PushID($"cmt-{key}");
        DrawTargetReactions(kind, targetId, ownerUid, groupGid, ops, st);
        SpheneUI.Text.Muted(locked
            ? $"{commentCount} comments · locked"
            : $"{commentCount} comment{(commentCount == 1 ? string.Empty : "s")}");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        if (locked)
        {
            SpheneUI.Text.Colored("This thread is locked by moderation.",
                SpheneUI.Palette.Warning);
        }
        else if (ops.Save != null)
        {
            var composer = ComposerFor(key, ops);
            var draft = st.Draft;
            composer.Draw("##new", ref draft, 4000, 70f * SpheneUI.Scale);
            st.Draft = draft;
            using (ImRaii.Disabled(string.IsNullOrWhiteSpace(st.Draft)))
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.PaperPlane, "Comment"))
                {
                    Submit(kind, targetId, ownerUid, groupGid, ops, st, null);
                }
            }
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        if (st.Loading && st.Roots.Count == 0)
        {
            SpheneUI.Text.Muted("Loading comments…");
        }
        else if (st.Roots.Count == 0)
        {
            SpheneUI.Text.Muted("No comments yet.");
        }

        foreach (var root in st.Roots)
        {
            DrawComment(root, kind, targetId, ownerUid, groupGid, ops, st,
                locked, isReply: false);
            if (st.Expanded.Contains(root.Id))
            {
                using var indent = ImRaii.PushIndent(16f * SpheneUI.Scale);
                var replies = st.Replies.GetValueOrDefault(root.Id) ?? [];
                foreach (var reply in replies)
                {
                    DrawComment(reply, kind, targetId, ownerUid, groupGid, ops, st,
                        locked, isReply: true);
                }
                if (root.ReplyCount > replies.Count && ops.LoadReplies != null)
                {
                    var next = replies.Count > 0 ? replies[^1].Id : (long?)null;
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.AngleDown,
                            $"Show {root.ReplyCount - replies.Count} more repl{(root.ReplyCount - replies.Count == 1 ? "y" : "ies")}"))
                    {
                        LoadMoreReplies(ops, st, root.Id, next);
                    }
                }
            }
            else if (root.ReplyCount > 0)
            {
                using var indent = ImRaii.PushIndent(16f * SpheneUI.Scale);
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Comment,
                        $"{root.ReplyCount} repl{(root.ReplyCount == 1 ? "y" : "ies")}"))
                {
                    st.Expanded.Add(root.Id);
                    if ((st.Replies.GetValueOrDefault(root.Id)?.Count ?? 0) < root.ReplyCount
                        && ops.LoadReplies != null)
                    {
                        var next = st.Replies.GetValueOrDefault(root.Id) is { Count: > 0 } l
                            ? l[^1].Id : (long?)null;
                        LoadMoreReplies(ops, st, root.Id, next);
                    }
                }
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);
        }

        if (st.NextCursor != null && ops.LoadRoots != null
            && SpheneUI.Icons.TextButton(FontAwesomeIcon.AngleDoubleDown, "Older comments"))
        {
            LoadMoreRoots(ops, st);
        }
        ImGui.PopID();
    }

    private void DrawComment(CommunityCommentDto c, CommentTargetKind kind, long targetId,
        string? ownerUid, string? groupGid, CommentOps ops, ThreadState st,
        bool locked, bool isReply)
    {
        ImGui.PushID($"c{c.Id}");
        var authorUid = c.Author?.UID;
        var name = (authorUid == null ? null : ops.DisplayName?.Invoke(authorUid))
            ?? AuthorName(c.Author) ?? "Unknown user";
        var state = c.State;

        if (state is CommentState.ModeratorRemoved or CommentState.AuthorDeleted)
        {
            SpheneUI.Text.Muted(state == CommentState.AuthorDeleted
                ? $"[{name}] deleted this comment"
                : $"[{name}] comment removed by moderation");
            ImGui.PopID();
            return;
        }
        if (state == CommentState.ModeratorHidden && c.Body == null)
        {
            SpheneUI.Text.Muted($"[{name}] comment hidden by moderation");
            ImGui.PopID();
            return;
        }

        DrawAuthorAvatar(authorUid, ops);
        SpheneUI.Text.Plain(name);
        ImGui.SameLine();
        var stamp = c.EditedAt != null
            ? $"{c.CreatedAt:MMM d, HH:mm} · edited"
            : $"{c.CreatedAt:MMM d, HH:mm}";
        SpheneUI.Text.Muted(stamp);
        if (state == CommentState.ModeratorHidden)
        {
            ImGui.SameLine();
            SpheneUI.Text.Colored("[hidden]", SpheneUI.Palette.Warning);
        }

        if (st.EditingId == c.Id && ops.Save != null)
        {
            var draft = st.EditDraft;
            ComposerFor($"edit{c.Id}", ops).Draw("##e", ref draft, 4000, 50f * SpheneUI.Scale);
            st.EditDraft = draft;
            ImGui.SameLine();
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Check, "Save"))
            {
                var dto = new CommentUpsertDto(kind, targetId, ownerUid, groupGid,
                    st.EditDraft, null, c.Id);
                st.EditingId = null;
                _ = Task.Run(async () => await ops.Save(dto).ConfigureAwait(false));
            }
            ImGui.SameLine();
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Times, "Cancel"))
            {
                st.EditingId = null;
            }
        }
        else
        {
            MarkdownRenderer.RenderMarkdown(c.Body ?? string.Empty, 0,
                MarkdownComposer.MediaImageSource);
        }

        // Action row - react, reply, edit/delete (own), moderate, report.
        if (!locked && ops.SetReaction != null)
        {
            DrawCommentReactions(c, ops, st);
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
        }
        if (!locked && !isReply && ops.Save != null)
        {
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Reply))
            {
                st.ReplyTo = st.ReplyTo == c.Id ? null : c.Id;
                st.ReplyDraft = string.Empty;
            }
            SpheneUI.AttachTooltip("Reply");
            ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
        }
        if (c.IsMine && ops.Save != null && state == CommentState.Visible)
        {
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Pen))
            {
                st.EditingId = c.Id;
                st.EditDraft = c.Body ?? string.Empty;
            }
            SpheneUI.AttachTooltip("Edit");
            ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
            if (ops.Delete != null && SpheneUI.Icons.Button(FontAwesomeIcon.Trash))
            {
                var captured = c.Id;
                _ = Task.Run(async () => await ops.Delete(captured).ConfigureAwait(false));
            }
            SpheneUI.AttachTooltip("Delete");
            ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
        }
        if (c.CanModerate && ops.Moderate != null)
        {
            DrawModerationButtons(c, kind, targetId, ops);
        }
        if (!c.IsMine && ops.Report != null)
        {
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Flag))
            {
                var dto = new ReportCreateDto(ReportTargetKind.Comment, c.Id,
                    ReportReason.Spam, ownerUid, null,
                    groupGid != null ? new Sphene.API.Data.GroupData(groupGid) : null);
                _ = Task.Run(async () => await ops.Report(dto).ConfigureAwait(false));
            }
            SpheneUI.AttachTooltip("Report");
        }

        if (st.ReplyTo == c.Id && ops.Save != null)
        {
            var draft = st.ReplyDraft;
            ComposerFor($"r{c.Id}", ops).Draw("##rp", ref draft, 4000, 50f * SpheneUI.Scale);
            st.ReplyDraft = draft;
            ImGui.SameLine();
            using (ImRaii.Disabled(string.IsNullOrWhiteSpace(st.ReplyDraft)))
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.PaperPlane, "Reply"))
                {
                    Submit(kind, targetId, ownerUid, groupGid, ops, st, c.Id);
                }
            }
        }
        ImGui.PopID();
    }

    private static void DrawCommentReactions(CommunityCommentDto c, CommentOps ops,
        ThreadState st)
    {
        if (!st.CommentSummaries.ContainsKey(c.Id) && !st.SummariesLoading.Contains(c.Id)
            && ops.LoadCommentSummary != null)
        {
            st.SummariesLoading.Add(c.Id);
            var captured = c.Id;
            _ = Task.Run(async () =>
            {
                var s = await ops.LoadCommentSummary(captured).ConfigureAwait(false);
                if (s != null) st.CommentSummaries[captured] = s;
                st.SummariesLoading.Remove(captured);
            });
        }
        var summary = st.CommentSummaries.GetValueOrDefault(c.Id);
        ReactionBar.DrawInline(ReactionTargetKind.Comment, c.Id, summary,
            (k, set) =>
            {
                var dto = new ReactionSetDto(ReactionTargetKind.Comment, c.Id, k, set);
                _ = Task.Run(async () =>
                {
                    var s = await ops.SetReaction!(dto).ConfigureAwait(false);
                    st.CommentSummaries[c.Id] = s;
                });
            });
    }

    private static void DrawTargetReactions(CommentTargetKind kind, long targetId,
        string? ownerUid, string? groupGid, CommentOps ops, ThreadState st)
    {
        if (ops.SetReaction == null || ops.LoadTargetSummary == null) return;
        if (!st.TargetSummaryLoaded)
        {
            st.TargetSummaryLoaded = true;
            var captured = st;
            _ = Task.Run(async () =>
            {
                captured.TargetSummary = await ops.LoadTargetSummary(targetId).ConfigureAwait(false);
            });
        }
        var rk = kind switch
        {
            CommentTargetKind.News => ReactionTargetKind.News,
            CommentTargetKind.Event => ReactionTargetKind.Event,
            _ => ReactionTargetKind.Blog,
        };
        ReactionBar.DrawInline(rk, targetId, st.TargetSummary, (k, set) =>
        {
            var dto = new ReactionSetDto(rk, targetId, k, set, null, ownerUid,
                groupGid != null ? new Sphene.API.Data.GroupData(groupGid) : null);
            _ = Task.Run(async () =>
            {
                st.TargetSummary = await ops.SetReaction(dto).ConfigureAwait(false);
            });
        });
        ImGui.SameLine(0f, SpheneUI.Spacing.X12.Scaled);
    }

    private static void DrawModerationButtons(CommunityCommentDto c, CommentTargetKind kind,
        long targetId, CommentOps ops)
    {
        void Mod(int action, string tip, FontAwesomeIcon icon)
        {
            if (SpheneUI.Icons.Button(icon))
            {
                var dto = new CommentModerationDto(kind, targetId, c.Id, action,
                    null, null, null);
                _ = Task.Run(async () => await ops.Moderate!(dto).ConfigureAwait(false));
            }
            SpheneUI.AttachTooltip(tip);
            ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
        }

        if (c.State == CommentState.Visible)
        {
            Mod(0, "Hide comment", FontAwesomeIcon.EyeSlash);
            Mod(2, "Remove comment", FontAwesomeIcon.TimesCircle);
        }
        else if (c.State == CommentState.ModeratorHidden)
        {
            // Owner-scope hides may be restored by the owner; other scopes
            // require staff/platform - the server enforces either way.
            Mod(1, "Restore comment", FontAwesomeIcon.Undo);
        }
    }

    private static void Submit(CommentTargetKind kind, long targetId, string? ownerUid,
        string? groupGid, CommentOps ops, ThreadState st, long? parentId)
    {
        var body = parentId != null ? st.ReplyDraft : st.Draft;
        var dto = new CommentUpsertDto(kind, targetId, ownerUid, groupGid, body, parentId);
        _ = Task.Run(async () =>
        {
            var saved = await ops.Save!(dto).ConfigureAwait(false);
            if (saved == null) return;
            if (parentId == null)
            {
                st.Draft = string.Empty;
                st.Roots.Insert(0, saved);
            }
            else
            {
                st.ReplyDraft = string.Empty;
                st.ReplyTo = null;
                st.Replies.GetValueOrDefault(parentId.Value)?.Add(saved);
            }
        });
    }

    private static void LoadMoreRoots(CommentOps ops, ThreadState st)
    {
        var cursor = st.NextCursor;
        _ = Task.Run(async () =>
        {
            var page = await ops.LoadRoots!(cursor).ConfigureAwait(false);
            if (page == null) return;
            st.Roots.AddRange(page.Roots);
            st.NextCursor = page.NextCursor;
            foreach (var kv in page.ReplyPreviews)
            {
                st.Replies[kv.Key] = kv.Value;
            }
        });
    }

    private static void LoadMoreReplies(CommentOps ops, ThreadState st, long rootId, long? cursor)
    {
        _ = Task.Run(async () =>
        {
            var page = await ops.LoadReplies!(rootId, cursor).ConfigureAwait(false);
            if (page == null) return;
            if (!st.Replies.TryGetValue(rootId, out var list))
            {
                st.Replies[rootId] = list = [];
            }
            list.AddRange(page.Roots);
        });
    }

    private MarkdownComposer ComposerFor(string key, CommentOps ops)
    {
        if (!_composers.TryGetValue(key, out var c))
        {
            _composers[key] = c = new MarkdownComposer($"cmt-{key}");
        }
        c.MentionProvider = () => ops.MentionCandidates?.Invoke() ?? [];
        return c;
    }

    /// <summary>Small author avatar via the host's profile hook - texture
    /// key carries a content fingerprint so avatar changes version
    /// themselves instead of needing manual eviction.</summary>
    private static void DrawAuthorAvatar(string? uid, CommentOps ops)
    {
        if (uid == null || ops.Profile == null) return;
        var profile = ops.Profile(uid);
        if (profile == null || string.IsNullOrEmpty(profile.Base64ProfilePicture)) return;
        var wrap = SpheneUI.Media.Texture(
            $"comment-avatar:{uid}:{Sphene.UI.Syncshell.SyncshellCommunityView.MediaVersion(null, profile.Base64ProfilePicture)}",
            () => profile.ImageData.Value);
        if (wrap == null) return;
        SpheneUI.Media.Avatar(wrap, 20f);
        ImGui.SameLine();
    }

    /// <summary>DisplayName then Alias - a raw uid is never a display string.</summary>
    private static string? AuthorName(UserData? u)
        => u == null ? null
            : !string.IsNullOrWhiteSpace(u.DisplayName) ? u.DisplayName
            : !string.IsNullOrWhiteSpace(u.Alias) ? u.Alias : null;
}
