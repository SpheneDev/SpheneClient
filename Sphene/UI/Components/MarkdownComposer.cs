using System.Collections.Concurrent;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.Services;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Shared markdown editor for community content (news, blogs, events).
/// Reuses the single <see cref="MarkdownRenderer"/> engine for a WYSIWYG
/// preview - identical to the published rendering - plus a formatting
/// toolbar, media upload via <see cref="CommunityMediaClientService"/> and an
/// entity-reference picker that inserts <c>@uid</c>/<c>#gid</c>/<c>post:id</c>
/// tokens the server resolves with visibility checks.
/// Toolbar edits run inside the input's <see cref="ImGuiInputTextFlags.CallbackAlways"/>
/// callback so every format, insert and wrap happens at the real caret /
/// selection in UTF-8 byte space - never an append at the document end.
/// </summary>
public sealed class MarkdownComposer
{
    private const int DefaultMaxLength = 8000;

    /// <summary>Assigned once from DI - resolves <c>media:{hash}</c> image
    /// tokens to bytes for both preview and published views.</summary>
    public static CommunityMediaClientService? MediaClient { get; set; }

    /// <summary>File-picker used by the image button - set by the host so
    /// tests and alternate pickers can substitute it.</summary>
    public static Func<Task<byte[]?>>? ImagePicker { get; set; }

    /// <summary>PhotoStudio-backed picker targeted at a specific image preset -
    /// the studio enforces the target's aspect + output size. Hosts without a
    /// studio leave this null; callers fall back to <see cref="ImagePicker"/>.</summary>
    public static Func<Services.Mediator.PhotoStudioTarget, Task<byte[]?>>? StudioPicker { get; set; }

    /// <summary>Font sizes offered by the size flyout - all within
    /// <see cref="MarkdownRenderer.MaxSizePt"/>.</summary>
    private static readonly int[] SizePresets = [12, 14, 16, 18, 20, 24, 28, 32];

    /// <summary>Theme-compatible preset swatches for the color flyout.</summary>
    private static readonly (string Name, Vector4 Color)[] ColorPresets =
    [
        ("Red", Dalamud.Interface.Colors.ImGuiColors.DalamudRed),
        ("Orange", Dalamud.Interface.Colors.ImGuiColors.DalamudOrange),
        ("Yellow", Dalamud.Interface.Colors.ImGuiColors.DalamudYellow),
        ("Green", Dalamud.Interface.Colors.ImGuiColors.HealerGreen),
        ("Blue", Dalamud.Interface.Colors.ImGuiColors.TankBlue),
        ("Purple", Dalamud.Interface.Colors.ImGuiColors.ParsedPurple),
        ("White", Dalamud.Interface.Colors.ImGuiColors.DalamudWhite),
        ("Grey", Dalamud.Interface.Colors.ImGuiColors.DalamudGrey),
    ];

    /// <summary>Searchable mention candidate - the label is the user's
    /// recognizable alias, the detail disambiguates the source
    /// ("Syncshell member", "Friend", …); the uid is the stored identity.</summary>
    public sealed record MentionCandidate(string Uid, string Label, string Detail);

    /// <summary>Host-scoped member/friend candidates for `@` mentions - set by
    /// the surface that owns the composer (community ops provide members +
    /// friends); null hides the typeahead.</summary>
    public Func<IEnumerable<MentionCandidate>>? MentionProvider { get; set; }

    /// <summary>One queued buffer edit - executes inside the input callback
    /// where UTF-8 byte positions and <c>InsertChars</c>/<c>DeleteChars</c>
    /// apply directly to the live ImGui buffer.</summary>
    private delegate void ComposerEdit(ref ImGuiInputTextCallbackData data);

    /// <summary>Mutable callback context - the queue of pending edits plus
    /// the last-known caret/selection snapshot. Lives as a settable field so
    /// it can be handed to the callback by ref.</summary>
    private sealed class EditContext
    {
        public int CaretB = -1;
        public int SelStartB = -1;
        public int SelEndB = -1;
        public readonly Queue<ComposerEdit> Pending = new();
    }

