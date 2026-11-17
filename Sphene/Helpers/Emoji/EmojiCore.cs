using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace Sphene.Helpers.Emoji;

/// <summary>
/// Pure-codepoint emoji segmentation, grapheme-safe editing and inline
/// text+emoji line layout. No ImGui, no rendering - everything here is
/// unit-testable. The atlas/index side lives in SpheneUI.Emoji.
/// </summary>
public static class EmojiCore
{
    public enum RunKind : byte { Text, Emoji }

    /// <summary>A maximal run inside a UTF-16 string; offsets are char indices.</summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Auto)]
    public readonly record struct Run(RunKind Kind, int Start, int Length);

    /// <summary>A positioned atom on one laid-out line.</summary>
    public readonly record struct Atom(bool IsEmoji, string Text, float X, float Width);

    /// <summary>One wrapped line of atoms plus its total width.</summary>
    public sealed class Line
    {
        public List<Atom> Atoms { get; } = new();
        public float Width { get; set; }
    }

    public const int VariationSelector15 = 0xFE0E;
    public const int VariationSelector16 = 0xFE0F;
    public const int ZeroWidthJoiner = 0x200D;
    public const int KeycapMark = 0x20E3;
    public const int TagBase = 0xE0020;
    public const int TagEnd = 0xE007F;
    public const int SkinToneBase = 0x1F3FB;
    public const int SkinToneEnd = 0x1F3FF;
    public const int RegionalBase = 0x1F1E6;
    public const int RegionalEnd = 0x1F1FF;
    public const int CancelTag = 0xE007F;

    /// <summary>
    /// Codepoint ranges that carry Emoji=Yes / Emoji_Presentation. Kept
    /// deliberately tight: real emoji blocks plus the scattered BMP
    /// singletons (©, ®, ‼, ⁉, ™, ℹ, arrows, ⌚, ⏰, Ⓜ, ▶, 〰, ㊗…).
    /// </summary>
    private static readonly (uint Lo, uint Hi)[] EmojiRanges =
    {
        (0x00A9, 0x00A9), (0x00AE, 0x00AE), (0x203C, 0x203C), (0x2049, 0x2049),
        (0x2122, 0x2122), (0x2139, 0x2139), (0x2194, 0x21AA), (0x231A, 0x231B),
        (0x2328, 0x2328), (0x23CF, 0x23CF), (0x23E9, 0x23FA), (0x24C2, 0x24C2),
        (0x25AA, 0x25AB), (0x25B6, 0x25B6), (0x25C0, 0x25C0), (0x25FB, 0x25FE),
        (0x2600, 0x27BF), (0x2934, 0x2935), (0x2B05, 0x2B07), (0x2B1B, 0x2B1C),
        (0x2B50, 0x2B50), (0x2B55, 0x2B55), (0x3030, 0x3030), (0x303D, 0x303D),
        (0x3297, 0x3297), (0x3299, 0x3299), (0x1F000, 0x1FAFF),
        (0x1FC00, 0x1FFFD),
    };

    public static bool IsVariationSelector(int cp)
        => cp is VariationSelector15 or VariationSelector16;

    public static bool IsSkinTone(int cp) => cp is >= SkinToneBase and <= SkinToneEnd;

    public static bool IsRegionalIndicator(int cp) => cp is >= RegionalBase and <= RegionalEnd;

    public static bool IsTagChar(int cp) => cp is >= TagBase and <= TagEnd;

    public static bool IsKeycapBase(int cp)
        => cp is >= '0' and <= '9' or '#' or '*';

    /// <summary>Whether <paramref name="cp"/> can start/continue an emoji sequence.</summary>
    public static bool IsEmojiCodepoint(int cp, IReadOnlySet<int>? atlasBases = null)
    {
        if (atlasBases != null && atlasBases.Contains(cp))
        {
            return true;
        }
        foreach (var (lo, hi) in EmojiRanges)
        {
            if (cp >= lo && cp <= hi)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Decodes UTF-16 text into codepoints plus a codepoint→char-index map.</summary>
    public static int ToCodePoints(ReadOnlySpan<char> text, Span<int> cps, Span<int> map)
    {
        var n = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var start = i;
            int cp;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(c, text[i + 1]);
                i += 2;
            }
            else
            {
                cp = c;
                i++;
            }
            cps[n] = cp;
            map[n] = start;
            n++;
        }
        return n;
    }

