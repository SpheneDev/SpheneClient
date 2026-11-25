using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Markdig.Extensions.CustomContainers;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Sphene.Helpers.Emoji;

namespace Sphene.UI.Design;

/// <summary>
/// Walks a Markdig inline tree and emits styled, word-wrap-aware ImGui items.
/// Styles compose through a copy-down <see cref="SpanStyle"/> stack instead of
/// per-combination render paths, so any emphasis nesting (bold inside italic
/// inside a link ...) resolves independently. Bold is faked by a second
/// draw-pass since ImGui ships a single face; italic is a synthetic oblique -
/// neither the default ImGui font nor the shipped Lato-Regular has an italic
/// face, so emitted glyph vertices are sheared in the draw list instead.
/// </summary>
internal sealed class MarkdownInlineWriter
{
    /// <summary>Oblique shear factor for synthetic italic (~11° at baseline).</summary>
    private const float ItalicShear = 0.20f;

    internal struct SpanStyle
    {
        public bool Bold;
        public bool Italic;
        public bool Strike;
        public bool Underline;
        public bool Code;
        public bool Sup;
        public bool Sub;
        public bool Muted;
        public bool Hidden;
        public bool RevealedSpoiler;
        public Vector4? Color;
        public string? Link;
        public string? SpoilerKey;
        public float FontScale;
        /// <summary>Real font face for headings - pushed instead of
        /// <see cref="FontScale"/> so glyphs stay crisp (no raster upscale).</summary>
        public Dalamud.Interface.ManagedFontAtlas.IFontHandle? Font;
    }

    private static readonly Vector4 LinkColor = new(0.36f, 0.65f, 0.95f, 1f);

    private readonly Action<string, string>? _imageBlock;
    private float _lineStartX;
    private float _nextX;
    private float _lineY;
    private float _lineHeight;
    private int _pendingSpaces;
    private bool _lastWasChip;
    private float _lastChipEndX;
    private float _lastChipY = -1f;
    // End of the current underline run: while consecutive words on one line
    // carry underline/link styling in the same color, the inter-word gap is
    // underlined too, so an underlined sentence reads as one solid line
    // instead of being interrupted between words. -1 = no open run.
    private float _underlineRunEndX = -1f;
    private float _underlineRunY = -1f;
    private uint _underlineRunColor;

    public MarkdownInlineWriter(Action<string, string>? imageBlock)
    {
        _imageBlock = imageBlock;
    }

    public float RightBound { get; set; }

    /// <summary>
    /// Marks the current cursor as the left edge of a logical line. ImGui's
    /// <c>ItemSize</c> resets <c>DC.CursorPos.x</c> to the line start after
    /// every item, so flow position is tracked manually in
    /// <see cref="_nextX"/>/<see cref="_lineY"/> and each word is placed with
    /// an explicit <c>SetCursorScreenPos</c> - SameLine packing cannot be
    /// relied on here.
    /// </summary>
    public void BeginLine(float rightBound)
    {
        var pos = ImGui.GetCursorScreenPos();
        _lineStartX = pos.X;
        _nextX = pos.X;
        _lineY = pos.Y;
        _lineHeight = ImGui.GetTextLineHeight();
        RightBound = rightBound;
        _pendingSpaces = 0;
        _lastWasChip = false;
        _underlineRunEndX = -1f;
    }

    public void NewLine()
    {
        _nextX = _lineStartX;
        _lineY += _lineHeight;
        _lineHeight = ImGui.GetTextLineHeight();
        _pendingSpaces = 0;
        _lastWasChip = false;
        _underlineRunEndX = -1f;
    }

    public void Write(ContainerInline? container, SpanStyle style)
    {
        if (container == null)
            return;
        foreach (var child in container)
            WriteInline(child, style);
    }