    private string? _mentionQuery;
    private string? _mentionLastQuery;
    private string? _mentionDismissQuery;
    private int _mentionIndex;

    private readonly string _id;
    private bool _preview;
    private string _error = string.Empty;
    private Task<byte[]?>? _uploadTask;
    private int _refPickerTab;

    private EditContext _edit = new();
    private bool _refocus;
    private string _colorHex = "#ffffff";

    /// <summary>Set false by hosts that provide their own full preview
    /// surface - the inline toggle + preview block are suppressed.</summary>
    public bool ShowPreview { get; set; } = true;

    /// <summary>Plain-text mode (comments): no formatting toolbar, no
    /// preview toggle - the body is entered and rendered verbatim.</summary>
    public bool PlainText { get; set; }

    public MarkdownComposer(string id) => _id = id;

    /// <summary>Image-source hook for <see cref="MarkdownRenderer"/> - maps
    /// <c>media:{hash}</c> URLs onto the file-server cache.</summary>
    public static Func<string, byte[]?> MediaImageSource => url =>
        url.StartsWith("media:", StringComparison.Ordinal) && url.Length > 6
            ? MediaClient?.TryGetBytes(url[6..])
            : null;

    /// <summary>Draws toolbar + input, the input slot swapping in place
    /// to a rendered preview when the toolbar's eye toggle is on.
    /// <paramref name="body"/> is the bound text - edits write through.
    /// Returns the current validation error (empty when valid).</summary>
    public string Draw(string label, ref string body, int maxLength = DefaultMaxLength,
        float height = -1f)
    {
        var scale = SpheneUI.Scale;
        var h = height < 0f ? 110f * scale : height;
        var previewing = !PlainText && ShowPreview && _preview
            && !string.IsNullOrWhiteSpace(body);
        if (!PlainText)
        {
            DrawToolbar(scale, ref body, previewing);
        }
        if (!string.IsNullOrEmpty(_error))
        {
            SpheneUI.Text.Colored(_error, SpheneUI.Palette.Warning);
        }
        // Queued toolbar edits apply inside the input callback - force focus
        // this frame so the callback runs (it fires while the widget is
        // active) and the edit lands in the same frame as the click.
        if (_refocus)
        {
            _refocus = false;
            ImGui.SetKeyboardFocusHere();
        }
        if (previewing)
        {
            DrawPreviewPane(body, h);
        }
        else
        {
            var flags = ImGuiInputTextFlags.CallbackAlways;
            SpheneUI.Controls.InputTextMultiline(label, ref body, maxLength,
                new Vector2(-1f, h), flags, OnInputCallback, ref _edit,
                trailingWidth: 0f, preferredWidth: -1f);
            if (body.Length > maxLength - maxLength / 10)
            {
                SpheneUI.Text.Muted($"{body.Length}/{maxLength}");
            }
        }
        return Validate(body, maxLength);
    }

    /// <summary>Length/emptiness validation - the only client-side gate;
    /// content rules stay server-side.</summary>
    private static string Validate(string body, int maxLength)
        => body.Length > maxLength ? $"Body exceeds the {maxLength}-character limit." : string.Empty;

    private static int OnInputCallback(ref ImGuiInputTextCallbackData data,
        ref EditContext ctx)
    {
        while (ctx.Pending.Count > 0)
        {
            ctx.Pending.Dequeue()(ref data);
            data.BufDirty = 1;
        }
        ctx.SelStartB = data.SelectionStart;
        ctx.SelEndB = data.SelectionEnd;
        ctx.CaretB = data.CursorPos;
        return 0;
    }

