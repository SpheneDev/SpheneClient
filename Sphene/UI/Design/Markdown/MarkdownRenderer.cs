using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Markdig;
using Markdig.Extensions.CustomContainers;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Sphene.UI.Design;

/// <summary>
/// Renders Markdown through Markdig (CommonMark + GFM pipe tables, task lists,
/// autolinks, footnotes, fenced code, strikethrough) plus Sphene extensions:
/// &lt;color=...&gt;/&lt;u&gt; tags, ||spoilers|| (click to reveal), __underline__,
/// and ::: fenced containers - <c>:::callout info|warning|danger|success</c>
/// renders an accent-striped block, <c>:::side img="url" side="left|right" width="38"</c>
/// renders an image column beside the text column, any other name renders a
/// bordered box. Raw HTML is never executed - it renders as muted literal text.
/// Parsed documents are cached per body text so a redraw does not reparse.
/// </summary>
public static class MarkdownRenderer
{
    private const int DocCacheLimit = 24;

    /// <summary>Supported &lt;size=N&gt; range - clamps inside the writer so
    /// out-of-range markup degrades instead of breaking the layout.</summary>
    internal const int MinSizePt = 8;
    internal const int MaxSizePt = 32;

    private static readonly Dictionary<string, Vector4> ColorMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "red", ImGuiColors.DalamudRed },
        { "green", ImGuiColors.HealerGreen },
        { "blue", ImGuiColors.TankBlue },
        { "yellow", ImGuiColors.DalamudYellow },
        { "orange", ImGuiColors.DalamudOrange },
        { "purple", ImGuiColors.ParsedPurple },
        { "white", ImGuiColors.DalamudWhite },
        { "grey", ImGuiColors.DalamudGrey },
        { "gray", ImGuiColors.DalamudGrey }
    };

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseFootnotes()
        .UseCustomContainers()
        .UseMathematics()
        .UseSoftlineBreakAsHardlineBreak()
        .Use<SpheneMarkdownExtension>()
        .Build();

    private static readonly Dictionary<string, MarkdownDocument> DocCache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> RevealedSpoilers = new(StringComparer.Ordinal);

    private sealed class RenderContext
    {
        public MarkdownInlineWriter Writer = null!;
        public Func<string, byte[]?>? ImageSource;
        public float RightBound;
    }

    /// <summary>
    /// Renders markdown. <paramref name="wrapPos"/> is an absolute X bound
    /// (same semantics as ImGui.PushTextWrapPos; 0 = content region edge).
    /// <paramref name="imageSource"/> resolves image URLs to bytes lazily -
    /// return null while a fetch is in flight and the block paints a
    /// placeholder until the cache lands.
    /// </summary>
    public static void RenderMarkdown(string text, float wrapPos = 0,
        Func<string, byte[]?>? imageSource = null)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var doc = GetDocument(text);
        if (doc == null)
            return;

        var ctx = new RenderContext
        {
            ImageSource = imageSource,
            RightBound = ResolveRightBound(wrapPos)
        };
        ctx.Writer = new MarkdownInlineWriter((url, alt) => RenderImageBlock(url, alt, ctx));

        FootnoteRefScroll.Clear();
        FootnoteDefScroll.Clear();
        using var itemSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        RenderBlocks(doc, ctx);
    }

    /// <summary>
    /// Content-space Y offsets for footnote reference markers - populated as
    /// they render; footnote back-links scroll here on click.
    /// </summary>
    internal static readonly Dictionary<int, float> FootnoteRefScroll = new();

    /// <summary>
    /// Content-space Y offsets for footnote definitions in the end-of-document
    /// section - reference markers scroll here on click.
    /// </summary>
    internal static readonly Dictionary<int, float> FootnoteDefScroll = new();

    /// <summary>
    /// Resolves the absolute screen-X wrap bound. <paramref name="wrapPos"/>
    /// keeps the legacy semantic: an absolute X bound, 0 = cursor + remaining
    /// content width. The result is always clamped to the design-system
    /// right bound so content inside cards respects their inner edge.
    /// </summary>
    private static float ResolveRightBound(float wrapPos)
    {
        var bound = wrapPos > 0f
            ? wrapPos
            : ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var designBound = SpheneUI.Layout.ContentRightBound;
        return designBound > 0f ? MathF.Min(bound, designBound) : bound;
    }

    /// <summary>Remaining content width, clamped to the renderer's right bound.</summary>
    private static float AvailWidth(RenderContext ctx)
    {
        var avail = ImGui.GetContentRegionAvail().X;
        var toBound = ctx.RightBound - ImGui.GetCursorScreenPos().X;
        return MathF.Max(0f, MathF.Min(avail, toBound));
    }

    internal static bool IsSpoilerRevealed(string key) => RevealedSpoilers.Contains(key);

    internal static void ToggleSpoiler(string key)
    {
        if (!RevealedSpoilers.Remove(key))
            RevealedSpoilers.Add(key);
    }

    /// <summary>Fetch-safe URLs - http/https only. Gate for image loads.</summary>
    internal static bool IsSafeUrl(string? url)
        => url != null
           && Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
               || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal));

    /// <summary>
    /// Clickable link URLs - http/https plus mailto: and tel: (both handed to
    /// the OS handler via <c>Util.OpenLink</c>). Image fetches still use
    /// <see cref="IsSafeUrl"/>.
    /// </summary>
    internal static bool IsSafeLinkUrl(string? url)
    {
        if (IsSafeUrl(url))
            return true;
        return url != null
               && Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && (string.Equals(uri.Scheme, "mailto", StringComparison.Ordinal)
                   || string.Equals(uri.Scheme, "tel", StringComparison.Ordinal));
    }

    internal static Vector4? ResolveColorArg(string arg)
    {
        if (arg.StartsWith('#'))
            return ParseHexColor(arg);
        return ColorMap.TryGetValue(arg, out var col) ? col : null;
    }

    /// <summary>
    /// Flattens markdown to plain text for excerpts, tooltips and teasers -
    /// same parser as <see cref="RenderMarkdown"/>, so Sphene tags, spoilers
    /// and emphasis resolve before flattening. One line per block.
    /// </summary>
    public static string ToPlainText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var doc = GetDocument(text);
        if (doc == null)
            return string.Empty;
        var sb = new System.Text.StringBuilder(text.Length);
        CollectPlainBlocks(doc, sb);
        return sb.ToString().Trim();
    }

    private static void CollectPlainBlocks(ContainerBlock container, System.Text.StringBuilder sb)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case LeafBlock { Inline: { } inline }:
                {
                    var t = MarkdownInlineWriter.CollectText(inline).Trim();
                    if (t.Length > 0)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(t);
                    }
                    break;
                }
                case CodeBlock code:
                {
                    var t = string.Join('\n', code.Lines.Lines.Select(l => l.ToString())).Trim();
                    if (t.Length > 0)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(t);
                    }
                    break;
                }
                case ContainerBlock child:
                    CollectPlainBlocks(child, sb);
                    break;
            }
        }
    }

    private static MarkdownDocument? GetDocument(string text)
    {
        if (DocCache.TryGetValue(text, out var doc))
            return doc;
        if (DocCache.Count >= DocCacheLimit)
            DocCache.Clear();
        doc = Markdown.Parse(PreprocessDiscordQuotes(text), Pipeline);
        foreach (var par in doc.Descendants<ParagraphBlock>())
        {
            if (par.Inline != null)
                TransformEmails(par.Inline);
        }
        DocCache[text] = doc;
        return doc;
    }

    /// <summary>
    /// Discord-style ">>>" multiline quote: a line starting with &gt;&gt;&gt;
    /// quotes that line and everything after it until end of message, as ONE
    /// blockquote. CommonMark would parse the raw text as triple-nested
    /// quotes, so the tail is rewritten to "&gt; " prefixes - the AST stays a
    /// single coherent QuoteBlock. Fence-aware: &gt;&gt;&gt; inside code
    /// blocks stays literal. Regular ">" and CommonMark "&gt;&gt;" nesting
    /// are untouched.
    /// </summary>
    private static string PreprocessDiscordQuotes(string text)
    {
        if (!text.Contains(">>>", StringComparison.Ordinal))
            return text;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new System.Text.StringBuilder(text.Length + 64);
        char fence = '\0';
        var inMultiQuote = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (inMultiQuote)
            {
                sb.Append(trimmed.Length == 0 ? ">"
                        : trimmed[0] == '>' ? line
                        : "> " + line);
            }
            else if (fence != '\0')
            {
                if (trimmed.Length >= 3 && trimmed.Trim(fence, ' ', '\t').Length == 0)
                    fence = '\0';
                sb.Append(line);
            }
            else if (trimmed.StartsWith("```", StringComparison.Ordinal)
                     || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = trimmed[0];
                sb.Append(line);
            }
            else if (trimmed.StartsWith(">>>", StringComparison.Ordinal)
                     && (trimmed.Length == 3 || trimmed[3] != '>')
                     && line.Length - trimmed.Length <= 3)
            {
                inMultiQuote = true;
                sb.Append("> ").Append(trimmed.Substring(3).TrimStart());
            }
            else
            {
                sb.Append(line);
            }
            if (i + 1 < lines.Length)
                sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Bare email addresses are not covered by Markdig's autolink extension -
    /// literals matching the address shape are split out and wrapped in
    /// mailto: links so they render clickable like other URLs.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex EmailPattern = new(
        @"(?<![\w.%+-])[\w.%+-]+@[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)+",
        System.Text.RegularExpressions.RegexOptions.Compiled
        | System.Text.RegularExpressions.RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(100));

    private static void TransformEmails(ContainerInline container)
    {
        var child = container.FirstChild;
        while (child != null)
        {
            var next = child.NextSibling;
            switch (child)
            {
                case LiteralInline lit:
                    SplitLiteralEmails(lit);
                    break;
                case LinkInline:
                    break; // already linked - don't nest mailto inside links
                case ContainerInline nested:
                    TransformEmails(nested);
                    break;
            }
            child = next;
        }
    }

    private static void SplitLiteralEmails(LiteralInline lit)
    {
        var text = lit.Content.ToString();
        var matches = EmailPattern.Matches(text);
        if (matches.Count == 0)
            return;
        var cursor = 0;
        Inline anchor = lit;
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            if (m.Index > cursor)
            {
                var lead = new LiteralInline(text[cursor..m.Index]);
                anchor.InsertAfter(lead);
                anchor = lead;
            }
            var link = new LinkInline { Url = "mailto:" + m.Value };
            link.AppendChild(new LiteralInline(m.Value));
            anchor.InsertAfter(link);
            anchor = link;
            cursor = m.Index + m.Length;
        }
        if (cursor < text.Length)
            anchor.InsertAfter(new LiteralInline(text[cursor..]));
        lit.Remove();
    }

    private static void RenderBlocks(ContainerBlock container, RenderContext ctx)
    {
        var count = container.Count;
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                SpheneUI.Layout.VerticalSpace(GapBefore(container[i]));
            RenderBlock(container[i], ctx);
        }
    }

    /// <summary>
    /// Top margin per block type - the renderer's only inter-block spacing
    /// rule. Emitted only between siblings, so nested containers never stack
    /// outer margins on their children. Headings get more separation,
    /// paragraphs a consistent 8px, invisible blocks (link refs, empty)
    /// nothing.
    /// </summary>
    private static SpheneUI.Space GapBefore(Block block)
        => block switch
        {
            HeadingBlock { Level: <= 2 } => SpheneUI.Spacing.X16,
            HeadingBlock => SpheneUI.Spacing.X12,
            FootnoteGroup => SpheneUI.Spacing.X12,
            ParagraphBlock or ListBlock or QuoteBlock or CodeBlock
                or Markdig.Extensions.Tables.Table or CustomContainer
                or ThematicBreakBlock or HtmlBlock => SpheneUI.Spacing.X8,
            EmptyBlock or LinkReferenceDefinition
                or LinkReferenceDefinitionGroup => new SpheneUI.Space(0f),
            _ => SpheneUI.Spacing.X4
        };

    private static void RenderBlock(Block block, RenderContext ctx)
    {
        switch (block)
        {
            case ParagraphBlock par:
                RenderParagraph(par, ctx);
                break;
            case HeadingBlock heading:
                RenderHeading(heading, ctx);
                break;
            case ListBlock list:
                RenderList(list, ctx);
                break;
            case QuoteBlock quote:
                RenderQuote(quote, ctx);
                break;
            case CodeBlock code:
                RenderCodeBlock(code, ctx);
                break;
            case ThematicBreakBlock:
                RenderRule(ctx);
                break;
            case HtmlBlock html:
                RenderHtmlBlock(html);
                break;
            case Table table:
                RenderTable(table, ctx);
                break;
            case FootnoteGroup group:
                RenderFootnoteGroup(group, ctx);
                break;
            case CustomContainer custom:
                RenderCustomContainer(custom, ctx);
                break;
            case LinkReferenceDefinitionGroup:
            case LinkReferenceDefinition:
            case EmptyBlock:
                break;
            case ContainerBlock container:
                RenderBlocks(container, ctx);
                break;
            case LeafBlock leaf when leaf.Inline != null:
                ctx.Writer.BeginLine(ctx.RightBound);
                ctx.Writer.Write(leaf.Inline, DefaultStyle());
                break;
        }
    }

    private static MarkdownInlineWriter.SpanStyle DefaultStyle()
        => new() { FontScale = 1f };

    private static void RenderParagraph(ParagraphBlock par, RenderContext ctx)
    {
        // A paragraph that is a single bare image gets block treatment.
        if (ctx.ImageSource != null && par.Inline?.FirstChild is LinkInline { IsImage: true } only
            && only == par.Inline.LastChild)
        {
            RenderImageBlock(only.Url ?? string.Empty,
                MarkdownInlineWriter.CollectText(only), ctx);
            return;
        }
        ctx.Writer.BeginLine(ctx.RightBound);
        ctx.Writer.Write(par.Inline, DefaultStyle());
    }

    private static void RenderHeading(HeadingBlock heading, RenderContext ctx)
    {
        var level = Math.Clamp(heading.Level, 1, 6);
        var style = DefaultStyle();
        // Real font faces per level - never SetWindowFontScale, which
        // raster-scales glyphs into blur. h1 takes the 26px display face,
        // h2/h3 the 20px heading face, h4-h6 stay on the body face.
        style.Font = level switch
        {
            1 => SpheneUI.DisplayFont,
            2 or 3 => SpheneUI.HeadingFont,
            _ => null,
        };
        style.Bold = level <= 3;
        ctx.Writer.BeginLine(ctx.RightBound);
        ctx.Writer.Write(heading.Inline, style);
        if (heading.Level <= 2)
        {
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            var w = MathF.Min(ctx.RightBound - pos.X, ImGui.GetContentRegionAvail().X);
            drawList.AddLine(pos, new Vector2(pos.X + w, pos.Y + 2f * SpheneUI.Scale),
                SpheneUI.ColorUtils.ToU32(
                    SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Border, 0.4f)),
                MathF.Max(1f, SpheneUI.Scale));
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }
    }

    private static void RenderList(ListBlock list, RenderContext ctx)
    {
        var gap = SpheneUI.Spacing.X8.Scaled + SpheneUI.Spacing.X4.Scaled;
        var first = true;
        foreach (var child in list)
        {
            if (!first)
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);
            first = false;
            if (child is not ListItemBlock item)
            {
                RenderBlock(child, ctx);
                continue;
            }
            if (IsTaskItem(item))
            {
                // Task items: checkbox is drawn by the inline writer as the
                // marker - no extra indent here.
                RenderBlocks(item, ctx);
                continue;
            }
            var marker = list.IsOrdered ? $"{item.Order}." : "•";
            var markerW = ImGui.CalcTextSize(marker).X + gap;
            SpheneUI.Text.Colored(marker, SpheneUI.Colors.Accent);
            ImGui.SameLine(0f, gap);
            ImGui.Indent(markerW); // wrapped lines hang under the text column
            RenderBlocks(item, ctx);
            ImGui.Unindent(markerW);
        }
    }

    private static bool IsTaskItem(ListItemBlock item)
        => item.Count > 0
           && item[0] is ParagraphBlock p
           && p.Inline?.FirstChild is Markdig.Extensions.TaskLists.TaskList;

    private static void RenderQuote(QuoteBlock quote, RenderContext ctx)
    {
        var scale = SpheneUI.Scale;
        var barWidth = 3f * scale;
        var inset = SpheneUI.Spacing.X8.Scaled;
        var startPos = ImGui.GetCursorScreenPos();

        ImGui.Indent(barWidth + inset);
        using (ImRaii.PushColor(ImGuiCol.Text, SpheneUI.Colors.TextMuted))
        {
            RenderBlocks(quote, ctx);
        }
        ImGui.Unindent(barWidth + inset);

        var height = MathF.Max(ImGui.GetTextLineHeight(),
            ImGui.GetCursorScreenPos().Y - startPos.Y);
        ImGui.GetWindowDrawList().AddRectFilled(startPos,
            new Vector2(startPos.X + barWidth, startPos.Y + height),
            SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Accent, 0.55f)),
            barWidth * 0.5f);
    }

    private static void RenderCodeBlock(CodeBlock code, RenderContext ctx)
    {
        var scale = SpheneUI.Scale;
        var pad = SpheneUI.Spacing.X8.Scaled;
        var availW = AvailWidth(ctx);

        // Collect source lines
        var lines = new List<string>();
        foreach (var sl in code.Lines.Lines)
            lines.Add(sl.Slice.ToString());
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var lang = code is FencedCodeBlock fenced && !string.IsNullOrWhiteSpace(fenced.Info)
            ? fenced.Info.Trim()
            : null;

        // Header row: language label + copy icon
        using (ImRaii.PushFont(UiBuilder.MonoFont))
        {
            if (lang != null)
                SpheneUI.Text.Colored(lang, SpheneUI.Colors.TextMuted);
            else
                SpheneUI.Text.Plain(" ");
            var copyIcon = "Copy";
            var iconW = ImGui.CalcTextSize(copyIcon).X;
            ImGui.SameLine(0f, MathF.Max(SpheneUI.Spacing.X8.Scaled,
                availW - iconW - ImGui.CalcTextSize(lang ?? " ").X - pad));
            SpheneUI.Text.Colored(copyIcon, SpheneUI.Colors.TextMuted);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                SpheneUI.AttachTooltip("Copy to clipboard");
            }
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                ImGui.SetClipboardText(string.Join('\n', lines));
        }

        // Body: filled rect sized by line count, mono text inside a
        // horizontally scrollable child so long lines scroll instead of clip
        var lineH = ImGui.GetTextLineHeight();
        var pos = ImGui.GetCursorScreenPos();
        var bodyH = pad * 2f + lines.Count * lineH;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, new Vector2(pos.X + availW, pos.Y + bodyH),
            ImGui.GetColorU32(ImGuiCol.FrameBg), 4f * scale);
        drawList.AddRect(pos, new Vector2(pos.X + availW, pos.Y + bodyH),
            SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Border, 0.6f)),
            4f * scale);
        using (var pushId = ImRaii.PushId(code.Line))
        using (var child = ImRaii.Child("##mdcode", new Vector2(availW, bodyH), false,
            ImGuiWindowFlags.HorizontalScrollbar))
        {
            if (child.Success)
            {
                ImGui.SetCursorPos(new Vector2(pad, pad));
                using (ImRaii.PushFont(UiBuilder.MonoFont))
                {
                    foreach (var line in lines)
                        RenderCodeLine(line, lang);
                }
            }
        }
    }

    private sealed class LangProfile
    {
        public required HashSet<string> Keywords;
        public string LineComment = "//";
    }

    private static LangProfile? GetLangProfile(string lang)
    {
        var csharp = new HashSet<string>(StringComparer.Ordinal)
        {
            "using","namespace","public","private","protected","internal","class","struct",
            "record","interface","enum","void","return","var","new","if","else","for",
            "foreach","while","do","switch","case","break","continue","static","string",
            "int","long","float","double","bool","char","byte","true","false","null",
            "get","set","init","this","base","async","await","readonly","sealed",
            "abstract","virtual","override","partial","is","in","out","ref","params",
            "typeof","nameof","try","catch","finally","throw","when","where","required",
            "event","delegate","operator","implicit","explicit","const","unsafe","fixed"
        };
        var js = new HashSet<string>(StringComparer.Ordinal)
        {
            "const","let","var","function","return","if","else","for","of","in","while",
            "do","switch","case","break","continue","class","extends","new","this","super",
            "import","from","export","default","async","await","try","catch","finally",
            "throw","true","false","null","undefined","typeof","instanceof","void","delete",
            "yield","static","get","set"
        };
        var lower = lang.ToLowerInvariant();
        return lower switch
        {
            "csharp" or "cs" or "c#" => new LangProfile { Keywords = csharp },
            "javascript" or "js" or "jsx" or "typescript" or "ts" or "tsx" or "node"
                => new LangProfile { Keywords = js },
            "json" or "json5" => new LangProfile
            {
                Keywords = new HashSet<string>(StringComparer.Ordinal) { "true", "false", "null" },
                LineComment = ""
            },
            "python" or "py" => new LangProfile
            {
                Keywords = new HashSet<string>(StringComparer.Ordinal)
                {
                    "def","class","return","if","elif","else","for","in","while","import",
                    "from","as","pass","break","continue","try","except","finally","raise",
                    "with","lambda","None","True","False","and","or","not","is","global",
                    "nonlocal","yield","async","await","del","assert","print","self"
                },
                LineComment = "#"
            },
            _ => null
        };
    }

    /// <summary>
    /// Renders one code line with light keyword/string/comment coloring.
    /// Stateless per line - block comments spanning lines are not tracked
    /// (acceptable for announcement-sized snippets).
    /// </summary>
    private static void RenderCodeLine(string line, string? lang)
    {
        if (line.Length == 0)
        {
            SpheneUI.Text.Plain(" ");
            return;
        }
        var profile = lang != null ? GetLangProfile(lang) : null;
        if (profile == null)
        {
            SpheneUI.Text.Plain(line);
            return;
        }

        var keywordCol = SpheneUI.Palette.CrystalBlue;
        var stringCol = ImGuiColors.DalamudOrange;
        var commentCol = ImGuiColors.HealerGreen;
        var i = 0;
        var first = true;
        while (i < line.Length)
        {
            var c = line[i];
            int end;
            Vector4? color = null;
            if (profile.LineComment.Length > 0
                && c == profile.LineComment[0]
                && line.AsSpan(i).StartsWith(profile.LineComment.AsSpan(), StringComparison.Ordinal))
            {
                end = line.Length;
                color = commentCol;
            }
            else if (c == '"' || c == '\'')
            {
                end = i + 1;
                while (end < line.Length && line[end] != c)
                {
                    if (line[end] == '\\')
                        end++;
                    end++;
                }
                if (end < line.Length)
                    end++;
                color = stringCol;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                end = i;
                while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
                    end++;
                if (profile.Keywords.Contains(line[i..end]))
                    color = keywordCol;
            }
            else
            {
                end = i + 1;
            }

            var token = line[i..end];
            if (first)
                first = false;
            else
                ImGui.SameLine(0f, 0f);
            if (color is { } col)
                SpheneUI.Text.Colored(token, col);
            else
                SpheneUI.Text.Plain(token);
            i = end;
        }
    }

    private static void RenderRule(RenderContext ctx)
    {
        var scale = SpheneUI.Scale;
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var w = AvailWidth(ctx);
        var y = pos.Y + SpheneUI.Spacing.X4.Scaled;
        drawList.AddLine(new Vector2(pos.X, y), new Vector2(pos.X + w, y),
            SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Border, 0.5f)),
            MathF.Max(1f, scale));
        ImGui.Dummy(new Vector2(w, SpheneUI.Spacing.X8.Scaled));
    }

    private static void RenderHtmlBlock(HtmlBlock html)
    {
        foreach (var sl in html.Lines.Lines)
        {
            var line = sl.Slice.ToString();
            if (line.Length == 0)
                continue;
            SpheneUI.Text.Muted(line);
        }
    }

    private static void RenderTable(Table table, RenderContext ctx)
    {
        var scale = SpheneUI.Scale;
        var pad = 6f * scale;
        var availW = AvailWidth(ctx);
        var cols = table.ColumnDefinitions.Count;
        if (cols == 0)
            return;

        // Measure natural column widths from cell text, then fit to available width.
        var colW = new float[cols];
        foreach (var rowObj in table)
        {
            if (rowObj is not TableRow row)
                continue;
            var ci = 0;
            foreach (var cellObj in row)
            {
                if (cellObj is not TableCell cell || ci >= cols)
                    continue;
                var w = MeasureCell(cell) + pad * 2f;
                if (w > colW[ci])
                    colW[ci] = MathF.Min(w, availW * 0.6f);
                ci++;
            }
        }
        var total = 0f;
        for (var c = 0; c < cols; c++)
        {
            colW[c] = MathF.Max(colW[c], 40f * scale);
            total += colW[c];
        }
        if (total > availW)
        {
            var factor = availW / total;
            for (var c = 0; c < cols; c++)
                colW[c] *= factor;
            total = availW;
        }

        var drawList = ImGui.GetWindowDrawList();
        foreach (var rowObj in table)
        {
            if (rowObj is not TableRow row)
                continue;
            var rowX = ImGui.GetCursorScreenPos().X;
            var rowY = ImGui.GetCursorScreenPos().Y;
            var rowBottom = rowY + ImGui.GetTextLineHeight();
            var cellX = rowX;
            var ci = 0;
            foreach (var cellObj in row)
            {
                if (cellObj is not TableCell cell || ci >= cols)
                    continue;
                var align = ci < table.ColumnDefinitions.Count
                    ? table.ColumnDefinitions[ci].Alignment
                    : null;
                var textW = MeasureCell(cell);
                var offset = align switch
                {
                    TableColumnAlign.Right => MathF.Max(0f, colW[ci] - pad * 2f - textW),
                    TableColumnAlign.Center => MathF.Max(0f, (colW[ci] - pad * 2f - textW) * 0.5f),
                    _ => 0f
                };
                ImGui.SetCursorScreenPos(new Vector2(cellX + pad + offset, rowY));
                var writer = ctx.Writer;
                writer.BeginLine(cellX + colW[ci] - pad);
                var style = DefaultStyle();
                style.Bold = row.IsHeader;
                foreach (var cellBlock in cell)
                {
                    if (cellBlock is ParagraphBlock p)
                        writer.Write(p.Inline, style);
                    else
                        RenderBlock(cellBlock, ctx);
                }
                rowBottom = MathF.Max(rowBottom, ImGui.GetCursorScreenPos().Y);
                cellX += colW[ci];
                ci++;
            }
            // Header rule + row hairlines
            var lineY = rowBottom;
            drawList.AddLine(new Vector2(rowX, lineY), new Vector2(rowX + total, lineY),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                    row.IsHeader ? SpheneUI.Colors.Accent : SpheneUI.Colors.Border,
                    row.IsHeader ? 0.8f : 0.35f)),
                MathF.Max(1f, scale));
            ImGui.SetCursorScreenPos(new Vector2(rowX, rowBottom + SpheneUI.Spacing.X2.Scaled));
        }
    }

    private static float MeasureCell(TableCell cell)
    {
        var w = 0f;
        foreach (var b in cell)
        {
            if (b is ParagraphBlock p)
            {
                var text = MarkdownInlineWriter.CollectText(p.Inline);
                w = MathF.Max(w, ImGui.CalcTextSize(text).X);
            }
        }
        return w;
    }

    private static void RenderFootnoteGroup(FootnoteGroup group, RenderContext ctx)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var w = AvailWidth(ctx);
        drawList.AddLine(pos, new Vector2(pos.X + w, pos.Y),
            SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Border, 0.4f)),
            MathF.Max(1f, SpheneUI.Scale));
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);
        foreach (var child in group)
        {
            if (child is not Footnote note)
                continue;
            FootnoteDefScroll[note.Order] =
                ImGui.GetCursorScreenPos().Y - ImGui.GetWindowPos().Y + ImGui.GetScrollY();
            SpheneUI.Text.Colored($"{note.Order}.", SpheneUI.Colors.TextMuted);
            ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
            ImGui.Indent(ImGui.CalcTextSize("000.").X);
            using (ImRaii.PushColor(ImGuiCol.Text, SpheneUI.Colors.TextMuted))
                RenderBlocks(note, ctx);
            ImGui.Unindent(ImGui.CalcTextSize("000.").X);
        }
    }

    private static void RenderCustomContainer(CustomContainer custom, RenderContext ctx)
    {
        var info = custom.Info?.Trim() ?? string.Empty;
        var args = custom.Arguments ?? string.Empty;
        if (string.Equals(info, "callout", StringComparison.OrdinalIgnoreCase))
        {
            RenderCallout(args, custom, ctx);
            return;
        }
        if (string.Equals(info, "side", StringComparison.OrdinalIgnoreCase))
        {
            RenderSide(args, custom, ctx);
            return;
        }
        RenderBorderedBox(custom, ctx);
    }

    private static void RenderCallout(string kind, CustomContainer custom, RenderContext ctx)
    {
        var (color, icon, label) = kind.Trim().ToLowerInvariant() switch
        {
            "warning" => (ImGuiColors.DalamudOrange, FontAwesomeIcon.ExclamationTriangle, "Warning"),
            "danger" or "error" => (ImGuiColors.DalamudRed, FontAwesomeIcon.TimesCircle, "Danger"),
            "success" or "ok" => (ImGuiColors.HealerGreen, FontAwesomeIcon.CheckCircle, "Success"),
            _ => (SpheneUI.Palette.CrystalBlue, FontAwesomeIcon.InfoCircle, "Info")
        };
        var scale = SpheneUI.Scale;
        var barWidth = 3f * scale;
        var inset = SpheneUI.Spacing.X8.Scaled;
        var startPos = ImGui.GetCursorScreenPos();

        ImGui.Indent(barWidth + inset);
        SpheneUI.Icons.Icon(icon, color);
        ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
        SpheneUI.Text.Colored(label, color);
        RenderBlocks(custom, ctx);
        ImGui.Unindent(barWidth + inset);

        var height = MathF.Max(ImGui.GetTextLineHeight(),
            ImGui.GetCursorScreenPos().Y - startPos.Y);
        ImGui.GetWindowDrawList().AddRectFilled(startPos,
            new Vector2(startPos.X + barWidth, startPos.Y + height),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(color, 0.7f)),
            barWidth * 0.5f);
    }

    private static void RenderSide(string args, CustomContainer custom, RenderContext ctx)
    {
        var parsed = ParseSideArgs(args);
        if (parsed.Url == null || ctx.ImageSource == null)
        {
            RenderBlocks(custom, ctx);
            return;
        }

        var gap = SpheneUI.Spacing.X8.Scaled;
        var startPos = ImGui.GetCursorScreenPos();
        var availW = AvailWidth(ctx);
        var imgW = availW * Math.Clamp(parsed.Width, 15f, 60f) / 100f;
        var textW = availW - imgW - gap;

        var savedBound = ctx.RightBound;
        float imgH, endY;
        if (parsed.LeftSide)
        {
            imgH = DrawImageAt(ctx.ImageSource, parsed.Url, startPos, imgW);
            // Indent (not just SetCursorScreenPos) so NewLine/VerticalSpace
            // resets land in the text column for EVERY block - otherwise the
            // second block onwards renders at the outer left under the image.
            ImGui.Indent(imgW + gap);
            ImGui.SetCursorScreenPos(new Vector2(startPos.X + imgW + gap, startPos.Y));
            ctx.RightBound = startPos.X + availW;
            RenderBlocks(custom, ctx);
            ImGui.Unindent(imgW + gap);
            endY = ImGui.GetCursorScreenPos().Y;
        }
        else
        {
            ctx.RightBound = startPos.X + textW;
            RenderBlocks(custom, ctx);
            endY = ImGui.GetCursorScreenPos().Y;
            ImGui.SetCursorScreenPos(new Vector2(startPos.X + textW + gap, startPos.Y));
            imgH = DrawImageAt(ctx.ImageSource, parsed.Url, ImGui.GetCursorScreenPos(), imgW);
        }
        ctx.RightBound = savedBound;
        ImGui.SetCursorScreenPos(new Vector2(startPos.X, MathF.Max(endY, startPos.Y + imgH)));
    }

    private static (string? Url, bool LeftSide, float Width) ParseSideArgs(string args)
    {
        string? url = null;
        var left = true;
        var width = 38f;
        var rest = args.Trim();
        if (rest.StartsWith("side", StringComparison.OrdinalIgnoreCase))
        {
            // tolerate `:::side left` shorthand without img first
        }
        var span = args.AsSpan();
        var i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && char.IsWhiteSpace(span[i])) i++;
            var keyStart = i;
            while (i < span.Length && span[i] != '=' && !char.IsWhiteSpace(span[i])) i++;
            var key = span[keyStart..i].ToString();
            if (i < span.Length && span[i] == '=')
            {
                i++;
                string value;
                if (i < span.Length && span[i] == '"')
                {
                    i++;
                    var vStart = i;
                    while (i < span.Length && span[i] != '"') i++;
                    value = span[vStart..i].ToString();
                    if (i < span.Length) i++;
                }
                else
                {
                    var vStart = i;
                    while (i < span.Length && !char.IsWhiteSpace(span[i])) i++;
                    value = span[vStart..i].ToString();
                }
                if (key.Equals("img", StringComparison.OrdinalIgnoreCase))
                    url = value;
                else if (key.Equals("side", StringComparison.OrdinalIgnoreCase))
                    left = !value.Equals("right", StringComparison.OrdinalIgnoreCase);
                else if (key.Equals("width", StringComparison.OrdinalIgnoreCase)
                         && float.TryParse(value, out var wv))
                    width = wv;
            }
            else
            {
                if (key.Equals("left", StringComparison.OrdinalIgnoreCase)) left = true;
                else if (key.Equals("right", StringComparison.OrdinalIgnoreCase)) left = false;
            }
        }
        return (IsSafeUrl(url) ? url : null, left, width);
    }

    private static void RenderBorderedBox(CustomContainer custom, RenderContext ctx)
    {
        var scale = SpheneUI.Scale;
        var inset = SpheneUI.Spacing.X8.Scaled;
        var startPos = ImGui.GetCursorScreenPos();
        ImGui.Indent(inset);
        RenderBlocks(custom, ctx);
        ImGui.Unindent(inset);
        var endY = ImGui.GetCursorScreenPos().Y;
        var w = AvailWidth(ctx);
        ImGui.GetWindowDrawList().AddRect(startPos,
            new Vector2(startPos.X + w, endY + inset * 0.5f),
            SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Border, 0.5f)),
            4f * scale);
    }

    /// <summary>Draws an image (or placeholder) at an exact position. Returns painted height; does not move the cursor.</summary>
    private static float DrawImageAt(Func<string, byte[]?> imageSource, string url,
        Vector2 pos, float w)
    {
        var scale = SpheneUI.Scale;
        var wrap = SpheneUI.Media.Texture($"mdimg:{url}", () => imageSource(url));
        var h = wrap != null ? w * (wrap.Height / (float)wrap.Width) : MathF.Min(w * 0.5f, 120f * scale);
        var drawList = ImGui.GetWindowDrawList();
        if (wrap != null)
        {
            SpheneUI.Media.ImageInRect(drawList, wrap, pos, pos + new Vector2(w, h),
                ImageFit.Fit, rounding: 6f * scale);
        }
        else
        {
            drawList.AddRectFilled(pos, pos + new Vector2(w, h),
                ImGui.GetColorU32(ImGuiCol.FrameBg), 6f * scale);
        }
        return h;
    }

    private static void RenderImageBlock(string url, string alt, RenderContext ctx)
    {
        if (ctx.ImageSource == null || !IsSafeUrl(url))
        {
            SpheneUI.Text.Muted($"[image: {alt}]");
            return;
        }
        var scale = SpheneUI.Scale;
        var availW = AvailWidth(ctx);
        var wrap = SpheneUI.Media.Texture($"mdimg:{url}", () => ctx.ImageSource(url));
        if (wrap == null)
        {
            SpheneUI.Media.Placeholder(
                new Vector2(availW, MathF.Min(availW * 0.35f, 160f * scale)));
        }
        else
        {
            var w = MathF.Min(availW, wrap.Width);
            var h = w * (wrap.Height / (float)wrap.Width);
            var drawList = ImGui.GetWindowDrawList();
            var min = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(w, h));
            SpheneUI.Media.ImageInRect(drawList, wrap, min, min + new Vector2(w, h),
                ImageFit.Fit, rounding: 6f * scale);
        }
        if (!string.IsNullOrWhiteSpace(alt))
            SpheneUI.Text.Muted(alt);
    }

    private static Vector4 ParseHexColor(string hexColor)
    {
        try
        {
            var hex = hexColor.StartsWith('#') ? hexColor[1..] : hexColor;
            if (hex.Length == 6)
            {
                var r = Convert.ToInt32(hex[..2], 16) / 255f;
                var g = Convert.ToInt32(hex.Substring(2, 2), 16) / 255f;
                var b = Convert.ToInt32(hex.Substring(4, 2), 16) / 255f;
                return new Vector4(r, g, b, 1.0f);
            }
        }
        catch
        {
            // fall back to white
        }
        return ImGuiColors.DalamudWhite;
    }
}
