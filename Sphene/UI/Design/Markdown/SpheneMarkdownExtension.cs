using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Syntax.Inlines;

namespace Sphene.UI.Design;

/// <summary>Which Sphene tag a <see cref="ColorTagInline"/> container represents.</summary>
public enum SpheneTagKind
{
    Color,
    Underline,
    Size,
}

/// <summary>Inline container for the Sphene-specific tags
/// &lt;color=name|#hex&gt;...&lt;/color&gt;, &lt;u&gt;...&lt;/u&gt; and
/// &lt;size=N&gt;...&lt;/size&gt;. Produced by post-process pairing of
/// <see cref="SpheneTagLeaf"/> tokens - never emitted by the parser itself.</summary>
public sealed class ColorTagInline : ContainerInline
{
    public SpheneTagKind Kind { get; set; } = SpheneTagKind.Color;
    public string ColorArg { get; set; } = string.Empty;
    /// <summary>Requested point size for <see cref="SpheneTagKind.Size"/> tags -
    /// clamped at render to the supported font-handle range.</summary>
    public int SizePt { get; set; }
    public bool IsUnderline
    {
        get => Kind == SpheneTagKind.Underline;
        set => Kind = value ? SpheneTagKind.Underline : SpheneTagKind.Color;
    }
}

/// <summary>Unpaired open/close token for a Sphene tag. Flat in the inline
/// stream during parsing; the post-process pass either pairs it into a
/// <see cref="ColorTagInline"/> or leaves it as literal text.</summary>
public sealed class SpheneTagLeaf : LeafInline
{
    public SpheneTagKind Kind { get; set; }
    public bool IsClose { get; set; }
    public string ColorArg { get; set; } = string.Empty;
    public int SizePt { get; set; }
    public string Literal { get; set; } = string.Empty;
    public override string ToString() => Literal;
}

/// <summary>
/// Parses the Sphene inline tags &lt;color=...&gt;, &lt;size=...&gt;, &lt;u&gt; and
/// their closing counterparts into flat leaf tokens, then pairs them in a
/// post-process pass that runs after emphasis resolution.
///
/// Why not open containers at parse time: emphasis delimiter inlines are
/// themselves open containers while parsing, so any emphasis inside an open
/// tag container cascade-nests into the delimiters - emphasis never pairs and
/// the close tag can no longer find its container. Flat leaves sidestep the
/// container mechanics entirely; pairing afterwards mirrors how emphasis
/// itself works, so nesting like &lt;color=x&gt;~~**hi**~~&lt;/color&gt;
/// resolves correctly in any order.
/// </summary>
public sealed class SpheneTagInlineParser : InlineParser, IPostInlineProcessor
{
    public SpheneTagInlineParser()
    {
        OpeningCharacters = ['<'];
    }

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        if (MatchOpenTag(ref slice, "<color=", out var colorArg))
        {
            processor.Inline = new SpheneTagLeaf
            {
                Kind = SpheneTagKind.Color, ColorArg = colorArg,
                Literal = "<color=" + colorArg + ">"
            };
            return true;
        }
        var reset = slice.Start;
        if (MatchOpenTag(ref slice, "<size=", out var sizeArg)
            && int.TryParse(sizeArg, out var sizePt))
        {
            processor.Inline = new SpheneTagLeaf
            {
                Kind = SpheneTagKind.Size, SizePt = sizePt,
                Literal = "<size=" + sizeArg + ">"
            };
            return true;
        }
        slice.Start = reset;
        if (MatchLiteral(ref slice, "<u>"))
        {
            processor.Inline = new SpheneTagLeaf
            {
                Kind = SpheneTagKind.Underline, Literal = "<u>"
            };
            return true;
        }
        if (MatchLiteral(ref slice, "</u>"))
            return Close(processor, SpheneTagKind.Underline, "</u>");
        if (MatchLiteral(ref slice, "</color>"))
            return Close(processor, SpheneTagKind.Color, "</color>");
        if (MatchLiteral(ref slice, "</size>"))
            return Close(processor, SpheneTagKind.Size, "</size>");
        return false;
    }

    private static bool Close(InlineProcessor processor, SpheneTagKind kind, string literal)
    {
        processor.Inline = new SpheneTagLeaf { Kind = kind, IsClose = true, Literal = literal };
        return true;
    }

    /// <summary>Consumes "&lt;color=" plus the color name/hex up to '>'. </summary>
    private static bool MatchOpenTag(ref StringSlice slice, string prefix, out string arg)
    {
        arg = string.Empty;
        for (var i = 0; i < prefix.Length; i++)
        {
            if (slice.PeekCharExtra(i) != prefix[i] || slice.Start + i > slice.End)
                return false;
        }
        var start = slice.Start + prefix.Length;
        var end = start;
        while (end <= slice.End && slice.Text[end] != '>')
        {
            var c = slice.Text[end];
            if (!(char.IsLetterOrDigit(c) || c == '#' || c == '_'))
                return false;
            end++;
        }
        if (end > slice.End || end == start)
            return false;
        arg = slice.Text[start..end];
        slice.Start = end + 1;
        return true;
    }

    /// <summary>Consumes an exact tag literal at the slice position.</summary>
    private static bool MatchLiteral(ref StringSlice slice, string literal)
    {
        for (var i = 0; i < literal.Length; i++)
        {
            if (slice.Start + i > slice.End || slice.PeekCharExtra(i) != literal[i])
                return false;
        }
        slice.Start += literal.Length;
        return true;
    }

    /// <summary>
    /// Pairs leaf tokens into <see cref="ColorTagInline"/> containers. Runs
    /// after emphasis post-processing (registration order), so emphasis
    /// inside or around tags is already resolved; each pairing level operates
    /// on direct siblings only - crossed/malformed overlaps degrade to
    /// literal text instead of corrupting the tree. Idempotent.
    /// </summary>
    public bool PostProcess(InlineProcessor state, Inline? root, Inline? lastChild,
        int postInlineProcessorIndex, bool isFinalProcessing)
    {
        if (root is ContainerInline container)
            PairLevel(container);
        return true;
    }

    private static void PairLevel(ContainerInline container)
    {
        // Each successful pair restructures the level, so scan until fixpoint.
        while (TryPairOnce(container))
        {
            // restart after every restructure
        }
        var child = container.FirstChild;
        while (child != null)
        {
            if (child is ContainerInline ci)
                PairLevel(ci);
            child = child.NextSibling;
        }
    }

    /// <summary>Scans one level left-to-right; on the first close leaf with a
    /// matching unmatched open leaf, wraps the in-between siblings and
    /// reports progress. Opens skipped over by the match are dropped from the
    /// candidate set (their span crosses the pair - malformed).</summary>
    private static bool TryPairOnce(ContainerInline container)
    {
        var opens = new List<SpheneTagLeaf>();
        var node = container.FirstChild;
        while (node != null)
        {
            if (node is SpheneTagLeaf leaf)
            {
                if (!leaf.IsClose)
                {
                    opens.Add(leaf);
                }
                else
                {
                    for (var i = opens.Count - 1; i >= 0; i--)
                    {
                        if (opens[i].Kind != leaf.Kind)
                            continue;
                        var open = opens[i];
                        opens.RemoveRange(i, opens.Count - i);
                        Wrap(open, leaf);
                        return true;
                    }
                }
            }
            node = node.NextSibling;
        }
        return false;
    }

    /// <summary>Moves every sibling strictly between the two leaves into a new
    /// tag container placed at the open leaf's position, then removes both
    /// leaves. AppendChild detaches moved nodes from the shared parent.</summary>
    private static void Wrap(SpheneTagLeaf open, SpheneTagLeaf close)
    {
        var tag = new ColorTagInline
        {
            Kind = open.Kind, ColorArg = open.ColorArg, SizePt = open.SizePt,
            IsClosed = true
        };
        open.InsertAfter(tag);
        var node = tag.NextSibling;
        while (node != null && node != close)
        {
            var next = node.NextSibling;
            node.Remove();
            tag.AppendChild(node);
            node = next;
        }
        open.Remove();
        close.Remove();
    }
}