    /// <summary>The byte range the next edit splices around: the live
    /// selection when one exists, else the caret.</summary>
    private (int Start, int End, bool HadSelection) EditRange()
    {
        var s = _edit.SelStartB >= 0 ? Math.Min(_edit.SelStartB, _edit.SelEndB) : -1;
        var e = _edit.SelStartB >= 0 ? Math.Max(_edit.SelStartB, _edit.SelEndB) : -1;
        if (s >= 0 && e > s) return (s, e, true);
        var caret = Math.Max(0, _edit.CaretB);
        return (caret, caret, false);
    }

    /// <summary>Inserts <paramref name="insert"/> at the caret or replaces the
    /// selection; the caret lands at <paramref name="caretOffsetInInsert"/>
    /// UTF-8 bytes inside the inserted text (end = default).</summary>
    private void QueueInsert(string insert, int caretOffsetInInsert = -1)
    {
        _edit.Pending.Enqueue((ref ImGuiInputTextCallbackData d) =>
        {
            var (s, e, _) = EditRange();
            var bounds = d.BufTextLen;
            s = Math.Clamp(s, 0, bounds);
            e = Math.Clamp(e, s, bounds);
            if (e > s) d.DeleteChars(s, e - s);
            d.InsertChars(s, insert);
            var caret = s + (caretOffsetInInsert < 0
                ? Utf8Len(insert) : caretOffsetInInsert);
            d.SelectionStart = d.SelectionEnd = d.CursorPos = caret;
        });
        _refocus = true;
    }

    /// <summary>Wraps the selection in <paramref name="prefix"/>/<paramref name="suffix"/>;
    /// without a selection inserts the empty pair with the caret between
    /// the markers.</summary>
    private void QueueWrap(string prefix, string suffix)
    {
        _edit.Pending.Enqueue((ref ImGuiInputTextCallbackData d) =>
        {
            var (s, e, had) = EditRange();
            var bounds = d.BufTextLen;
            s = Math.Clamp(s, 0, bounds);
            e = Math.Clamp(e, s, bounds);
            if (had)
            {
                d.InsertChars(e, suffix);
                d.InsertChars(s, prefix);
                d.SelectionStart = d.SelectionEnd = d.CursorPos =
                    e + Utf8Len(prefix) + Utf8Len(suffix);
            }
            else
            {
                d.InsertChars(s, prefix + suffix);
                d.SelectionStart = d.SelectionEnd = d.CursorPos = s + Utf8Len(prefix);
            }
        });
        _refocus = true;
    }

    private void QueueLink()
    {
        _edit.Pending.Enqueue((ref ImGuiInputTextCallbackData d) =>
        {
            var (s, e, had) = EditRange();
            var bounds = d.BufTextLen;
            s = Math.Clamp(s, 0, bounds);
            e = Math.Clamp(e, s, bounds);
            if (had)
            {
                // Selected text becomes the link label; caret waits in the URL.
                d.InsertChars(e, "]()");
                d.InsertChars(s, "[");
                d.SelectionStart = d.SelectionEnd = d.CursorPos = e + 3;
            }
            else
            {
                d.InsertChars(s, "[]()");
                d.SelectionStart = d.SelectionEnd = d.CursorPos = s + 1;
            }
        });
        _refocus = true;
    }

    private static int Utf8Len(string s) => System.Text.Encoding.UTF8.GetByteCount(s);

