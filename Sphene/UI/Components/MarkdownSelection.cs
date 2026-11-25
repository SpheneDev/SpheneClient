using System.Runtime.InteropServices;
using System.Text;

namespace Sphene.UI.Components;

/// <summary>Selection in the emoji-encoded editor view. Only the ImGui callback uses UTF-8 byte offsets.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct MarkdownSelection(int Start, int End, int Caret)
{
    public static MarkdownSelection At(int caret) => new(caret, caret, caret);

    public static MarkdownSelection FromCallback(ReadOnlySpan<byte> buffer, int anchor, int end, int caret)
    {
        int cursorIndex = ViewIndex(buffer, caret);
        if (anchor == end) return At(cursorIndex);
        int startIndex = anchor == caret ? cursorIndex : ViewIndex(buffer, anchor);
        int endIndex = end == caret ? cursorIndex : ViewIndex(buffer, end);
        return new(Math.Min(startIndex, endIndex), Math.Max(startIndex, endIndex), cursorIndex);
    }

    private static int ViewIndex(ReadOnlySpan<byte> buffer, int offset)
        => Encoding.UTF8.GetCharCount(buffer[..Math.Clamp(offset, 0, buffer.Length)]);

    public static int CallbackOffset(string view, int index)
        => Encoding.UTF8.GetByteCount(view.AsSpan(0, Math.Clamp(index, 0, view.Length)));

    public (string View, int Caret) Replace(string view, string insert, int caretInInsert)
    {
        int start = Math.Clamp(Start, 0, view.Length);
        int end = Math.Clamp(End, start, view.Length);
        return (string.Concat(view.AsSpan(0, start), insert, view.AsSpan(end)),
            start + Math.Clamp(caretInInsert, 0, insert.Length));
    }

    public (string View, int Caret) Wrap(string view, string prefix, string suffix, int? caretInSuffix = null)
    {
        int start = Math.Clamp(Start, 0, view.Length);
        int end = Math.Clamp(End, start, view.Length);
        string insert = string.Concat(prefix, view.AsSpan(start, end - start), suffix);
        int caret = start == end ? prefix.Length : prefix.Length + end - start + (caretInSuffix ?? suffix.Length);
        return Replace(view, insert, caret);
    }
}