    /// <summary>UTF-16 length occupied by <paramref name="cp"/>.</summary>
    public static int Utf16Length(int cp) => cp > 0xFFFF ? 2 : 1;

    /// <summary>
    /// Scans the whole string and merges adjacent codepoints that form a
    /// single emoji sequence into Emoji runs; everything else is Text.
    /// </summary>
    public static List<Run> Segment(string text, IReadOnlySet<int>? atlasBases = null)
        => Segment(text, null, atlasBases);

    /// <summary>
    /// Like <see cref="Segment(string, IReadOnlySet{int})"/>, but codepoints
    /// present in <paramref name="placeholders"/> also start a single-codepoint
    /// Emoji run. Used for the private-use chars an <c>EmojiTextCodec</c>
    /// substitutes into buffer views of native ImGui inputs.
    /// </summary>
    public static List<Run> Segment(string text,
        IReadOnlyDictionary<int, string>? placeholders, IReadOnlySet<int>? atlasBases = null)
    {
        var runs = new List<Run>();
        if (string.IsNullOrEmpty(text))
        {
            return runs;
        }
        var cps = new int[text.Length];
        var map = new int[text.Length];
        var count = ToCodePoints(text, cps, map);
        var i = 0;
        var textStart = 0;
        while (i < count)
        {
            var seqLen = placeholders != null && placeholders.ContainsKey(cps[i])
                ? 1
                : MatchSequenceLength(cps, count, i, atlasBases);
            if (seqLen > 0)
            {
                var charStart = map[i];
                if (charStart > textStart)
                {
                    runs.Add(new Run(RunKind.Text, textStart, charStart - textStart));
                }
                var end = map[i + seqLen - 1] + Utf16Length(cps[i + seqLen - 1]);
                runs.Add(new Run(RunKind.Emoji, charStart, end - charStart));
                i += seqLen;
                textStart = end;
            }
            else
            {
                i++;
            }
        }
        if (textStart < text.Length)
        {
            runs.Add(new Run(RunKind.Text, textStart, text.Length - textStart));
        }
        return runs;
    }

    /// <summary>
    /// Length in codepoints of the emoji sequence starting at
    /// <paramref name="cps"/>[<paramref name="i"/>], or 0 when none starts
    /// there. Implements: flag pair, keycap, base (+VS16? +tone?)
    /// (+ZWJ base…)*, tag-suffixed sequences.
    /// </summary>
    public static int MatchSequenceLength(int[] cps, int count, int i, IReadOnlySet<int>? atlasBases = null)
    {
        var c = cps[i];
        if (IsRegionalIndicator(c))
        {
            return i + 1 < count && IsRegionalIndicator(cps[i + 1]) ? 2 : 0;
        }
        if (IsKeycapBase(c))
        {
            var j = i + 1;
            if (j < count && IsVariationSelector(cps[j]))
            {
                j++;
            }
            return j < count && cps[j] == KeycapMark ? j + 1 - i : 0;
        }
        if (!IsEmojiCodepoint(c, atlasBases))
        {
            return 0;
        }
        var len = 1;
        var j2 = i + 1;
        // optional VS + tone on the base
        if (j2 < count && IsVariationSelector(cps[j2]))
        {
            j2++;
        }
        if (j2 < count && IsSkinTone(cps[j2]))
        {
            j2++;
        }
        len = j2 - i;
        while (j2 < count)
        {
            if (IsTagChar(cps[j2]))
            {
                while (j2 < count && IsTagChar(cps[j2]))
                {
                    j2++;
                }
                len = j2 - i;
                break;
            }
            if (cps[j2] != ZeroWidthJoiner || j2 + 1 >= count)
            {
                break;
            }
            var next = cps[j2 + 1];
            if (!IsEmojiCodepoint(next, atlasBases) && !IsKeycapBase(next))
            {
                break;
            }
            j2 += 2;
            if (j2 < count && IsVariationSelector(cps[j2]))
            {
                j2++;
            }
            if (j2 < count && IsSkinTone(cps[j2]))
            {
                j2++;
            }
            len = j2 - i;
        }
        return len;
    }

