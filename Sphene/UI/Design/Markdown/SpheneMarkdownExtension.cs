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
/// &lt;color=name|#hex&gt;…&lt;/color&gt;, &lt;u&gt;…&lt;/u&gt; and
/// &lt;size=N&gt;…&lt;/size&gt;.</summary>
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

/// <summary>
/// Parses the Sphene inline tags &lt;color=…&gt;, &lt;u&gt; and their closing
/// counterparts. Open tags emit an unclosed <see cref="ContainerInline"/> so
/// the regular inline pipeline nests subsequent content inside; close tags
/// flip <see cref="Inline.IsClosed"/> on the deepest matching open container
/// (same mechanics as the emphasis parser uses for open containers).
/// Must run before the autolink/html parsers so the tags win over raw HTML.
/// </summary>
public sealed class SpheneTagInlineParser : InlineParser
{
    public SpheneTagInlineParser()
    {
        OpeningCharacters = ['<'];
    }

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        if (MatchOpenTag(ref slice, "<color=", out var colorArg))
        {
            processor.Inline = new ColorTagInline { ColorArg = colorArg };
            return true;
        }
        var reset = slice.Start;
        if (MatchOpenTag(ref slice, "<size=", out var sizeArg)
            && int.TryParse(sizeArg, out var sizePt))
        {
            processor.Inline = new ColorTagInline
            {
                Kind = SpheneTagKind.Size, SizePt = sizePt
            };
            return true;
        }
        slice.Start = reset;
        if (MatchLiteral(ref slice, "<u>"))
        {
            processor.Inline = new ColorTagInline { IsUnderline = true };
            return true;
        }
        if (MatchLiteral(ref slice, "</u>"))
            return TryClose(processor, SpheneTagKind.Underline);
        if (MatchLiteral(ref slice, "</color>"))
            return TryClose(processor, SpheneTagKind.Color);
        if (MatchLiteral(ref slice, "</size>"))
            return TryClose(processor, SpheneTagKind.Size);
        return false;
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
    /// Closes the deepest open container when it matches the requested tag kind.
    /// Unclosed containers receive following inlines as children - closing here
    /// pops the effective scope. Returns false (literal text) when nothing
    /// matching is open.
    /// </summary>
    private static bool TryClose(InlineProcessor processor, SpheneTagKind kind)
    {
        var container = processor.Root;
        while (container?.LastChild is ContainerInline { IsClosed: false } open)
            container = open;
        if (container is not ColorTagInline target || target.Kind != kind)
            return false;
        target.IsClosed = true;
        processor.Inline = target;
        return true;
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
        if (!pipeline.InlineParsers.Contains<SpheneTagInlineParser>())
            pipeline.InlineParsers.Insert(0, new SpheneTagInlineParser());

        var emphasis = pipeline.InlineParsers.Find<EmphasisInlineParser>();
        if (emphasis == null)
            return;
        if (!emphasis.HasEmphasisChar('|'))
        {
            emphasis.EmphasisDescriptors.Add(new EmphasisDescriptor('|', 2, 2, true));
        }

        // PipeTableParser claims '|' ahead of emphasis - without this reorder
        // ||spoiler|| never reaches EmphasisInlineParser. Single '|' runs are
        // below the descriptor's 2-char minimum and still fall through to the
        // table parser, so pipe tables are unaffected.
        var tableParser = pipeline.InlineParsers.Find<PipeTableParser>();
        if (tableParser == null)
            return;
        var emphasisIndex = pipeline.InlineParsers.IndexOf(emphasis);
        var tableIndex = pipeline.InlineParsers.IndexOf(tableParser);
        if (emphasisIndex > tableIndex)
        {
            pipeline.InlineParsers.RemoveAt(emphasisIndex);
            pipeline.InlineParsers.Insert(tableIndex, emphasis);
        }
    }

    public void Setup(MarkdownPipeline pipeline, Markdig.Renderers.IMarkdownRenderer renderer)
    {
    }
}