    /// <summary>Flattens an inline subtree to plain text (spoiler keys, tooltips, measurement).</summary>
    public static string CollectText(Inline? inline)
    {
        switch (inline)
        {
            case null:
                return string.Empty;
            case LiteralInline lit:
                return lit.Content.ToString();
            case HtmlEntityInline ent:
                return ent.Transcoded.ToString();
            case CodeInline code:
                return code.Content;
            case DelimiterInline del:
                return del.ToLiteral();
            case LineBreakInline:
                return " ";
            case FootnoteLink:
                return string.Empty; // markers never appear in flattened text
            case LeafInline leaf:
                return leaf is TaskList ? string.Empty : leaf.ToString() ?? string.Empty;
            case ContainerInline container:
            {
                var sb = new System.Text.StringBuilder();
                foreach (var child in container)
                    sb.Append(CollectText(child));
                return sb.ToString();
            }
            default:
                return inline.ToString() ?? string.Empty;
        }
    }

    private void WriteInline(Inline inline, SpanStyle style)
    {
        switch (inline)
        {
            case LiteralInline lit:
                EmitText(lit.Content.ToString(), style);
                break;
            case HtmlEntityInline ent:
                EmitText(ent.Transcoded.ToString(), style);
                break;
            case CodeInline code:
            {
                var s = style;
                s.Code = true;
                EmitText(code.Content, s);
                break;
            }
            case LineBreakInline:
                NewLine();
                break;
            case HtmlInline html:
            {
                var s = style;
                s.Muted = true;
                EmitText(html.Tag, s);
                break;
            }
            case SpheneTagLeaf tagLeaf:
            {
                var s = style;
                s.Muted = true;
                EmitText(tagLeaf.Literal, s);
                break;
            }
            case PipeTableDelimiterInline:
                EmitText("|", style);
                break;
            case DelimiterInline del:
                EmitText(del.ToLiteral(), style);
                break;
            case CustomContainerInline cci:
                Write(cci, style);
                break;
            case EmphasisInline em when em.DelimiterChar == '|' && em.DelimiterCount == 2:
                WriteSpoiler(em, style);
                break;
            case EmphasisInline em:
                Write(em, EmphasisStyle(style, em));
                break;
            case ColorTagInline c when c.IsUnderline:
            {
                var s = style;
                s.Underline = true;
                Write(c, s);
                break;
            }
            case ColorTagInline c when c.Kind == SpheneTagKind.Size:
            {
                var s = style;
                var pt = Math.Clamp(c.SizePt, MarkdownRenderer.MinSizePt,
                    MarkdownRenderer.MaxSizePt);
                // FontScale is the proportional fallback for the whole time
                // the lazily-built handle reports !Available - and forever if
                // the atlas never produces it. Real glyphs take over once
                // ready; the writer checks Available before Push.
                s.FontScale = pt / 16f;
                if (SpheneUI.ResolveMarkdownFont(pt) is { } sizedFont)
                    s.Font = sizedFont;
                Write(c, s);
                break;
            }
            case ColorTagInline c:
            {
                var s = style;
                if (MarkdownRenderer.ResolveColorArg(c.ColorArg) is { } col)
                    s.Color = col;
                Write(c, s);
                break;
            }
            case LinkInline link when link.IsImage:
            {
                ImGui.SetCursorScreenPos(new Vector2(_nextX, _lineY + _lineHeight));
                _imageBlock?.Invoke(link.Url ?? string.Empty, CollectText(link));
                _nextX = _lineStartX;
                _lineY = ImGui.GetCursorScreenPos().Y;
                _lineHeight = ImGui.GetTextLineHeight();
                break;
            }
            case LinkInline link:
            {
                var s = style;
                if (MarkdownRenderer.IsSafeLinkUrl(link.Url))
                    s.Link = link.Url;
                else
                    s.Muted = true;
                Write(link, s);
                break;
            }
            case AutolinkInline auto:
            {
                // Angle-bracket autolinks (<url>, <a@b.c>) are LeafInline, not
                // LinkInline - without this case they'd emit as plain text.
                var s = style;
                var url = auto.IsEmail ? "mailto:" + auto.Url : auto.Url;
                if (MarkdownRenderer.IsSafeLinkUrl(url))
                    s.Link = url;
                else
                    s.Muted = true;
                EmitText(auto.ToString(), s);
                break;
            }
            case FootnoteLink fl when !fl.IsBackLink:
                EmitFootnote(fl, style);
                break;
            case FootnoteLink backLink:
            {
                // Return-reference inside the footnote list - click scrolls
                // back to the body reference. The generic leaf fallback
                // would emit the type name, so this case must stay first.
                ImGui.SetCursorScreenPos(new Vector2(_nextX + _pendingSpaces * ImGui.CalcTextSize(" ").X, _lineY));
                _pendingSpaces = 0;
                SpheneUI.Icons.Icon(FontAwesomeIcon.Reply, SpheneUI.Colors.Accent);
                _nextX += ImGui.GetItemRectSize().X + SpheneUI.Spacing.X4.Scaled;
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    SpheneUI.AttachTooltip("Back to reference");
                }
                var order = backLink.Footnote?.Order ?? 0;
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left)
                    && MarkdownRenderer.FootnoteRefScroll.TryGetValue(order, out var refY))
                {
                    ImGui.SetScrollY(MathF.Max(0f, refY - ImGui.GetTextLineHeight() * 2f));
                }
                break;
            }
            case TaskList task:
            {
                ImGui.SetCursorScreenPos(new Vector2(_nextX, _lineY));
                SpheneUI.Icons.Icon(
                    task.Checked ? FontAwesomeIcon.CheckSquare : FontAwesomeIcon.Square,
                    task.Checked ? SpheneUI.Colors.Accent : SpheneUI.Colors.TextMuted);
                _nextX += ImGui.GetItemRectSize().X + SpheneUI.Spacing.X4.Scaled;
                break;
            }
            case MathInline math:
            {
                var s = style;
                s.Code = true;
                s.Muted = true;
                EmitText(math.Content.ToString(), s);
                break;
            }
            case ContainerInline container:
                Write(container, style);
                break;
            case LeafInline leaf:
                EmitText(leaf.ToString() ?? string.Empty, style);
                break;
        }
    }

    private static SpanStyle EmphasisStyle(SpanStyle s, EmphasisInline em)
    {
        var t = s;
        switch (em.DelimiterChar, em.DelimiterCount)
        {
            // Discord convention: __underline__ (not CommonMark bold). _x_ stays italic.
            case ('_', 2):
                t.Underline = true;
                break;
            case ('*' or '_', 1):
                t.Italic = true;
                break;
            case ('*' or '_', >= 2):
                t.Bold = true;
                break;
            case ('~', 2):
                t.Strike = true;
                break;
            case ('~', 1):
                t.Sub = true;
                break;
            case ('^', 1):
                t.Sup = true;
                break;
            case ('+', 2):
                t.Underline = true;
                break;
        }
        return t;
    }

    private void WriteSpoiler(EmphasisInline em, SpanStyle style)
    {
        // Position-based key - identical spoiler texts still toggle independently.
        var key = $"{em.Line}:{em.Span.Start}-{em.Span.End}";
        var s = style;
        s.SpoilerKey = key;
        if (MarkdownRenderer.IsSpoilerRevealed(key))
            s.RevealedSpoiler = true;
        else
            s.Hidden = true;
        Write(em, s);
    }

    private void EmitFootnote(FootnoteLink fl, SpanStyle style)
    {
        var s = style;
        s.Link = null;
        s.Sup = true; // superscript-style marker
        var order = fl.Footnote?.Order ?? fl.Index;
        var label = $"[{order}]";
        EmitWord(label, _pendingSpaces, s, linkColorOnly: true);
        _pendingSpaces = 0;
        MarkdownRenderer.FootnoteRefScroll[order] =
            ImGui.GetCursorScreenPos().Y - ImGui.GetWindowPos().Y + ImGui.GetScrollY();
        if (ImGui.IsItemHovered() && fl.Footnote != null)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            SpheneUI.AttachTooltip(TrimTooltip(CollectBlockText(fl.Footnote)));
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left)
            && MarkdownRenderer.FootnoteDefScroll.TryGetValue(order, out var defY))
        {
            ImGui.SetScrollY(MathF.Max(0f, defY - ImGui.GetTextLineHeight() * 2f));
        }
    }

    /// <summary>Flattens a block subtree (e.g. a footnote body) to plain text for tooltips.</summary>
    private static string CollectBlockText(Block block)
    {
        var sb = new System.Text.StringBuilder();
        CollectBlockText(block, sb);
        return sb.ToString().Trim();
    }

    private static void CollectBlockText(Block block, System.Text.StringBuilder sb)
    {
        switch (block)
        {
            case LeafBlock leaf:
                sb.Append(CollectText(leaf.Inline));
                sb.Append(' ');
                break;
            case ContainerBlock container:
                foreach (var child in container)
                    CollectBlockText(child, sb);
                break;
        }
    }

    private static string TrimTooltip(string text)
        => text.Length > 300 ? string.Concat(text.AsSpan(0, 300), "...") : text;

    /// <summary>Splits text into words and emits each via <see cref="EmitWord"/>, tracking runs of whitespace as pending space width.</summary>
    private void EmitText(string text, SpanStyle style)
    {
        // Emoji runs become their own atoms: a cluster can never be split
        // mid-word, so it rides the same wrap flow as an unbreakable word.
        var runs = SpheneUI.Emoji.Segment(text);
        var i = 0;
        var n = text.Length;
        var runIdx = 0;
        while (i < n)
        {
            if (runIdx < runs.Count)
            {
                var run = runs[runIdx];
                if (run.Kind == EmojiCore.RunKind.Emoji && run.Start == i)
                {
                    EmitEmojiAtom(text.Substring(run.Start, run.Length), _pendingSpaces, style);
                    _pendingSpaces = 0;
                    i += run.Length;
                    runIdx++;
                    continue;
                }
                if (run.Start <= i)
                {
                    runIdx++;
                    continue;
                }
            }
            var c = text[i];
            if (c == '\n')
            {
                NewLine();
                i++;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                var j = i;
                while (j < n && char.IsWhiteSpace(text[j]) && text[j] != '\n')
                    j++;
                _pendingSpaces += j - i;
                i = j;
                continue;
            }
            var k = i;
            while (k < n && !char.IsWhiteSpace(text[k])
                && !(runIdx < runs.Count && runs[runIdx].Start == k))
                k++;
            EmitWord(text[i..k], _pendingSpaces, style);
            _pendingSpaces = 0;
            i = k;
        }
    }

    /// <summary>One emoji sprite in the inline flow - wraps like a word,
    /// draws via the atlas, keeps text line metrics.</summary>
    private void EmitEmojiAtom(string sequence, int spaces, in SpanStyle style)
    {
        var lineH = ImGui.GetTextLineHeight();
        var emojiSize = MathF.Max(1f, lineH * 1.1f);
        var spaceW = spaces * ImGui.CalcTextSize(" ").X;
        var atLineStart = MathF.Abs(_nextX - _lineStartX) < 0.5f;
        var needed = (atLineStart ? 0f : spaceW) + emojiSize;
        if (!atLineStart && _nextX + needed > RightBound + 0.001f)
        {
            _nextX = _lineStartX;
            _lineY += _lineHeight;
            _lineHeight = ImGui.GetTextLineHeight();
        }
        else if (!atLineStart)
        {
            _nextX += spaceW;
        }
        var pos = new Vector2(_nextX, _lineY + (lineH - emojiSize) * 0.5f);
        ImGui.SetCursorScreenPos(pos);
        ImGui.Dummy(new Vector2(emojiSize, lineH));
        var alpha = style.Hidden ? 0.25f : 1f;
        SpheneUI.Emoji.SpriteAt(ImGui.GetWindowDrawList(), pos, emojiSize,
            sequence, alpha);
        _nextX += emojiSize;
        _lineHeight = MathF.Max(_lineHeight, lineH);
        _lastWasChip = false;
        _underlineRunEndX = -1f; // emoji atom carries no underline - breaks the run
    }

    private static Vector4 EffectiveColor(in SpanStyle s)
    {
        if (s.Hidden)
            return SpheneUI.Colors.TextMuted;
        if (s.Color is { } col)
            return col;
        if (s.Link != null)
            return LinkColor;
        if (s.Code)
            return ImGuiColors.DalamudGrey2;
        if (s.Muted)
            return SpheneUI.Colors.TextMuted;
        return ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
    }

    private void EmitWord(string word, int spaces, in SpanStyle style, bool linkColorOnly = false)
    {
        var scale = SpheneUI.Scale;
        // Headings carry a real font face - FontScale is only the sup/sub
        // shrink now; SetWindowFontScale raster-scales glyphs and must not
        // be used for size steps.
        // IFontHandle.Push() throws while the atlas hasn't built the face -
        // and forever when it can't (missing asset/test build). Only push a
        // handle that reports Available; otherwise scale the default face.
        var readyFont = style.Font is { Available: true } f ? f : null;
        var fontScale = readyFont != null ? 1f
            : (style.FontScale > 0f ? style.FontScale : 1f) * ((style.Sup || style.Sub) ? 0.75f : 1f);
        var needsScale = MathF.Abs(fontScale - 1f) > 0.001f;

        IDisposable? fontPop = null;
        if (readyFont != null)
            fontPop = readyFont.Push();
        else if (style.Code)
            fontPop = ImRaii.PushFont(UiBuilder.MonoFont);
        if (needsScale)
            ImGui.SetWindowFontScale(fontScale);
        try
        {

            var wordW = ImGui.CalcTextSize(word).X;
            var spaceW = spaces * ImGui.CalcTextSize(" ").X;
            // Sheared italic tops lean right by shear*lineH - reserve it in the
            // wrap check so the last word's overhang can't cross the bound.
            var lineHPre = ImGui.GetTextLineHeight();
            var italicOverhang = style.Italic ? ItalicShear * lineHPre : 0f;
            var atLineStart = MathF.Abs(_nextX - _lineStartX) < 0.5f;
            var needed = (atLineStart ? 0f : spaceW) + wordW + italicOverhang;
            if (!atLineStart && _nextX + needed > RightBound + 0.001f)
            {
                _nextX = _lineStartX;
                _lineY += _lineHeight;
                _lineHeight = ImGui.GetTextLineHeight();
            }
            else if (!atLineStart)
            {
                _nextX += spaceW;
            }

            var pos = new Vector2(_nextX, _lineY);
            if (style.Sup)
                pos.Y -= lineHPre * 0.28f;
            else if (style.Sub)
                pos.Y += lineHPre * 0.22f;
            ImGui.SetCursorScreenPos(pos);
            _nextX += wordW;
            _lineHeight = MathF.Max(_lineHeight, ImGui.GetTextLineHeight());

            var lineH = ImGui.GetTextLineHeight();
            var drawList = ImGui.GetWindowDrawList();
            var effColor = linkColorOnly ? LinkColor : EffectiveColor(style);
            var effU32 = SpheneUI.ColorUtils.ToU32(effColor);

            if (style.Hidden)
            {
                // Real text item keeps layout + interaction; an opaque rect
                // covers it so nothing of the content shows through.
                using (ImRaii.PushColor(ImGuiCol.Text, effColor))
                    SpheneUI.Text.Plain(word);
                var frameBg = ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg];
                frameBg.W = 1f; // fully opaque - FrameBg is translucent by default
                drawList.AddRectFilled(
                    pos - new Vector2(MathF.Min(spaceW, 4f * scale) + 1f, 0f),
                    pos + new Vector2(wordW + 2f, lineH),
                    SpheneUI.ColorUtils.ToU32(frameBg), 3f * scale);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    SpheneUI.AttachTooltip("Spoiler - click to reveal");
                }
                if (style.SpoilerKey != null && ImGui.IsItemClicked(ImGuiMouseButton.Left))
                    MarkdownRenderer.ToggleSpoiler(style.SpoilerKey);
                _lastWasChip = false;
                _underlineRunEndX = -1f;
            }
            else
            {
                var chip = style.Code || style.RevealedSpoiler;
                if (chip)
                {
                    // Same-line continuation merges into the previous chip so a
                    // multi-word code/spoiler span reads as one continuous bar
                    // (spaces inside the span keep the background too).
                    var continues = _lastWasChip && MathF.Abs(_lastChipY - pos.Y) < 0.5f;
                    var padX = 2f * scale;
                    var left = continues ? _lastChipEndX : pos.X - padX;
                    drawList.AddRectFilled(new Vector2(left, pos.Y - 0.5f * scale),
                        new Vector2(pos.X + wordW + padX, pos.Y + lineH + 0.5f * scale),
                        ImGui.GetColorU32(ImGuiCol.FrameBg), 3f * scale,
                        continues
                            ? ImDrawFlags.RoundCornersNone
                            : ImDrawFlags.RoundCornersTopLeft | ImDrawFlags.RoundCornersBottomLeft);
                    _lastChipEndX = pos.X + wordW + padX;
                    _lastChipY = pos.Y;
                }
                _lastWasChip = chip;
                var vtxStart = style.Italic ? drawList.VtxBuffer.Size : -1;
                using (ImRaii.PushColor(ImGuiCol.Text, effColor))
                    SpheneUI.Text.Plain(word);
                if (style.Bold)
                {
                    // Faux-bold: second pass offset by a fraction of a pixel.
                    drawList.AddText(pos + new Vector2(MathF.Max(1f, scale * 0.6f), 0f), effU32, word);
                }
                if (vtxStart >= 0)
                    ShearGlyphs(drawList, vtxStart, pos.Y + lineH, ItalicShear);
                if (style.Underline || style.Link != null)
                {
                    var lineY = pos.Y + lineH - 1f;
                    // Bridge the inter-word gap when the previous word on this
                    // line ended an underline run in the same color - spaces
                    // inside an underlined span stay underlined, so the line
                    // reads continuous instead of dashed between words. The
                    // bridge and the word underline merge into ONE line so no
                    // sub-pixel seam can appear at the joint.
                    var startX = pos.X;
                    if (_underlineRunEndX >= 0f && MathF.Abs(_underlineRunY - pos.Y) < 0.5f
                        && _underlineRunColor == effU32 && pos.X > _underlineRunEndX)
                    {
                        startX = _underlineRunEndX;
                    }
                    drawList.AddLine(new Vector2(startX, lineY), new Vector2(pos.X + wordW, lineY),
                        effU32, MathF.Max(1f, scale));
                    _underlineRunEndX = pos.X + wordW;
                    _underlineRunY = pos.Y;
                    _underlineRunColor = effU32;
                }
                else
                {
                    _underlineRunEndX = -1f;
                }
                if (style.Strike)
                {
                    var lineY = pos.Y + lineH * 0.52f;
                    drawList.AddLine(new Vector2(pos.X, lineY), new Vector2(pos.X + wordW, lineY),
                        effU32, MathF.Max(1f, scale));
                }
                if (style.RevealedSpoiler && style.SpoilerKey != null)
                {
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        SpheneUI.AttachTooltip("Click to hide again");
                    }
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                        MarkdownRenderer.ToggleSpoiler(style.SpoilerKey);
                }
                if (style.Link != null)
                {
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        SpheneUI.AttachTooltip(style.Link);
                    }
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                        Dalamud.Utility.Util.OpenLink(style.Link);
                }
            }

        }
        finally
        {
            if (needsScale)
                ImGui.SetWindowFontScale(1f);
            fontPop?.Dispose();
        }
    }

    /// <summary>
    /// Synthetic italic: shears the draw-list vertices appended since
    /// <paramref name="vtxStart"/> so glyph tops lean right and bottoms stay
    /// anchored. Used because no loaded font face provides true italics.
    /// </summary>
    private static unsafe void ShearGlyphs(ImDrawListPtr drawList, int vtxStart, float baseline, float shear)
    {
        var buf = drawList.VtxBuffer;
        var data = buf.Data;
        for (var i = vtxStart; i < buf.Size; i++)
        {
            ref var v = ref data[i];
            v.Pos.X += shear * (baseline - v.Pos.Y);
        }
    }
}
