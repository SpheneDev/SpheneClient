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
        /// <summary>Author-name deep-link - when set, non-deleted author
        /// names render as profile links instead of plain text.</summary>
        public Action<string>? OnAuthorClick { get; set; }
        /// <summary>Member/friend identities for @-mention suggestions -
        /// forwarded from the host edit ops; null hides the picker.</summary>
        public Func<IReadOnlyList<MarkdownComposer.MentionCandidate>>? MentionCandidates { get; set; }
    }

    private sealed class ThreadState
    {
        /// <summary>Serializes every read and write of the collections and
        /// flags below - Draw iterates them on the UI thread while load,
        /// patch and focus-walk completions apply off-thread. Every apply
        /// takes this gate for a tiny section; Draw holds it across the
        /// render so a completion can never modify a collection mid-foreach.</summary>
        public readonly Lock Gate = new();
        public bool Loaded, Loading;
        public List<CommunityCommentDto> Roots = [];
        public readonly Dictionary<long, List<CommunityCommentDto>> Replies = [];
        public readonly HashSet<long> Expanded = [];
        public long? NextCursor;
        public string Draft = string.Empty;
        public bool ComposerOpen;
        public long? ReplyTo;
        public string ReplyDraft = string.Empty;
        public long? EditingId;
        public string EditDraft = string.Empty;
        public readonly Dictionary<long, ReactionSummaryDto> CommentSummaries = [];
        public ReactionSummaryDto? TargetSummary;
        public bool TargetSummaryLoaded;
        public readonly HashSet<long> SummariesLoading = [];
        /// <summary>Deep-link target - scrolls to this comment once loaded;
        /// auto-paginates roots/replies while searching. Survives Reset so a
        /// mid-flight invalidate cannot lose the destination.</summary>
        public long? PendingFocusCommentId;
        /// <summary>One focus-driven page fetch at a time.</summary>
        public bool FocusBusy;
        /// <summary>Roots whose reply pages came back empty during a focus
        /// walk - a stale ReplyCount must not retrigger fetches forever.</summary>
        public readonly HashSet<long> FocusExhaustedRoots = [];

        /// <summary>Drops all loaded content so the next Draw refetches.
        /// Expander state and in-progress drafts survive.</summary>
        public void Reset()
        {
            lock (Gate)
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
                FocusExhaustedRoots.Clear();
            }
        }
    }

    /// <summary>Replies rendered inline before the "Show N more"
    /// affordance kicks in - matches the server's 3-per-root preview
    /// slice, so typical threads never need an extra fetch.</summary>
    private const int PreviewReplyLimit = 3;

    /// <summary>Author avatar edge size - the thread rail and the reply
    /// indent are derived from it, so the rail passes through the
    /// avatar column's center like reddit's gutter line.</summary>
    private const float AuthorAvatarSize = 28f;

    /// <summary>Guards <see cref="_threads"/> - entries are looked up on the
    /// UI thread while invalidations can arrive on mediator threads.</summary>
    private readonly Lock _threadsGate = new();
    private readonly Dictionary<string, ThreadState> _threads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MarkdownComposer> _composers = new(StringComparer.Ordinal);

    /// <summary>Drops the cached thread for one target - the next Draw
    /// reloads roots, replies and reaction summaries.</summary>
    public void Invalidate(CommentTargetKind kind, long targetId)
    {
        lock (_threadsGate)
        {
            if (_threads.TryGetValue($"{(int)kind}:{targetId}", out var st)) st.Reset();
        }
    }

    /// <summary>Drops every cached thread - for pushes without a precise
    /// target (reaction changes) and whole-profile refreshes.</summary>
    public void InvalidateAll()
    {
        lock (_threadsGate)
        {
            foreach (var st in _threads.Values) st.Reset();
        }
    }

    /// <summary>Arms a comment deep-link - the next Draw walks the thread to
    /// the comment (auto-expanding its root for replies) and scrolls to it.
    /// Comments still on unloaded pages are fetched first.</summary>
    public void FocusComment(CommentTargetKind kind, long targetId, long commentId)
    {
        var key = $"{(int)kind}:{targetId}";
        ThreadState st;
        lock (_threadsGate)
        {
            if (!_threads.TryGetValue(key, out st!))
            {
                _threads[key] = st = new ThreadState();
            }
        }
        lock (st.Gate)
        {
            st.PendingFocusCommentId = commentId;
        }
    }

    /// <summary>Invalidates the thread matching a pushed community change;
    /// pushes without a mappable section/target (reactions, page-wide
    /// refreshes) drop every thread for safety.</summary>
    public void InvalidateForChange(CommunityChangedDto change)
    {
        // reaction-only pushes move counts, not content - open threads keep
        // their state and pick up fresh summaries on the next open
        if (change.ReactionsOnly) return;
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
        ThreadState st;
        lock (_threadsGate)
        {
            if (!_threads.TryGetValue(key, out st!))
            {
                _threads[key] = st = new ThreadState();
            }
        }
        lock (st.Gate)
        {
            if (!st.Loaded && !st.Loading && ops.LoadRoots != null)
            {
                st.Loading = true;
                var captured = st;
                _ = Task.Run(async () =>
                {
                    var page = await ops.LoadRoots(null).ConfigureAwait(false);
                    // Apply inside the gate - Draw iterates these same
                    // collections on the UI thread every frame.
                    lock (captured.Gate)
                    {
                        if (page != null)
                        {
                            captured.Roots = page.Roots;
                            captured.NextCursor = page.NextCursor;
                            foreach (var kv in page.ReplyPreviews ?? [])
                            {
                                captured.Replies[kv.Key] = kv.Value;
                            }
                        }
                        captured.Loaded = true;
                        captured.Loading = false;
                    }
                });
            }
            ResolveFocus(ops, st);
            DrawThread(kind, targetId, ownerUid, groupGid, ops, locked,
                commentCount, key, st);
        }
    }

    /// <summary>The actual thread render - the caller holds
    /// <paramref name="st"/>'s <see cref="ThreadState.Gate"/>, so every
    /// collection this iterates is frozen against off-thread applies.</summary>
    private void DrawThread(CommentTargetKind kind, long targetId, string? ownerUid,
        string? groupGid, CommentOps ops, bool locked, int commentCount,
        string key, ThreadState st)
    {
        ImGui.PushID($"cmt-{key}");
        DrawTargetReactions(kind, targetId, ownerUid, groupGid, ops, st);
        SpheneUI.Text.Muted(locked
            ? $"{commentCount} comments · locked"
            : $"{commentCount} comment{(commentCount == 1 ? string.Empty : "s")}");
        // the comment opener docks at the right edge of the target's
        // reaction row - one line, engagement left / composer entry right
        if (!locked && ops.Save != null && !st.ComposerOpen)
        {
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            var openW = SpheneUI.Controls.SegAutoWidth("Comment",
                FontAwesomeIcon.Comment, ImGui.GetFrameHeight());
            SpheneUI.Controls.DockToCardEdge(openW);
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Comment, "Comment"))
            {
                st.ComposerOpen = true;
            }
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        if (locked)
        {
            SpheneUI.Text.Colored("This thread is locked by moderation.",
                SpheneUI.Palette.Warning);
        }
        else if (ops.Save != null && st.ComposerOpen)
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
                    // optimistic close - the draft survives a failed
                    // save, so reopening restores the text
                    st.ComposerOpen = false;
                }
            }
            ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Times, "Cancel"))
            {
                st.ComposerOpen = false;
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
            var rootTop = ImGui.GetCursorScreenPos();
            DrawComment(root, kind, targetId, ownerUid, groupGid, ops, st,
                locked);
            var replies = st.Replies.GetValueOrDefault(root.Id) ?? [];
            var expanded = st.Expanded.Contains(root.Id);
            var shown = expanded ? replies.Count : Math.Min(replies.Count, PreviewReplyLimit);
            if (shown > 0 || root.ReplyCount > 0)
            {
                // the thread rail runs avatar-to-avatar: from the root
                // avatar's bottom edge down the gutter to the last
                // reply's avatar center, with a short elbow into each
                // reply avatar - the reddit thread line
                var railX = rootTop.X + AuthorAvatarSize * 0.5f;
                var railTop = rootTop.Y + AuthorAvatarSize;
                using var indent = ImRaii.PushIndent(AuthorAvatarSize);
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                var stubMids = new List<float>(shown);
                for (var i = 0; i < shown; i++)
                {
                    stubMids.Add(ImGui.GetCursorScreenPos().Y
                        + AuthorAvatarSize * 0.5f);
                    DrawComment(replies[i], kind, targetId, ownerUid, groupGid, ops, st,
                        locked);
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                }
                var remaining = root.ReplyCount - shown;
                if (remaining > 0 && ops.LoadReplies != null
                    && SpheneUI.Icons.TextButton(FontAwesomeIcon.AngleDown,
                        $"Show {remaining} more repl{(remaining == 1 ? "y" : "ies")}"))
                {
                    st.Expanded.Add(root.Id);
                    if (replies.Count < root.ReplyCount)
                    {
                        var next = replies.Count > 0 ? replies[^1].Id : (long?)null;
                        LoadMoreReplies(ops, st, root.Id, next);
                    }
                }
                if (shown > 0)
                {
                    var dl = ImGui.GetWindowDrawList();
                    var railCol = SpheneUI.ColorUtils.ToU32(
                        SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Border, 0.7f));
                    var railW = MathF.Max(1.5f, 1.25f * SpheneUI.Scale);
                    // each elbow is a quarter-circle turning the
                    // vertical rail right into the reply's avatar edge -
                    // arc π/2→π sweeps the bottom-left quadrant, rounded
                    // never a hard corner
                    var elbowR = 6f * SpheneUI.Scale;
                    // the vertical rail ends on the last reply's elbow -
                    // nothing below it exists to connect to
                    var railBot = stubMids[^1] - elbowR;
                    dl.AddLine(new Vector2(railX, railTop),
                        new Vector2(railX, railBot), railCol, railW);
                    foreach (var mid in stubMids)
                    {
                        // +1px on the elbow's x so the arc's left vertex
                        // lands on the rail's right edge, not its center -
                        // owner-visible 1px-left seam otherwise
                        dl.PathArcTo(new Vector2(railX + elbowR + 1f, mid - elbowR),
                            elbowR, MathF.PI * 0.5f, MathF.PI, 8);
                        dl.PathStroke(railCol, ImDrawFlags.None, railW);
                        dl.AddLine(new Vector2(railX + elbowR + 1f, mid),
                            new Vector2(railX + AuthorAvatarSize * 0.5f, mid),
                            railCol, railW);
                    }
                    // clicking the rail collapses the thread back to the
                    // preview slice - the same affordance reddit carries
                    var resume = ImGui.GetCursorScreenPos();
                    ImGui.SetCursorScreenPos(new Vector2(
                        railX - 5f * SpheneUI.Scale, railTop));
                    if (ImGui.InvisibleButton("##rail",
                            new Vector2(10f * SpheneUI.Scale,
                                railBot - railTop)))
                    {
                        st.Expanded.Remove(root.Id);
                    }
                    SpheneUI.AttachTooltip("Collapse replies");
                    ImGui.SetCursorScreenPos(resume);
                }
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
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
        bool locked)
    {
        ImGui.PushID($"c{c.Id}");
        // deep-link landing - one-shot scroll so the comment sits a third
        // down the window, then the pending target clears
        if (st.PendingFocusCommentId == c.Id)
        {
            ImGui.SetScrollHereY(0.35f);
            st.PendingFocusCommentId = null;
        }
        var authorUid = c.Author?.UID;
        var name = (authorUid == null ? null : ops.DisplayName?.Invoke(authorUid))
            ?? AuthorName(c.Author) ?? "Unknown user";
        var state = c.State;

        // only the message text keeps a background card - the header and
        // the footer ride bare on the host surface, the reddit way -
        // deleted/hidden placeholders keep the same inset-card shape
        if (state is CommentState.ModeratorRemoved or CommentState.AuthorDeleted
            || (state == CommentState.ModeratorHidden && c.Body == null))
        {
            SpheneUI.Layout.InnerCard(() =>
                SpheneUI.Text.Muted(state == CommentState.AuthorDeleted
                    ? $"[{name}] deleted this comment"
                    : state == CommentState.ModeratorRemoved
                        ? $"[{name}] comment removed by moderation"
                        : $"[{name}] comment hidden by moderation"),
                $"cbody{c.Id}",
                padding: SpheneUI.Spacing.X8,
                fill: SpheneUI.ColorUtils.LerpColor(
                    SpheneUI.Layout.NavRailBandColor(), Vector4.Zero, 0.18f),
                borderAlpha: 0.25f);
            ImGui.PopID();
            return;
        }

        // header line: avatar + vertically centered name/stamp - bare
        // on the host surface; SameLine lands back on the avatar's top
        // edge, so each following item re-applies the offset via nameTop.
        // Avatar and name are the profile deep-link - no button chrome.
        var authorClick = authorUid != null ? ops.OnAuthorClick : null;
        if (DrawAuthorAvatar(authorUid, ops, AuthorAvatarSize))
        {
            if (authorClick != null)
            {
                if (ImGui.IsItemClicked()) authorClick(authorUid!);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                }
                SpheneUI.AttachTooltip("View profile");
            }
            ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY()
                + (AuthorAvatarSize - ImGui.GetTextLineHeight()) * 0.5f);
        }
        if (authorClick != null)
        {
            if (SpheneUI.Text.Link(name)) authorClick(authorUid!);
            SpheneUI.AttachTooltip("View profile");
        }
        else
        {
            SpheneUI.Text.Plain(name);
        }
        var nameTop = ImGui.GetItemRectMin().Y;
        ImGui.SameLine();
        // GetItemRectMin is screen-space - SetCursorPosY takes
        // window-local coords and would drop the stamp off-view
        ImGui.SetCursorScreenPos(new Vector2(
            ImGui.GetCursorScreenPos().X, nameTop));
        var stamp = c.EditedAt != null
            ? $"{c.CreatedAt:MMM d, HH:mm} · edited"
            : $"{c.CreatedAt:MMM d, HH:mm}";
        SpheneUI.Text.Muted($"· {stamp}");
        if (state == CommentState.ModeratorHidden)
        {
            ImGui.SameLine();
            ImGui.SetCursorScreenPos(new Vector2(
                ImGui.GetCursorScreenPos().X, nameTop));
            SpheneUI.Text.Colored("[hidden]", SpheneUI.Palette.Warning);
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        // body, footer and the reply composer sit under the name column
        // (avatar + gap) - the rail runs free through the avatar gutter
        using (ImRaii.PushIndent(AuthorAvatarSize + SpheneUI.Spacing.X4.Scaled))
        {
            if (st.EditingId == c.Id && ops.Save != null)
            {
                var draft = st.EditDraft;
                ComposerFor($"edit{c.Id}", ops).Draw("##e", ref draft, 4000, 50f * SpheneUI.Scale);
                st.EditDraft = draft;
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Check, "Save"))
                {
                    var dto = new CommentUpsertDto(kind, targetId, ownerUid, groupGid,
                        st.EditDraft, null, c.Id);
                    var captured = st;
                    // the server returns the updated comment - patch it in
                    // place instead of refetching the whole thread
                    _ = Task.Run(async () =>
                    {
                        var saved = await ops.Save(dto).ConfigureAwait(false);
                        if (saved == null) return;
                        lock (captured.Gate)
                        {
                            PatchComment(captured, saved);
                            captured.EditingId = null;
                            captured.EditDraft = string.Empty;
                        }
                    });
                }
                ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Times, "Cancel"))
                {
                    st.EditingId = null;
                }
            }
            else
            {
                // the only card left: the message text in a darker inset
                // well - own comments carry the accent seam on it
                SpheneUI.Layout.InnerCard(() =>
                    SpheneUI.Emoji.Plain(c.Body ?? string.Empty),
                    $"cbody{c.Id}",
                    c.IsMine ? SpheneUI.Colors.Accent : null,
                    padding: SpheneUI.Spacing.X8,
                    fill: SpheneUI.ColorUtils.LerpColor(
                        SpheneUI.Layout.NavRailBandColor(), Vector4.Zero, 0.18f),
                    borderAlpha: 0.25f);
            }

            DrawCommentFooter(c, kind, targetId, ownerUid, groupGid, ops,
                st, locked);

            if (st.ReplyTo == c.Id && ops.Save != null)
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                var draft = st.ReplyDraft;
                ComposerFor($"r{c.Id}", ops).Draw("##rp", ref draft, 4000, 50f * SpheneUI.Scale);
                st.ReplyDraft = draft;
                using (ImRaii.Disabled(string.IsNullOrWhiteSpace(st.ReplyDraft)))
                {
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.PaperPlane, "Reply"))
                    {
                        // replying to a reply still attaches to the root -
                        // the server model is flat depth-2
                        Submit(kind, targetId, ownerUid, groupGid, ops, st,
                            c.ParentId ?? c.Id);
                    }
                }
                ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Times, "Cancel"))
                {
                    st.ReplyTo = null;
                }
            }
        }
        ImGui.PopID();
    }

    /// <summary>The footer row's right-docked action cluster - own
    /// edit/delete, staff moderation and the report flag, drawn just
    /// left of the reply button.</summary>
    private static List<(FontAwesomeIcon Icon, string Tip, Action Act)> BuildCommentActions(
        CommunityCommentDto c, CommentTargetKind kind, long targetId,
        string? ownerUid, string? groupGid, CommentOps ops, ThreadState st)
    {
        var right = new List<(FontAwesomeIcon Icon, string Tip, Action Act)>();
        if (c.IsMine && ops.Save != null && c.State == CommentState.Visible)
        {
            right.Add((FontAwesomeIcon.Pen, "Edit", () =>
            {
                st.EditingId = c.Id;
                st.EditDraft = c.Body ?? string.Empty;
            }));
            if (ops.Delete != null)
            {
                right.Add((FontAwesomeIcon.Trash, "Delete", () =>
                {
                    var captured = c.Id;
                    var capturedSt = st;
                    _ = Task.Run(async () =>
                    {
                        if (await ops.Delete(captured).ConfigureAwait(false))
                        {
                            // server keeps a tombstone - mirror it locally
                            lock (capturedSt.Gate)
                            {
                                PatchComment(capturedSt, c with
                                {
                                    State = CommentState.AuthorDeleted, Body = null
                                });
                                if (c.RootId is { } rootId)
                                {
                                    BumpRootReplyCount(capturedSt, rootId, -1);
                                }
                            }
                        }
                    });
                }));
            }
        }
        if (c.CanModerate && ops.Moderate != null)
        {
            void Mod(int action, string tip, FontAwesomeIcon icon)
                => right.Add((icon, tip, () =>
                {
                    var dto = new CommentModerationDto(kind, targetId, c.Id,
                        action, null, null, null);
                    var capturedSt = st;
                    _ = Task.Run(async () =>
                    {
                        var result = await ops.Moderate!(dto).ConfigureAwait(false);
                        if (result != null)
                        {
                            lock (capturedSt.Gate)
                            {
                                PatchComment(capturedSt, result);
                            }
                        }
                    });
                }));
            if (c.State == CommentState.Visible)
            {
                // Owner-scope hides may be restored by the owner; other
                // scopes require staff - the server enforces either way.
                Mod(0, "Hide comment", FontAwesomeIcon.EyeSlash);
                Mod(2, "Remove comment", FontAwesomeIcon.TimesCircle);
            }
            else if (c.State == CommentState.ModeratorHidden)
            {
                Mod(1, "Restore comment", FontAwesomeIcon.Undo);
            }
        }
        if (!c.IsMine && ops.Report != null)
        {
            right.Add((FontAwesomeIcon.Flag, "Report", () =>
            {
                var dto = new ReportCreateDto(ReportTargetKind.Comment, c.Id,
                    ReportReason.Spam, ownerUid, null,
                    groupGid != null ? new Sphene.API.Data.GroupData(groupGid) : null);
                _ = Task.Run(async () => await ops.Report(dto).ConfigureAwait(false));
            }));
        }

        return right;
    }

    /// <summary>The comment's bottom row - reaction chips (or the
    /// passive strip on own comments) flow left; the management icons
    /// and the reply affordance dock at the row's right edge.</summary>
    private static void DrawCommentFooter(CommunityCommentDto c,
        CommentTargetKind kind, long targetId, string? ownerUid,
        string? groupGid, CommentOps ops, ThreadState st, bool locked)
    {
        // own comments cannot be reacted to - they only display what
        // others cast, and only once a reaction exists; replies carry
        // Reply too - a reply-to-reply still attaches to the root
        var showReactions = !locked && ops.SetReaction != null && !c.IsMine;
        var showReply = !locked && ops.Save != null;
        var actions = BuildCommentActions(c, kind, targetId, ownerUid,
            groupGid, ops, st);
        if (!showReactions && !showReply && !c.IsMine && actions.Count == 0)
        {
            return;
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        var replyW = showReply
            ? SpheneUI.Controls.SegAutoWidth("Reply", FontAwesomeIcon.Reply,
                ImGui.GetFrameHeight())
            : 0f;
        var rightW = replyW;
        if (actions.Count > 0)
        {
            // the icon cluster sits just left of the Reply button
            if (showReply) rightW += SpheneUI.Spacing.X8.Scaled;
            foreach (var a in actions)
            {
                rightW += SpheneUI.Icons.ButtonSize(a.Icon).X
                    + SpheneUI.Spacing.X4.Scaled;
            }
        }
        SpheneUI.Layout.ActionBar(MathF.Max(0f, rightW),
            drawRight: () =>
            {
                for (var i = 0; i < actions.Count; i++)
                {
                    if (i > 0)
                    {
                        ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                    }
                    if (SpheneUI.Icons.Button(actions[i].Icon))
                    {
                        actions[i].Act();
                    }
                    SpheneUI.AttachTooltip(actions[i].Tip);
                }
                // nothing docks without the reply affordance - drawing
                // the button anyway would overhang the bound and clip
                if (!showReply) return;
                if (actions.Count > 0)
                {
                    ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                }
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Reply, "Reply"))
                {
                    st.ReplyTo = st.ReplyTo == c.Id ? null : c.Id;
                    st.ReplyDraft = string.Empty;
                }
            },
            drawLeft: _ =>
            {
                if (c.IsMine)
                {
                    EnsureCommentSummary(c.Id, ops, st);
                    ReactionBar.DrawReadOnly(ReactionTargetKind.Comment, c.Id,
                        st.CommentSummaries.GetValueOrDefault(c.Id));
                }
                else if (showReactions)
                {
                    DrawCommentReactions(c, ops, st);
                }
            });
    }

    /// <summary>Lazy-loads the reaction summary once per comment - shared
    /// by the interactive bar and the read-only strip on own comments.</summary>
    private static void EnsureCommentSummary(long id, CommentOps ops, ThreadState st)
    {
        if (!st.CommentSummaries.ContainsKey(id) && !st.SummariesLoading.Contains(id)
            && ops.LoadCommentSummary != null)
        {
            st.SummariesLoading.Add(id);
            _ = Task.Run(async () =>
            {
                var s = await ops.LoadCommentSummary(id).ConfigureAwait(false);
                lock (st.Gate)
                {
                    if (s != null) st.CommentSummaries[id] = s;
                    st.SummariesLoading.Remove(id);
                }
            });
        }
    }

    private static void DrawCommentReactions(CommunityCommentDto c, CommentOps ops,
        ThreadState st)
    {
        EnsureCommentSummary(c.Id, ops, st);
        var summary = st.CommentSummaries.GetValueOrDefault(c.Id);
        ReactionBar.DrawInline(ReactionTargetKind.Comment, c.Id, summary,
            (emoji, set) =>
            {
                var dto = new ReactionSetDto(ReactionTargetKind.Comment, c.Id,
                    ReactionKind.Like, set, Emoji: emoji, Toggle: true);
                _ = Task.Run(async () =>
                {
                    var s = await ops.SetReaction!(dto).ConfigureAwait(false);
                    lock (st.Gate)
                    {
                        st.CommentSummaries[c.Id] = s;
                    }
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
                var summary = await ops.LoadTargetSummary(targetId).ConfigureAwait(false);
                lock (captured.Gate)
                {
                    captured.TargetSummary = summary;
                }
            });
        }
        var rk = kind switch
        {
            CommentTargetKind.News => ReactionTargetKind.News,
            CommentTargetKind.Event => ReactionTargetKind.Event,
            _ => ReactionTargetKind.Blog,
        };
        ReactionBar.DrawInline(rk, targetId, st.TargetSummary, (emoji, set) =>
        {
            var dto = new ReactionSetDto(rk, targetId, ReactionKind.Like, set, null, ownerUid,
                groupGid != null ? new Sphene.API.Data.GroupData(groupGid) : null,
                Emoji: emoji, Toggle: true);
            _ = Task.Run(async () =>
            {
                var summary = await ops.SetReaction(dto).ConfigureAwait(false);
                lock (st.Gate)
                {
                    st.TargetSummary = summary;
                }
            });
        });
        ImGui.SameLine(0f, SpheneUI.Spacing.X12.Scaled);
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
            lock (st.Gate)
            {
                if (parentId == null)
                {
                    st.Draft = string.Empty;
                    st.Roots.Insert(0, saved);
                }
                else
                {
                    st.ReplyDraft = string.Empty;
                    st.ReplyTo = null;
                    if (!st.Replies.TryGetValue(parentId.Value, out var replies))
                    {
                        st.Replies[parentId.Value] = replies = [];
                    }
                    replies.Add(saved);
                    BumpRootReplyCount(st, parentId.Value, +1);
                }
            }
        });
    }

    /// <summary>Walks a pending focus target to its comment: already-loaded
    /// replies just expand their root; unloaded ones pull one page at a time
    /// (roots first, then the root with unfetched replies). When nothing is
    /// left to load the comment is gone - the focus clears instead of
    /// spinning forever.</summary>
    private static void ResolveFocus(CommentOps ops, ThreadState st)
    {
        if (st.PendingFocusCommentId is not { } focusId || !st.Loaded || st.FocusBusy)
        {
            return;
        }
        if (TryLocateComment(st, focusId, out var parentId))
        {
            if (parentId is { } pid)
            {
                st.Expanded.Add(pid);
            }
            return;
        }
        if (st.NextCursor != null && ops.LoadRoots != null)
        {
            AdvanceFocusLoad(ops, st, null);
            return;
        }
        var nextRoot = FirstRootWithMoreReplies(st);
        if (nextRoot is { } rid && ops.LoadReplies != null)
        {
            AdvanceFocusLoad(ops, st, rid);
            return;
        }
        st.PendingFocusCommentId = null;
    }

    /// <summary>True when the comment sits in loaded state - a root clears
    /// <paramref name="parentId"/>, a reply sets it to its root id.</summary>
    private static bool TryLocateComment(ThreadState st, long id, out long? parentId)
    {
        parentId = null;
        foreach (var root in st.Roots)
        {
            if (root.Id == id) return true;
        }
        foreach (var kv in st.Replies)
        {
            foreach (var reply in kv.Value)
            {
                if (reply.Id == id)
                {
                    parentId = kv.Key;
                    return true;
                }
            }
        }
        return false;
    }

    private static long? FirstRootWithMoreReplies(ThreadState st)
    {
        foreach (var root in st.Roots)
        {
            if (st.FocusExhaustedRoots.Contains(root.Id)) continue;
            var loaded = st.Replies.TryGetValue(root.Id, out var list) ? list.Count : 0;
            if (root.ReplyCount > loaded) return root.Id;
        }
        return null;
    }

    /// <summary>Fetches the next slice for the pending focus - a roots page,
    /// or the next replies page of the first root that still has unfetched
    /// replies. <see cref="ThreadState.FocusBusy"/> gates one fetch at a
    /// time.</summary>
    private static void AdvanceFocusLoad(CommentOps ops, ThreadState st, long? rootId)
    {
        st.FocusBusy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                if (rootId == null)
                {
                    long? cursor;
                    lock (st.Gate)
                    {
                        cursor = st.NextCursor;
                    }
                    var page = await ops.LoadRoots!(cursor).ConfigureAwait(false);
                    if (page == null) return;
                    lock (st.Gate)
                    {
                        st.Roots.AddRange(page.Roots);
                        st.NextCursor = page.NextCursor;
                        foreach (var kv in page.ReplyPreviews ?? [])
                        {
                            st.Replies[kv.Key] = kv.Value;
                        }
                    }
                }
                else if (ops.LoadReplies != null)
                {
                    long? cursor;
                    lock (st.Gate)
                    {
                        cursor = st.Replies.TryGetValue(rootId.Value, out var list)
                            && list.Count > 0 ? list[^1].Id : (long?)null;
                    }
                    var page = await ops.LoadReplies(rootId.Value, cursor).ConfigureAwait(false);
                    if (page == null || page.Roots.Count == 0)
                    {
                        lock (st.Gate)
                        {
                            st.FocusExhaustedRoots.Add(rootId.Value);
                        }
                        return;
                    }
                    lock (st.Gate)
                    {
                        if (!st.Replies.TryGetValue(rootId.Value, out var replies))
                        {
                            st.Replies[rootId.Value] = replies = [];
                        }
                        replies.AddRange(page.Roots);
                    }
                }
            }
            finally
            {
                lock (st.Gate)
                {
                    st.FocusBusy = false;
                }
            }
        });
    }

    /// <summary>Adjusts a root comment's denormalized reply counter after a
    /// local reply insert/remove so the "N replies" affordance stays truthful
    /// without a refetch.</summary>
    private static void BumpRootReplyCount(ThreadState st, long rootId, int delta)
    {
        for (var i = 0; i < st.Roots.Count; i++)
        {
            if (st.Roots[i].Id == rootId)
            {
                st.Roots[i] = st.Roots[i] with
                {
                    ReplyCount = Math.Max(0, st.Roots[i].ReplyCount + delta)
                };
                return;
            }
        }
    }

    /// <summary>Replaces a comment inside the loaded thread state in place -
    /// the write ops return the authoritative DTO, so a save/edit/moderation
    /// never needs a full thread refetch.</summary>
    private static void PatchComment(ThreadState st, CommunityCommentDto updated)
    {
        for (var i = 0; i < st.Roots.Count; i++)
        {
            if (st.Roots[i].Id == updated.Id)
            {
                st.Roots[i] = updated;
                return;
            }
        }
        foreach (var replies in st.Replies.Values)
        {
            for (var i = 0; i < replies.Count; i++)
            {
                if (replies[i].Id == updated.Id)
                {
                    replies[i] = updated;
                    return;
                }
            }
        }
    }

    private static void LoadMoreRoots(CommentOps ops, ThreadState st)
    {
        var cursor = st.NextCursor;
        _ = Task.Run(async () =>
        {
            var page = await ops.LoadRoots!(cursor).ConfigureAwait(false);
            if (page == null) return;
            lock (st.Gate)
            {
                st.Roots.AddRange(page.Roots);
                st.NextCursor = page.NextCursor;
                foreach (var kv in page.ReplyPreviews ?? [])
                {
                    st.Replies[kv.Key] = kv.Value;
                }
            }
        });
    }

    private static void LoadMoreReplies(CommentOps ops, ThreadState st, long rootId, long? cursor)
    {
        _ = Task.Run(async () =>
        {
            var page = await ops.LoadReplies!(rootId, cursor).ConfigureAwait(false);
            if (page == null) return;
            lock (st.Gate)
            {
                if (!st.Replies.TryGetValue(rootId, out var list))
                {
                    st.Replies[rootId] = list = [];
                }
                list.AddRange(page.Roots);
            }
        });
    }

    private MarkdownComposer ComposerFor(string key, CommentOps ops)
    {
        if (!_composers.TryGetValue(key, out var c))
        {
            _composers[key] = c = new MarkdownComposer($"cmt-{key}")
            {
                PlainText = true,
            };
        }
        c.MentionProvider = () => ops.MentionCandidates?.Invoke() ?? [];
        return c;
    }

    /// <summary>Small author avatar via the host's profile hook - texture
    /// key carries a content fingerprint so avatar changes version
    /// themselves instead of needing manual eviction. Returns false when
    /// no avatar was drawn.</summary>
    private static bool DrawAuthorAvatar(string? uid, CommentOps ops, float size)
    {
        if (uid == null || ops.Profile == null) return false;
        var profile = ops.Profile(uid);
        if (profile == null || string.IsNullOrEmpty(profile.Base64ProfilePicture)) return false;
        var wrap = SpheneUI.Media.Texture(
            $"comment-avatar:{uid}:{Sphene.UI.Syncshell.SyncshellCommunityView.MediaVersion(null, profile.Base64ProfilePicture)}",
            () => profile.ImageData.Value);
        if (wrap == null) return false;
        SpheneUI.Media.Avatar(wrap, size);
        return true;
    }

    /// <summary>DisplayName then Alias - a raw uid is never a display string.</summary>
    private static string? AuthorName(UserData? u)
        => u == null ? null
            : !string.IsNullOrWhiteSpace(u.DisplayName) ? u.DisplayName
            : !string.IsNullOrWhiteSpace(u.Alias) ? u.Alias : null;
}