    /// <summary>Maps a UTF-8 byte offset onto a UTF-16 char index - surrogate
    /// pairs contribute four bytes, counted at the low surrogate.</summary>
    private static int Utf8OffsetToCharIndex(string s, int byteOffset)
    {
        if (byteOffset <= 0) return 0;
        var utf8 = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            utf8 += ch < 0x80 ? 1 : ch < 0x800 ? 2 : char.IsLowSurrogate(ch) ? 4
                : char.IsHighSurrogate(ch) ? 0 : 3;
            if (utf8 >= byteOffset) return i + 1;
        }
        return s.Length;
    }

    private void DrawToolbar(float scale, ref string body, bool previewing)
    {
        var gap = SpheneUI.Spacing.X2.Scaled;
        ImGui.PushID(_id);
        void WrapBtn(FontAwesomeIcon icon, string tip, string pre, string post)
        {
            if (SpheneUI.Icons.Button(icon))
            {
                QueueWrap(pre, post);
            }
            SpheneUI.AttachTooltip(tip);
            ImGui.SameLine(0f, gap);
        }
        void InsertBtn(FontAwesomeIcon icon, string tip, string insert, int caret = -1)
        {
            if (SpheneUI.Icons.Button(icon))
            {
                QueueInsert(insert, caret);
            }
            SpheneUI.AttachTooltip(tip);
            ImGui.SameLine(0f, gap);
        }

        // formatting writes through the input callback - inert while the
        // preview pane owns the slot
        using (ImRaii.Disabled(previewing))
        {
            WrapBtn(FontAwesomeIcon.Bold, "Bold", "**", "**");
            WrapBtn(FontAwesomeIcon.Italic, "Italic", "*", "*");
            WrapBtn(FontAwesomeIcon.Underline, "Underline", "__", "__");
            WrapBtn(FontAwesomeIcon.Strikethrough, "Strikethrough", "~~", "~~");
            WrapBtn(FontAwesomeIcon.EyeSlash, "Spoiler", "||", "||");
            InsertBtn(FontAwesomeIcon.ExclamationCircle, "Callout block",
                "\n::: info\nCallout\n:::\n", 10);
            InsertBtn(FontAwesomeIcon.Table, "Table",
                "\n| col | col |\n|---|---|\n| a | b |\n", 3);
            InsertBtn(FontAwesomeIcon.ListUl, "List", "\n- item\n- item\n", 3);
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Link))
            {
                QueueLink();
            }
            SpheneUI.AttachTooltip("Link (selection becomes the label)");
            ImGui.SameLine(0f, gap);
            if (SpheneUI.Icons.Button(FontAwesomeIcon.TextHeight))
            {
                SpheneUI.Popups.OpenFlyout("size-" + _id);
            }
            SpheneUI.AttachTooltip("Text size");
            ImGui.SameLine(0f, gap);
            var colorOpen = SpheneUI.Popups.IsFlyoutOpen("color-" + _id);
            if (SpheneUI.Controls.ColorSwatch("##mccolor" + _id,
                    ParseHex(_colorHex) ?? SpheneUI.Colors.Accent, selected: colorOpen))
            {
                SpheneUI.Popups.ToggleFlyout("color-" + _id);
            }
            SpheneUI.AttachTooltip("Text color");
            ImGui.SameLine(0f, gap);
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Image))
            {
                SpheneUI.Popups.OpenFlyout("img-" + _id);
            }
            SpheneUI.AttachTooltip("Image (uploads to the media store)");
            ImGui.SameLine(0f, gap);
            if (SpheneUI.Icons.Button(FontAwesomeIcon.At))
            {
                SpheneUI.Popups.OpenFlyout("ref-" + _id);
            }
            SpheneUI.AttachTooltip("Entity reference");
        }
        if (ShowPreview)
        {
            // mode swap docked at the toolbar row's right edge - the eye
            // switches the input slot to the preview pane and back
            var toggleIcon = previewing ? FontAwesomeIcon.Pen : FontAwesomeIcon.Eye;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX()
                + ImGui.GetContentRegionAvail().X
                - SpheneUI.Icons.ButtonSize(toggleIcon).X);
            using (ImRaii.Disabled(string.IsNullOrWhiteSpace(body)))
            {
                if (SpheneUI.Icons.Button(toggleIcon))
                {
                    _preview = !_preview;
                }
            }
            SpheneUI.AttachTooltip(previewing ? "Back to editing" : "Preview");
        }
        ImGui.NewLine();
        ImGui.PopID();

        ApplyPendingInserts();
        if (SpheneUI.Popups.BeginFlyout("img-" + _id))
        {
            DrawImageFlyout();
            SpheneUI.Popups.EndFlyout();
        }
        if (SpheneUI.Popups.BeginFlyout("ref-" + _id))
        {
            DrawRefFlyout();
            SpheneUI.Popups.EndFlyout();
        }
        if (SpheneUI.Popups.BeginFlyout("size-" + _id))
        {
            DrawSizeFlyout();
            SpheneUI.Popups.EndFlyout();
        }
        if (SpheneUI.Popups.BeginFlyout("color-" + _id))
        {
            DrawColorFlyout();
            SpheneUI.Popups.EndFlyout();
        }
        DrawMentionTypeahead(ref body);
        _ = scale;
    }

    /// <summary>Size presets - each wraps the selection in
    /// <c>&lt;size=N&gt;…&lt;/size&gt;</c> (or inserts an empty pair).</summary>
    private void DrawSizeFlyout()
    {
        SpheneUI.Text.Muted("Text size");
        foreach (var pt in SizePresets)
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.TextHeight,
                    $"{pt} pt", isInPopup: true))
            {
                QueueWrap($"<size={pt}>", "</size>");
                SpheneUI.Popups.CloseFlyout("size-" + _id);
            }
        }
    }

    /// <summary>Preset swatches plus a full picker - picks wrap the selection
    /// in <c>&lt;color=#rrggbb&gt;…&lt;/color&gt;</c>.</summary>
    private void DrawColorFlyout()
    {
        SpheneUI.Text.Muted("Text color");
        foreach (var (name, col) in ColorPresets)
        {
            if (SpheneUI.Controls.ColorSwatch($"##mcp{name}" + _id, col,
                    selected: false))
            {
                QueueWrap($"<color={HexOf(col)}>", "</color>");
                SpheneUI.Popups.CloseFlyout("color-" + _id);
            }
            SpheneUI.AttachTooltip(name);
            ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
        }
        ImGui.NewLine();
        var custom = ParseHex(_colorHex) ?? Vector4.One;
        if (SpheneUI.Controls.ColorPickerSwatch("##mcpick" + _id, ref custom))
        {
            _colorHex = HexOf(custom);
        }
        SpheneUI.AttachTooltip("Pick a custom color");
        ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Check, "Apply custom color",
                isInPopup: true))
        {
            QueueWrap($"<color={_colorHex}>", "</color>");
            SpheneUI.Popups.CloseFlyout("color-" + _id);
        }
    }

    private static string HexOf(Vector4 c)
        => $"#{(int)(Math.Clamp(c.X, 0f, 1f) * 255f + 0.5f):x2}{(int)(Math.Clamp(c.Y, 0f, 1f) * 255f + 0.5f):x2}{(int)(Math.Clamp(c.Z, 0f, 1f) * 255f + 0.5f):x2}";

    private static Vector4? ParseHex(string hex)
    {
        if (hex.Length == 7 && hex[0] == '#'
            && int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
                null, out var rgb))
        {
            return new Vector4(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f,
                (rgb & 0xff) / 255f, 1f);
        }
        return null;
    }

    private void DrawImageFlyout()
    {
        if (ImagePicker == null)
        {
            SpheneUI.Text.Muted("Image picking is unavailable in this surface.");
            return;
        }
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Upload, "Choose image…"))
        {
            _uploadTask ??= ImagePicker();
        }
        if (_uploadTask is { IsCompleted: true })
        {
            var bytes = _uploadTask.IsCompletedSuccessfully ? _uploadTask.Result : null;
            if (bytes == null)
            {
                _error = "Image upload failed or was cancelled.";
            }
            else
            {
                _uploadTask = null;
                _ = UploadAndInsertAsync(bytes);
            }
        }
        if (_uploadTask is { IsCompleted: false })
        {
            SpheneUI.Text.Muted("Reading image…");
        }
    }

    private async Task UploadAndInsertAsync(byte[] bytes)
    {
        if (MediaClient == null)
        {
            _error = "Media channel unavailable - the post keeps a link placeholder.";
            return;
        }
        var hash = await MediaClient.UploadImageAsync(bytes, "image/png",
            CancellationToken.None).ConfigureAwait(false);
        if (hash == null)
        {
            _error = "Upload to the media store failed.";
            return;
        }
        PendingInserts.TryAdd(_id, $"![image](media:{hash})");
    }

    /// <summary>Async completions (media upload) hand their text over here -
    /// consumed next frame into the caret-aware insert queue.</summary>
    private static readonly ConcurrentDictionary<string, string> PendingInserts
        = new(StringComparer.Ordinal);

    private void ApplyPendingInserts()
    {
        if (PendingInserts.TryRemove(_id, out var ins))
        {
            QueueInsert(ins);
        }
    }


    private void DrawRefFlyout()
    {
        var kinds = new[] { "User @uid", "Syncshell #gid", "Post post:id", "Chronicle blog:uid:id" };
        _refPickerTab = Math.Clamp(_refPickerTab, 0, kinds.Length - 1);
        for (var i = 0; i < kinds.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
            var active = _refPickerTab == i;
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Tag,
                    (active ? "· " : string.Empty) + kinds[i], isInPopup: true))
            {
                _refPickerTab = i;
            }
        }
        var hint = _refPickerTab switch
        {
            0 => "@uid or @alias",
            1 => "#GID or #alias",
            2 => "post:{id}",
            _ => "blog:{uid}:{id}",
        };
        var value = _refInput ?? string.Empty;
        SpheneUI.Controls.InputTextWithHint("##refval-" + _id, hint, ref value, 96,
            preferredWidth: 220f * SpheneUI.Scale);
        _refInput = value;
        ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
        var token = BuildToken(_refPickerTab, _refInput ?? string.Empty);
        using (ImRaii.Disabled(token == null))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Plus, "Insert"))
            {
                QueueInsert(" " + token);
                _refInput = null;
                SpheneUI.Popups.CloseFlyout("ref-" + _id);
            }
        }
    }

    /// <summary>`@` typeahead - a `@query` at the caret opens the candidate
    /// flyout; choosing a row rewrites the token as `@uid` (the same stable
    /// identity <see cref="BuildToken"/> produces and the server resolves on
    /// render). Mouse + typeahead filter; keyboard nav stays in the field.</summary>
    private void DrawMentionTypeahead(ref string body)
    {
        if (MentionProvider == null) return;
        var effective = body;
        var nav = 0;
        var confirm = false;
        var escape = false;
        if (_mentionQuery != null)
        {
            // Key interception while the popup is open - the multiline keeps
            // focus so typing still filters; Enter may already have appended
            // '\n', which is stripped from the matching tail on selection.
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) nav = -1;
            else if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) nav = 1;
            confirm = ImGui.IsKeyPressed(ImGuiKey.Enter)
                || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)
                || ImGui.IsKeyPressed(ImGuiKey.Tab);
            escape = ImGui.IsKeyPressed(ImGuiKey.Escape);
            if (confirm && effective.EndsWith('\n'))
            {
                effective = effective[..^1];
            }
        }
        var q = CaretMentionQuery(effective);
        if (escape && _mentionQuery != null)
        {
            _mentionDismissQuery = _mentionQuery;
        }
        if (q != null && !string.Equals(q, _mentionDismissQuery, StringComparison.Ordinal))
        {
            if (_mentionQuery == null)
            {
                SpheneUI.Popups.OpenFlyout("md-mention-" + _id);
            }
            _mentionQuery = q;
        }
        else if (_mentionQuery != null)
        {
            SpheneUI.Popups.CloseFlyout("md-mention-" + _id);
            _mentionQuery = null;
        }
        if (q == null)
        {
            _mentionDismissQuery = null;
        }
        if (!string.Equals(q, _mentionLastQuery, StringComparison.Ordinal))
        {
            _mentionIndex = 0;
            _mentionLastQuery = q;
        }
        if (_mentionQuery == null || !SpheneUI.Popups.BeginFlyout("md-mention-" + _id))
        {
            return;
        }
        SpheneUI.Text.Muted("Mention someone");
        var list = MentionProvider.Invoke()
            .Where(c => _mentionQuery.Length == 0
                || c.Label.Contains(_mentionQuery, StringComparison.OrdinalIgnoreCase))
            .Take(8).ToList();
        _mentionIndex = Math.Clamp(_mentionIndex + nav, 0, Math.Max(0, list.Count - 1));
        for (var i = 0; i < list.Count; i++)
        {
            var c = list[i];
            var highlighted = i == _mentionIndex;
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.At,
                    (highlighted ? "· " : string.Empty) + c.Label, isInPopup: true))
            {
                ApplyMention(ref body, effective, c.Uid);
                break;
            }
            ImGui.SameLine();
            SpheneUI.Text.Muted(c.Detail);
        }
        if (list.Count == 0)
        {
            SpheneUI.Text.Muted("No matching members or friends");
        }
        else if (confirm)
        {
            ApplyMention(ref body, effective, list[_mentionIndex].Uid);
        }
        SpheneUI.Popups.EndFlyout();
    }

    /// <summary>Replaces the `@query` at the caret with the resolved `@uid`
    /// token - keeps surrounding text and the caret-tail intact.</summary>
    private void ApplyMention(ref string body, string effective, string uid)
    {
        var caretIdx = _edit.CaretB >= 0
            ? Utf8OffsetToCharIndex(effective, _edit.CaretB) : effective.Length;
        caretIdx = Math.Min(caretIdx, effective.Length);
        var tokenStart = caretIdx - _mentionQuery!.Length - 1;
        body = effective[..Math.Max(0, tokenStart)] + "@" + uid + " "
            + effective[caretIdx..];
        _mentionQuery = null;
        _mentionLastQuery = null;
        SpheneUI.Popups.CloseFlyout("md-mention-" + _id);
    }

    /// <summary>The `@query` fragment ending at the caret - only fires on a
    /// whitespace-bounded `@` (emails/`@uid` inside text are inert).</summary>
    private string? CaretMentionQuery(string body)
    {
        var caretIdx = _edit.CaretB >= 0
            ? Utf8OffsetToCharIndex(body, _edit.CaretB) : body.Length;
        var upto = caretIdx >= body.Length ? body : body[..caretIdx];
        var i = upto.LastIndexOf('@');
        if (i < 0 || (i > 0 && !char.IsWhiteSpace(upto[i - 1])))
        {
            return null;
        }
        var q = upto[(i + 1)..];
        return q.Any(c => char.IsWhiteSpace(c) || c == '@') ? null : q;
    }

    private string? _refInput;

    private static string? BuildToken(int tab, string raw)
    {
        var v = raw.Trim();
        if (v.Length == 0) return null;
        return tab switch
        {
            0 => v.StartsWith('@') ? v : "@" + v,
            1 => v.StartsWith('#') ? v : "#" + v,
            2 => v.StartsWith("post:", StringComparison.Ordinal) ? v : "post:" + v,
            _ => v.StartsWith("blog:", StringComparison.Ordinal) ? v : "blog:" + v,
        };
    }

    /// <summary>The input slot's preview mode: a bordered pane of the same
    /// height rendering the markdown - the slot swaps, nothing renders
    /// underneath. <see cref="ImGuiCol.FrameBg"/> matches the input box.</summary>
    private void DrawPreviewPane(string body, float h)
    {
        var frameBg = ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg];
        using var bgScope = ImRaii.PushColor(ImGuiCol.ChildBg,
            ImGui.ColorConvertFloat4ToU32(frameBg));
        using var pane = ImRaii.Child($"##mdpreview-{_id}", new Vector2(-1f, h),
            border: true, ImGuiWindowFlags.None);
        if (pane.Success)
        {
            MarkdownRenderer.RenderMarkdown(body, 0, MediaImageSource);
        }
    }
}
