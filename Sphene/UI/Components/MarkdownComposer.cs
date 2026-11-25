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
/// The editor uses the input's <see cref="ImGuiInputTextFlags.CallbackAlways"/>
/// callback to capture the caret and selection for managed edits.
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
    /// ("Syncshell member", "Friend", ...); the uid is the stored identity.</summary>
    public sealed record MentionCandidate(string Uid, string Label, string Detail);

    /// <summary>Host-scoped member/friend candidates for `@` mentions - set by
    /// the surface that owns the composer (community ops provide members +
    /// friends); null hides the typeahead.</summary>
    public Func<IEnumerable<MentionCandidate>>? MentionProvider { get; set; }

    /// <summary>Mutable callback context - the queue of pending edits plus
    /// the last-known caret/selection snapshot. Lives as a settable field so
    /// it can be handed to the callback by ref.</summary>
    private sealed class EditContext
    {
        public MarkdownSelection Selection = MarkdownSelection.At(-1);
        /// <summary>Caret as an index into the model string - view offsets
        /// differ once placeholders (1 char) stand in for sequences (N chars).</summary>
        public int ModelCaret = -1;
        /// <summary>When &gt;= 0 the callback forces caret+selection to this
        /// UTF-8 byte offset once - markup edits set it so the caret lands behind
        /// the inserted markers after the managed splice.</summary>
        public int CaretTargetByte = -1;
        /// <summary>Placeholder codec: emoji occupy one BMP char each in the
        /// 16-bit ImWchar buffer - real sequences live only in the model.</summary>
        public readonly Helpers.Emoji.EmojiTextCodec Codec = new();
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
    private readonly Queue<string> _pendingInserts = new();
    private int _editEpoch;
    private (string Body, MarkdownSelection Selection) _colorTarget;
    private (string Body, MarkdownSelection Selection) _sizeTarget;
    private string _colorHex = "#ffffff";

    /// <summary>Undo/redo snapshots of the model text plus the view-space
    /// caret at snapshot time. Entries are pushed by <see cref="ApplySplice"/>
    /// only - plain typing stays owned by ImGui's native input undo; the two
    /// stacks interleave through the <see cref="_undoBaseline"/> heuristic.</summary>
    private const int MaxUndoEntries = 100;
    private readonly List<(string Text, int Caret)> _undoStack = new();
    private readonly List<(string Text, int Caret)> _redoStack = new();
    /// <summary>The model text as of the last splice/restore (or first frame).
    /// While the live body diverges, the user has typed since - ImGui's native
    /// undo owns Ctrl+Z for that typing, so the managed undo yields. Once the
    /// body matches again (native undo exhausted or no typing happened), the
    /// managed stack takes over. Epoch bumps wipe the native undo state, so
    /// right after a toolbar edit only the managed stack can answer.</summary>
    private string _undoBaseline = string.Empty;
    private bool _undoBaselineInit;
    private bool _inputWasActive;

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
        while (_pendingInserts.TryDequeue(out var insert))
            body = ApplyInsert(body, insert);
        if (!_undoBaselineInit)
        {
            _undoBaselineInit = true;
            _undoBaseline = body;
        }
        if (!previewing)
            body = HandleUndoKeys(body);
        var emoji = SpheneUI.EmojiPickerHook;
        if (previewing)
        {
            DrawPreviewPane(body, h);
        }
        else
        {
            var flags = ImGuiInputTextFlags.CallbackAlways;
            // 16-bit ImWchar buffer: emoji ride as placeholders, the model keeps real text.
            // The id carries the edit epoch - a managed splice bumps it, the
            // widget state re-initializes from the edited text instead of
            // resurrecting the stale pre-edit buffer over the model.
            var view = _edit.Codec.Encode(body);
            // Keep the picker open for consecutive picks. ApplySplice already advanced the
            // selection in the model, even while the search box owns keyboard focus.
            if (_refocus && !SpheneUI.Popups.IsFlyoutOpen($"emoji-{_id}"))
            {
                ImGui.SetKeyboardFocusHere();
                _refocus = false;
            }
            // The input owns the full row width (-1 fills) - PlainText
            // (comment) hosts draw the emoji trigger in their own action row
            // via DrawEmojiTrigger, next to submit/cancel; rich-text hosts
            // keep the toolbar trigger.
            SpheneUI.Controls.InputTextMultiline(label + "##me" + _editEpoch, ref view, maxLength,
                new Vector2(-1f, h), flags, OnInputCallback, ref _edit,
                encodedEmoji: _edit.Codec);
            _inputWasActive = ImGui.IsItemActive();
            var expanded = _edit.Codec.Expand(view);
            if (!string.Equals(expanded, body, StringComparison.Ordinal))
            {
                SpheneUI.LogDebug($"[ComposerDebug] roundtrip-write id={_id} epoch={_editEpoch} "
                    + $"model='{SanitizeForLog(body)}' view='{SanitizeForLog(view)}' "
                    + $"expanded='{SanitizeForLog(expanded)}'");
                body = expanded;
            }
            _edit.ModelCaret = _edit.Selection.Caret >= 0
                ? _edit.Codec.Expand(view[..Math.Min(_edit.Selection.Caret, view.Length)]).Length
                : -1;
            if (body.Length > maxLength - maxLength / 10)
            {
                SpheneUI.Text.Muted($"{body.Length}/{maxLength}");
            }
        }
        emoji?.Draw($"emoji-{_id}");
        return Validate(body, maxLength);
    }

    /// <summary>Length/emptiness validation - the only client-side gate;
    /// content rules stay server-side.</summary>
    private static string Validate(string body, int maxLength)
        => body.Length > maxLength ? $"Body exceeds the {maxLength}-character limit." : string.Empty;

    private static unsafe int OnInputCallback(ref ImGuiInputTextCallbackData data, ref EditContext ctx)
    {
        if (ctx.CaretTargetByte >= 0)
        {
            var target = Math.Min(ctx.CaretTargetByte, data.BufTextLen);
            data.SelectionStart = data.SelectionEnd = data.CursorPos = target;
            ctx.CaretTargetByte = -1;
        }
        var buffer = new ReadOnlySpan<byte>(data.Buf, data.BufTextLen);
        ctx.Selection = MarkdownSelection.FromCallback(buffer, data.SelectionStart, data.SelectionEnd, data.CursorPos);
        return 0;
    }

    /// <summary>Queues a caret insertion from an outside control (emoji
    /// picker). The pick arrives without access to the bound body, so the
    /// payload defers one frame and splices at the top of the next Draw -
    /// same managed path the toolbar edits use.</summary>
    public void QueueInsertText(string insert)
    {
        _pendingInserts.Enqueue(insert);
    }

    /// <summary>Draws the emoji-picker trigger for hosts that place it in
    /// their own action row (PlainText comment composers: next to the
    /// submit/cancel buttons) instead of inline right of the input. The
    /// flyout itself renders in <see cref="Draw"/>.</summary>
    public void DrawEmojiTrigger()
        => SpheneUI.EmojiPickerHook?.TriggerButton($"emoji-{_id}", QueueInsertText);

    private string ApplySplice(string body, (string View, int Caret) edit)
    {
        PushUndo(body);
        _edit.Selection = MarkdownSelection.At(edit.Caret);
        _edit.CaretTargetByte = MarkdownSelection.CallbackOffset(edit.View, edit.Caret);
        _refocus = true;
        // Give the edited buffer a fresh widget ID so ImGui reloads it from the model.
        _editEpoch++;
        _undoBaseline = _edit.Codec.Expand(edit.View);
        return _undoBaseline;
    }

    /// <summary>Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z while the input is focused.
    /// Only fires when the input was active last frame - focus state lags one
    /// frame, which is invisible in practice. Repeat is on so holding the key
    /// walks the history.</summary>
    private string HandleUndoKeys(string body)
    {
        if (!_inputWasActive)
            return body;
        var io = ImGui.GetIO();
        if (io.KeyCtrl && !io.KeyAlt && !io.KeyShift
            && ImGui.IsKeyPressed(ImGuiKey.Z, true)
            && TryUndo(body) is { } undone)
            return undone;
        if (((io.KeyCtrl && !io.KeyAlt && !io.KeyShift
                 && ImGui.IsKeyPressed(ImGuiKey.Y, true))
             || (io.KeyCtrl && io.KeyShift && !io.KeyAlt
                 && ImGui.IsKeyPressed(ImGuiKey.Z, true)))
            && TryRedo(body) is { } redone)
            return redone;
        return body;
    }

    private void PushUndo(string body)
    {
        if (_undoStack.Count > 0
            && string.Equals(_undoStack[^1].Text, body, StringComparison.Ordinal))
            return;
        _undoStack.Add((body, Math.Max(0, _edit.Selection.Caret)));
        if (_undoStack.Count > MaxUndoEntries)
            _undoStack.RemoveAt(0);
        _redoStack.Clear();
    }

    /// <summary>Pops one undo snapshot and restores it as a full-text
    /// replacement through the epoch mechanism. Yields (null) while the body
    /// diverges from the baseline - typing since the last splice belongs to
    /// ImGui's native undo, which fires on the same keypress.</summary>
    private string? TryUndo(string body)
    {
        if (_undoStack.Count == 0
            || !string.Equals(body, _undoBaseline, StringComparison.Ordinal))
            return null;
        var (text, caret) = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _redoStack.Add((body, Math.Max(0, _edit.Selection.Caret)));
        return Restore(text, caret);
    }

    private string? TryRedo(string body)
    {
        if (_redoStack.Count == 0
            || !string.Equals(body, _undoBaseline, StringComparison.Ordinal))
            return null;
        var (text, caret) = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.Add((body, Math.Max(0, _edit.Selection.Caret)));
        return Restore(text, caret);
    }

    /// <summary>Restores a snapshot as a full-text replacement. The caret
    /// target rides the snapshot's view-space position - re-encoding the
    /// restored model reproduces exactly that view.</summary>
    private string Restore(string text, int caret)
    {
        var view = _edit.Codec.Encode(text);
        _edit.Selection = MarkdownSelection.At(Math.Clamp(caret, 0, view.Length));
        _edit.CaretTargetByte = MarkdownSelection.CallbackOffset(view, _edit.Selection.Caret);
        _refocus = true;
        _editEpoch++;
        _undoBaseline = text;
        return text;
    }

    private static string SanitizeForLog(string text)
        => text.Length <= 40 ? text.Replace("\n", "\\n") : text[..40].Replace("\n", "\\n") + "...";

    /// <summary>Inserts <paramref name="insert"/> at the caret or replaces the
    /// selection; the caret lands at <paramref name="caretOffsetInInsert"/>
    /// chars inside the inserted text (end = default).</summary>
    private string ApplyInsert(string body, string insert, int caretOffsetInInsert = -1)
    {
        var encoded = _edit.Codec.Encode(insert);
        return ApplySplice(body, _edit.Selection.Replace(_edit.Codec.Encode(body), encoded,
            caretOffsetInInsert < 0 ? encoded.Length : caretOffsetInInsert));
    }

    private string ApplyWrap(string body, string prefix, string suffix, MarkdownSelection? target = null)
        => ApplySplice(body, (target ?? _edit.Selection).Wrap(_edit.Codec.Encode(body), prefix, suffix));

    private string ApplyLink(string body)
        => ApplySplice(body, _edit.Selection.Wrap(_edit.Codec.Encode(body), "[", "]()", caretInSuffix: 2));

    private void DrawToolbar(float scale, ref string body, bool previewing)
    {
        // ref params can't be captured by the local button functions - the
        // toolbar edits this captured copy synchronously and it writes back
        // to the caller's body at the end of the toolbar pass
        var text = body;
        var gap = SpheneUI.Spacing.X2.Scaled;
        ImGui.PushID(_id);
        void WrapBtn(FontAwesomeIcon icon, string tip, string pre, string post)
        {
            if (SpheneUI.Icons.Button(icon))
            {
                text = ApplyWrap(text, pre, post);
            }
            SpheneUI.AttachTooltip(tip);
            ImGui.SameLine(0f, gap);
        }
        void InsertBtn(FontAwesomeIcon icon, string tip, string insert, int caret = -1)
        {
            if (SpheneUI.Icons.Button(icon))
            {
                text = ApplyInsert(text, insert, caret);
            }
            SpheneUI.AttachTooltip(tip);
            ImGui.SameLine(0f, gap);
        }

        // Formatting controls share the last selection captured by the input.
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
                text = ApplyLink(text);
            }
            SpheneUI.AttachTooltip("Link (selection becomes the label)");
            ImGui.SameLine(0f, gap);
            if (SpheneUI.Icons.Button(FontAwesomeIcon.TextHeight))
            {
                _sizeTarget = (text, _edit.Selection);
                SpheneUI.Popups.OpenFlyout("size-" + _id);
            }
            SpheneUI.AttachTooltip("Text size");
            ImGui.SameLine(0f, gap);
            var colorOpen = SpheneUI.Popups.IsFlyoutOpen("color-" + _id);
            if (SpheneUI.Controls.ColorSwatch("##mccolor" + _id,
                    ParseHex(_colorHex) ?? SpheneUI.Colors.Accent, selected: colorOpen))
            {
                if (!colorOpen) _colorTarget = (text, _edit.Selection);
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
            ImGui.SameLine(0f, gap);
            SpheneUI.EmojiPickerHook?.TriggerButton($"emoji-{_id}", QueueInsertText);
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
            if (string.Equals(text, _sizeTarget.Body, StringComparison.Ordinal)) DrawSizeFlyout(ref text);
            else SpheneUI.Popups.CloseFlyout("size-" + _id);
            SpheneUI.Popups.EndFlyout();
        }
        if (SpheneUI.Popups.BeginFlyout("color-" + _id))
        {
            if (string.Equals(text, _colorTarget.Body, StringComparison.Ordinal)) DrawColorFlyout(ref text);
            else SpheneUI.Popups.CloseFlyout("color-" + _id);
            SpheneUI.Popups.EndFlyout();
        }
        DrawMentionTypeahead(ref text);
        body = text;
        _ = scale;
    }

    /// <summary>Size presets - each wraps the selection in
    /// <c>&lt;size=N&gt;...&lt;/size&gt;</c> (or inserts an empty pair).</summary>
    private void DrawSizeFlyout(ref string body)
    {
        SpheneUI.Text.Muted("Text size");
        foreach (var pt in SizePresets)
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.TextHeight,
                    $"{pt} pt", isInPopup: true))
            {
                body = ApplyWrap(body, $"<size={pt}>", "</size>", _sizeTarget.Selection);
                SpheneUI.Popups.CloseFlyout("size-" + _id);
            }
        }
    }

    /// <summary>Preset swatches plus a full picker - picks wrap the selection
    /// in <c>&lt;color=#rrggbb&gt;...&lt;/color&gt;</c>.</summary>
    private void DrawColorFlyout(ref string body)
    {
        SpheneUI.Text.Muted("Text color");
        foreach (var (name, col) in ColorPresets)
        {
            if (SpheneUI.Controls.ColorSwatch($"##mcp{name}" + _id, col,
                    selected: false))
            {
                body = ApplyWrap(body, $"<color={HexOf(col)}>", "</color>", _colorTarget.Selection);
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
            body = ApplyWrap(body, $"<color={_colorHex}>", "</color>", _colorTarget.Selection);
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
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Upload, "Choose image..."))
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
            SpheneUI.Text.Muted("Reading image...");
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
            QueueInsertText(ins);
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
                QueueInsertText(" " + token);
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
        var caretIdx = _edit.ModelCaret >= 0
            ? Math.Min(_edit.ModelCaret, effective.Length) : effective.Length;
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
        var caretIdx = _edit.ModelCaret >= 0
            ? Math.Min(_edit.ModelCaret, body.Length) : body.Length;
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