/// <summary>
/// Registers the Sphene tag parser and the Discord-style ||spoiler|| delimiter
/// (an emphasis delimiter - rendered as a click-to-reveal block, not emphasis).
/// </summary>
public sealed class SpheneMarkdownExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        var emphasis = pipeline.InlineParsers.Find<EmphasisInlineParser>();
        if (emphasis != null && !emphasis.HasEmphasisChar('|'))
        {
            emphasis.EmphasisDescriptors.Add(new EmphasisDescriptor('|', 2, 2, true));
        }

        // PipeTableParser claims '|' ahead of emphasis - without this reorder
        // ||spoiler|| never reaches EmphasisInlineParser. Single '|' runs are
        // below the descriptor's 2-char minimum and still fall through to the
        // table parser, so pipe tables are unaffected.
        var tableParser = pipeline.InlineParsers.Find<PipeTableParser>();
        if (emphasis != null && tableParser != null)
        {
            var emphasisIndex = pipeline.InlineParsers.IndexOf(emphasis);
            var tableIndex = pipeline.InlineParsers.IndexOf(tableParser);
            if (emphasisIndex > tableIndex)
            {
                pipeline.InlineParsers.RemoveAt(emphasisIndex);
                pipeline.InlineParsers.Insert(tableIndex, emphasis);
            }
        }

        // After emphasis in parser order: the inline '<' match never conflicts
        // (emphasis does not consume it), and post-processors run in parser
        // order - so tag pairing happens after emphasis resolution, which is
        // required for emphasis nested inside tags.
        if (!pipeline.InlineParsers.Contains<SpheneTagInlineParser>())
        {
            var insertAt = emphasis != null
                ? pipeline.InlineParsers.IndexOf(emphasis) + 1
                : 0;
            pipeline.InlineParsers.Insert(insertAt, new SpheneTagInlineParser());
        }
    }

    public void Setup(MarkdownPipeline pipeline, Markdig.Renderers.IMarkdownRenderer renderer)
    {
    }
}
