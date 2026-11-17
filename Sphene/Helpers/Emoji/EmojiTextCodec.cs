using System;
using System.Collections.Generic;
using System.Text;

namespace Sphene.Helpers.Emoji;

/// <summary>
/// Maps emoji sequences to BMP private-use placeholder codepoints for
/// ImGui text inputs. The native InputText keeps a 16-bit ImWchar edit
/// buffer - any astral codepoint (virtually every emoji) is truncated to a
/// bogus BMP char when it round-trips TextW→TextA, so the raw sequence can
/// never ride the buffer. Instead the buffer view carries one BMP
/// placeholder per emoji; the owning control converts the model string to
/// the buffer view (<see cref="Encode"/>) before the widget call and back
/// (<see cref="Expand"/>) after it, so model text always holds the real
/// sequence while every buffer position stays a single ImWchar.
/// The mapping is intentionally per-input-instance: placeholders are a
/// local encoding, never part of stored or transmitted content.
/// </summary>
public sealed class EmojiTextCodec
{
    public const int PlaceholderStart = 0xE000;
    public const int PlaceholderEnd = 0xF8FF;

    private readonly Dictionary<string, int> _bySeq = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _byPua = new();
    private int _next = PlaceholderStart;

    /// <summary>Placeholder codepoint → sequence for every assignment this
    /// codec made - drives placeholder-aware segmentation in renderers.</summary>
    public IReadOnlyDictionary<int, string> PlaceholderMap => _byPua;

    /// <summary>Sequence behind <paramref name="cp"/>, or null when it is not
    /// a placeholder this codec assigned.</summary>
    public bool TryResolve(int cp, out string sequence)
        => _byPua.TryGetValue(cp, out sequence!);

    /// <summary>Placeholder codepoint for <paramref name="sequence"/>, or
    /// -1 when the private-use range is exhausted (caller falls back to the
    /// literal sequence - the pre-codec native behavior).</summary>
    public int PlaceholderFor(string sequence)
    {
        if (_bySeq.TryGetValue(sequence, out var cp))
        {
            return cp;
        }
        if (_next > PlaceholderEnd)
        {
            return -1;
        }
        cp = _next++;
        _bySeq[sequence] = cp;
        _byPua[cp] = sequence;
        return cp;
    }

    /// <summary>Buffer view: every emoji sequence becomes its placeholder.</summary>
    public string Encode(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        var runs = EmojiCore.Segment(text);
        StringBuilder? sb = null;
        var pos = 0;
        foreach (var run in runs)
        {
            if (run.Kind == EmojiCore.RunKind.Emoji)
            {
                var cp = PlaceholderFor(text.Substring(run.Start, run.Length));
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, pos, run.Start - pos);
                sb.Append(cp >= 0 ? (char)cp : text.Substring(run.Start, run.Length));
                pos = run.Start + run.Length;
            }
        }
        if (sb == null)
        {
            return text;
        }
        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    /// <summary>Model text: every assigned placeholder becomes its sequence.</summary>
    public string Expand(string view)
    {
        if (string.IsNullOrEmpty(view) || _byPua.Count == 0)
        {
            return view;
        }
        StringBuilder? sb = null;
        var last = 0;
        for (var i = 0; i < view.Length; i++)
        {
            if (_byPua.TryGetValue(view[i], out var seq))
            {
                sb ??= new StringBuilder(view.Length);
                sb.Append(view, last, i - last);
                sb.Append(seq);
                last = i + 1;
            }
        }
        if (sb == null)
        {
            return view;
        }
        sb.Append(view, last, view.Length - last);
        return sb.ToString();
    }
}