    /// <summary>
    /// Atlas-key for one emoji sequence: lowercase hex codepoints joined by
    /// '_', variation selectors stripped (matches Noto's filename scheme and
    /// the packed index format).
    /// </summary>
    public static string ToAtlasKey(string sequence)
    {
        var sb = new StringBuilder(sequence.Length);
        var i = 0;
        while (i < sequence.Length)
        {
            var c = sequence[i];
            int cp;
            if (char.IsHighSurrogate(c) && i + 1 < sequence.Length)
            {
                cp = char.ConvertToUtf32(c, sequence[i + 1]);
                i += 2;
            }
            else
            {
                cp = c;
                i++;
            }
            if (IsVariationSelector(cp))
            {
                continue;
            }
            if (sb.Length > 0)
            {
                sb.Append('_');
            }
            sb.Append(cp.ToString("x"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Demotes a sequence not present in the atlas toward a drawable one:
    /// first drop skin tones, then reduce to the leading emoji codepoint.
    /// Yields each candidate key (most-specific first), never the input.
    /// </summary>
    public static IEnumerable<string> DemotionKeys(string sequence)
    {
        var cps = new int[sequence.Length];
        var map = new int[sequence.Length];
        var count = ToCodePoints(sequence, cps, map);
        var anyTone = false;
        var kept = 0;
        for (var i = 0; i < count; i++)
        {
            if (IsSkinTone(cps[i]))
            {
                anyTone = true;
                continue;
            }
            cps[kept++] = cps[i];
        }
        if (anyTone && kept > 0)
        {
            yield return KeyOf(cps, kept);
        }
        var first = 0;
        while (first < kept && !IsEmojiCodepoint(cps[first]))
        {
            first++;
        }
        if (first < kept)
        {
            yield return KeyOf(cps, first + 1);
        }
    }

    private static string KeyOf(int[] cps, int count)
    {
        var sb = new StringBuilder(count * 6);
        for (var i = 0; i < count; i++)
        {
            if (IsVariationSelector(cps[i]))
            {
                continue;
            }
            if (sb.Length > 0)
            {
                sb.Append('_');
            }
            sb.Append(cps[i].ToString("x"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Start index of the grapheme cluster that ends at
    /// <paramref name="pos"/> - i.e. where a caret-safe backspace would cut.
    /// Walks back over VS, skin tones, tag chars, the combining keycap mark,
    /// ZWJ chains and regional-indicator pairs.
    /// </summary>
    public static int PrevClusterStart(string text, int pos)
    {
        if (pos <= 0)
        {
            return 0;
        }
        var cps = new int[text.Length];
        var map = new int[text.Length];
        var count = ToCodePoints(text, cps, map);
        var ci = count;
        for (var k = 0; k < count; k++)
        {
            if (map[k] + Utf16Length(cps[k]) >= pos)
            {
                ci = k;
                break;
            }
        }
        var start = ci;
        // fold trailing modifiers/selectors/tags into the cluster
        while (start > 0
            && (IsVariationSelector(cps[start]) || IsSkinTone(cps[start])
                || IsTagChar(cps[start]) || cps[start] == KeycapMark))
        {
            start--;
        }
        // fold a ZWJ chain (…base ZWJ ext ZWJ ext) to its first base
        while (start > 0)
        {
            var t = start - 1;
            while (t > 0 && (IsVariationSelector(cps[t]) || IsSkinTone(cps[t])))
            {
                t--;
            }
            if (cps[t] != ZeroWidthJoiner)
            {
                break;
            }
            t--;
            while (t > 0 && (IsVariationSelector(cps[t]) || IsSkinTone(cps[t])))
            {
                t--;
            }
            if (!IsEmojiCodepoint(cps[t]) && !IsKeycapBase(cps[t]) && !IsRegionalIndicator(cps[t]))
            {
                break;
            }
            start = t;
        }
        // fold regional-indicator pairs (odd count before -> pair with prev)
        if (IsRegionalIndicator(cps[start]) || (start > 0 && IsRegionalIndicator(cps[start - 1])))
        {
            var ri = 0;
            var t = start;
            while (t > 0 && IsRegionalIndicator(cps[t - 1]))
            {
                t--;
                ri++;
            }
            if ((ri & 1) == 1)
            {
                start = t;
            }
        }
        return map[start];
    }

    /// <summary>Inserts <paramref name="insert"/> at a caret position, replacing an optional selection.</summary>
    public static string InsertAt(string text, int pos, string insert, int selectionStart = -1, int selectionEnd = -1)
    {
        if (selectionStart >= 0 && selectionEnd > selectionStart)
        {
            return text[..selectionStart] + insert + text[selectionEnd..];
        }
        pos = Math.Clamp(pos, 0, text.Length);
        return string.Concat(text.AsSpan(0, pos), insert, text.AsSpan(pos));
    }

    /// <summary>
    /// Truncates to at most <paramref name="maxUtf16Length"/> UTF-16 units
    /// without splitting a grapheme cluster or an emoji sequence.
    /// </summary>
    public static string TruncateSafe(string text, int maxUtf16Length)
    {
        if (text.Length <= maxUtf16Length)
        {
            return text;
        }
        var cut = PrevClusterStart(text, maxUtf16Length + 1);
        if (cut <= 0)
        {
            cut = maxUtf16Length;
            if (cut < text.Length && char.IsLowSurrogate(text[cut]))
            {
                cut--;
            }
        }
        return cut > 0 ? text[..cut] : string.Empty;
    }

    /// <summary>
    /// Greedy word-wrap layout for mixed text/emoji content. Atoms are words
    /// (maximal non-whitespace text runs), single whitespace runs and emoji
    /// sequences. Emoji take a square slot of <paramref name="emojiSize"/>.
    /// <paramref name="measure"/> reports the width of a text atom.
    /// Lines break at whitespace and inside oversized words (ImGui parity).
    /// </summary>
    public static List<Line> Layout(string text, float wrapWidth, float emojiSize,
        Func<string, float> measure, IReadOnlySet<int>? atlasBases = null)
    {
        var lines = new List<Line>();
        var line = new Line();
        lines.Add(line);
        if (string.IsNullOrEmpty(text))
        {
            return lines;
        }

        var x = 0f;
        var spaceW = measure(" ");

        void NewLine()
        {
            line = new Line();
            lines.Add(line);
            x = 0f;
        }

        void EmitAtom(bool isEmoji, string atom, float w)
        {
            if (x > 0f && x + w > wrapWidth + 0.001f)
            {
                NewLine();
            }
            line.Atoms.Add(new Atom(isEmoji, atom, x, w));
            x += w;
            if (w > 0f)
            {
                line.Width = Math.Max(line.Width, x);
            }
        }

        foreach (var run in Segment(text, atlasBases))
        {
            var sub = text.Substring(run.Start, run.Length);
            if (run.Kind == RunKind.Emoji)
            {
                EmitAtom(true, sub, emojiSize);
                continue;
            }
            var i = 0;
            while (i < sub.Length)
            {
                var start = i;
                var ws = char.IsWhiteSpace(sub[i]);
                while (i < sub.Length && char.IsWhiteSpace(sub[i]) == ws)
                {
                    i++;
                }
                var atom = sub[start..i];
                if (ws)
                {
                    // whitespace is a break point: it joins the current line
                    // if anything fits, otherwise it simply wraps away
                    var w = atom.Contains('\n') ? 0f : spaceW * atom.Length;
                    if (atom.Contains('\n'))
                    {
                        var parts = atom.Split('\n');
                        for (var p = 0; p < parts.Length; p++)
                        {
                            if (p > 0)
                            {
                                NewLine();
                            }
                            if (parts[p].Length > 0)
                            {
                                x += spaceW * parts[p].Length;
                                line.Width = Math.Max(line.Width, x);
                            }
                        }
                    }
                    else
                    {
                        x += w;
                        line.Width = Math.Max(line.Width, x);
                    }
                    continue;
                }
                var width = measure(atom);
                if (x > 0f && x + width > wrapWidth + 0.001f)
                {
                    NewLine();
                }
                EmitWord(atom, width);
            }
        }
        return lines;

        void EmitWord(string word, float width)
        {
            if (width <= wrapWidth || word.Length == 1)
            {
                EmitAtom(false, word, width);
                return;
            }
            // split an over-long word like ImGui does: char by char
            var start = 0;
            var i = 1;
            while (i <= word.Length)
            {
                var w = measure(word[start..i]);
                if (x + w > wrapWidth + 0.001f)
                {
                    var piece = word[start..(i - 1)];
                    if (piece.Length > 0)
                    {
                        var pw = measure(piece);
                        line.Atoms.Add(new Atom(false, piece, x, pw));
                        line.Width = Math.Max(line.Width, x + pw);
                        NewLine();
                        start = i - 1;
                        continue;
                    }
                    if (x > 0f)
                    {
                        NewLine();
                        continue;
                    }
                }
                i++;
            }
            var rest = word[start..];
            var rw = measure(rest);
            line.Atoms.Add(new Atom(false, rest, x, rw));
            x += rw;
            line.Width = Math.Max(line.Width, x);
        }
    }
}
