using System;
using System.Collections.Generic;

namespace Sphene.Helpers.Emoji;

/// <summary>
/// Soft-wrap engine for the private-chat multiline composer. ImGui's
/// InputTextMultiline only breaks on hard newlines, so the composer writes
/// real '\n' characters into the edit buffer at the measured wrap points
/// and remembers their positions. On the way back out those same positions
/// are verified and stripped, so the model text never carries wrap noise
/// into the sent message. Wrap is word-greedy at the given pixel width,
/// matching the layout the widget renders.
/// </summary>
public static class ChatComposerWrap
{
    /// <summary>
    /// Builds the buffer view of <paramref name="view"/> with soft '\n'
    /// inserted at wrap boundaries for <paramref name="innerW"/>. Returns
    /// the wrapped string and the sorted buffer positions of the inserted
    /// breaks. <paramref name="measure"/> is the caller's text-measure hook
    /// (<c>ImGui.CalcTextSize</c> in production, a stub in tests).
    /// </summary>
    public static (string Buf, List<int> Soft) Wrap(string view, float innerW,
        Func<string, float> measure)
    {
        var soft = new List<int>();
        if (view.Length == 0 || innerW <= 0f)
        {
            return (view, soft);
        }
        var sb = new System.Text.StringBuilder(view.Length + 16);
        // position inside the produced buffer where a '\n' lands = sb.Length
        var src = 0;
        while (src <= view.Length)
        {
            var nl = view.IndexOf('\n', src);
            var end = nl < 0 ? view.Length : nl;
            WrapSegment(view, src, end, innerW, measure, sb, soft);
            if (nl < 0)
            {
                break;
            }
            sb.Append('\n');
            src = nl + 1;
        }
        return (sb.ToString(), soft);
    }

    /// <summary>Wraps one hard-line span [start,end) of the source text.
    /// Breaks land at the last space; a space consumed by a break is
    /// dropped (standard word-wrap behavior). A word longer than the line
    /// splits mid-word.</summary>
    private static void WrapSegment(string view, int start, int end, float innerW,
        Func<string, float> measure, System.Text.StringBuilder sb, List<int> soft)
    {
        var x = 0f;
        var lastSpaceOut = -1;
        var lastSpaceSrc = -1;
        var i = start;
        while (i < end)
        {
            var c = view[i];
            var cw = measure(c == ' ' ? " " : view.Substring(i, 1));
            if (x + cw > innerW && x > 0f)
            {
                if (lastSpaceOut >= 0)
                {
                    // rewind to the space, drop it and everything after,
                    // then continue that tail on the next line
                    sb.Remove(lastSpaceOut, sb.Length - lastSpaceOut);
                    soft.Add(sb.Length);
                    sb.Append('\n');
                    i = lastSpaceSrc + 1;
                    x = 0f;
                    lastSpaceOut = -1;
                    continue;
                }
                soft.Add(sb.Length);
                sb.Append('\n');
                x = 0f;
            }
            if (c == ' ')
            {
                lastSpaceOut = sb.Length;
                lastSpaceSrc = i;
            }
            sb.Append(c);
            x += cw;
            i++;
        }
    }

    /// <summary>
    /// Recovers the unwrapped view from the edited buffer: the soft positions
    /// recorded for the submitted buffer are verified against
    /// <paramref name="buf"/> after locating the user's edit region via a
    /// common prefix/suffix diff. Verified positions are stripped;
    /// anything else (including user-typed newlines) survives.
    /// </summary>
    public static string Unwrap(string submitted, string buf, IReadOnlyList<int> soft)
    {
        if (soft.Count == 0 || string.Equals(submitted, buf, StringComparison.Ordinal))
        {
            return buf;
        }
        // find the edited span between common prefix and common suffix
        var p = 0;
        var pMax = Math.Min(submitted.Length, buf.Length);
        while (p < pMax && submitted[p] == buf[p])
        {
            p++;
        }
        var s = 0;
        var sMax = Math.Min(submitted.Length - p, buf.Length - p);
        while (s < sMax && submitted[submitted.Length - 1 - s] == buf[buf.Length - 1 - s])
        {
            s++;
        }
        var delta = buf.Length - submitted.Length;
        var sb = new System.Text.StringBuilder(buf.Length);
        // soft[i] maps to: unchanged when left of the edit, shifted by the
        // length delta when right of it; inside the region it can only be
        // checked at the naive shifted spot
        var drop = new HashSet<int>();
        foreach (var pos in soft)
        {
            var mapped = pos < p ? pos : pos + delta;
            if (mapped >= 0 && mapped < buf.Length && buf[mapped] == '\n')
            {
                drop.Add(mapped);
            }
        }
        for (var i = 0; i < buf.Length; i++)
        {
            if (!drop.Contains(i))
            {
                sb.Append(buf[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Remaps a caret offset from the old wrapped buffer into the new one so
    /// the caret keeps pointing at the same logical character across a
    /// rewrap. Offsets are buffer positions (ImWchar units = chars here).
    /// </summary>
    public static int RemapCaret(int caretOld, IReadOnlyList<int> oldSoft,
        IReadOnlyList<int> newSoft)
    {
        var logical = caretOld;
        foreach (var pos in oldSoft)
        {
            if (pos < caretOld)
            {
                logical--;
            }
        }
        var mapped = logical;
        foreach (var pos in newSoft)
        {
            if (pos <= mapped)
            {
                mapped++;
            }
        }
        return mapped;
    }

    /// <summary>Line count of a wrapped buffer (1 + newline count).</summary>
    public static int Lines(string wrapped)
    {
        var n = 1;
        foreach (var c in wrapped)
        {
            if (c == '\n')
            {
                n++;
            }
        }
        return n;
    }
}
