using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace Sphene.Helpers.Emoji;

/// <summary>
/// Pending-emoji queue for a plain ImGui input: the picker enqueues a
/// sequence, the input's <see cref="ImGuiInputTextFlags.CallbackAlways"/>
/// handler splices it at the live caret (replacing the selection) the next
/// frame. Same mechanism <c>MarkdownComposer</c> uses for its toolbar
/// edits. Callback positions are ImWchar (UTF-16-unit) offsets into the
/// edit buffer - every inserted emoji is first encoded through
/// <see cref="Codec"/> so it occupies exactly one BMP placeholder char in
/// the 16-bit native buffer.
/// </summary>
public sealed class EmojiEditQueue
{
    private readonly Queue<string> _pending = new();
    private int _caretB = -1;
    private int _selStartB = -1;
    private int _selEndB = -1;

    /// <summary>Placeholder codec that keeps this input's emoji as single
    /// BMP chars in the native buffer. The owning control encodes the model
    /// text into the buffer view and expands it back after the widget.</summary>
    public EmojiTextCodec Codec { get; } = new();

    /// <summary>Set when a pick arrived - the host focuses the input so the
    /// callback fires this frame.</summary>
    public bool Refocus { get; private set; }

    /// <summary>Last seen caret position in ImWchar units (-1 before the
    /// first callback) - the emoji overlay mirrors ImGui's horizontal scroll from it.
    /// Buffer content is BMP-only, so this is also the char index into the view.</summary>
    public int CaretCharOffset => _caretB;

    /// <summary>Estimated single-line horizontal scroll in px, kept by the
    /// emoji overlay so sprite positions track the widget's own layout.</summary>
    public float ScrollEst { get; set; }

    /// <summary>Estimated multiline vertical scroll in px - the overlay's
    /// caret-visibility mirror when the native child scroll can't be read.</summary>
    public float ScrollEstY { get; set; }

    /// <summary>Scroll captured inside the callback - for the multiline
    /// child frame this is the exact native value incl. wheel input.
    /// ScrollMax stays 0 when the callback's window isn't the scrollable
    /// child, which is how the overlay picks mirror vs native.</summary>
    public float NativeScrollY { get; private set; }
    public float NativeScrollMaxY { get; private set; }

    /// <summary>Soft-wrap break set of the buffer last submitted by the
    /// wrap-aware composer control.</summary>
    internal List<int> WrapSoft { get; set; } = new();

    /// <summary>When &gt;= 0 the callback forces the caret to this buffer
    /// offset - the control sets it when a rewrap shifted soft breaks
    /// under a numerically preserved caret.</summary>
    internal int WrapCaretTarget = -1;

    public void Enqueue(string text)
    {
        _pending.Enqueue(text);
        Refocus = true;
    }

    /// <summary>Consumes the refocus flag (host calls SetKeyboardFocusHere).</summary>
    public bool ConsumeRefocus()
    {
        var r = Refocus;
        Refocus = false;
        return r;
    }

    /// <summary>CallbackAlways handler for
    /// <c>Controls.InputTextWithHint&lt;T&gt;/InputTextMultiline&lt;T&gt;</c>.</summary>
    public static int OnCallback(ref ImGuiInputTextCallbackData data,
        ref EmojiEditQueue ctx)
    {
        // for a multiline input the callback runs inside the child frame
        // window, so GetScrollY reads its real scroll offset
        ctx.NativeScrollY = ImGui.GetScrollY();
        ctx.NativeScrollMaxY = ImGui.GetScrollMaxY();
        if (ctx.WrapCaretTarget >= 0)
        {
            var t = Math.Min(ctx.WrapCaretTarget, data.BufTextLen);
            data.SelectionStart = data.SelectionEnd = data.CursorPos = t;
            ctx.WrapCaretTarget = -1;
        }
        while (ctx._pending.Count > 0)
        {
            var insert = ctx._pending.Dequeue();
            var s = ctx._selStartB >= 0
                ? Math.Min(ctx._selStartB, ctx._selEndB) : -1;
            var e = ctx._selStartB >= 0
                ? Math.Max(ctx._selStartB, ctx._selEndB) : -1;
            if (s < 0 || e <= s)
            {
                s = e = Math.Max(0, ctx._caretB);
            }
            var bounds = data.BufTextLen;
            s = Math.Clamp(s, 0, bounds);
            e = Math.Clamp(e, s, bounds);
            if (e > s)
            {
                data.DeleteChars(s, e - s);
            }
            var encoded = ctx.Codec.Encode(insert);
            data.InsertChars(s, encoded);
            // CursorPos/Selection count ImWchar (UTF-16) units - the encoded
            // insert is BMP-only, so its UTF-16 length is the unit count.
            var caret = s + encoded.Length;
            data.SelectionStart = data.SelectionEnd = data.CursorPos = caret;
            data.BufDirty = 1;
        }
        ctx._selStartB = data.SelectionStart;
        ctx._selEndB = data.SelectionEnd;
        ctx._caretB = data.CursorPos;
        return 0;
    }
}
