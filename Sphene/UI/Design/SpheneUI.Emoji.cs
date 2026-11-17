using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Sphene.Helpers.Emoji;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// Color-emoji rendering on top of the Noto Emoji texture atlas.
    /// The Dalamud font chain carries no emoji glyphs and stb_truetype
    /// cannot rasterize CBDT color fonts, so every emoji sequence is
    /// segmented by <see cref="EmojiCore"/> and drawn as an atlas sprite
    /// inline with ordinary text. Unknown/unsupported sequences demote
    /// (strip skin tones, then reduce to the first codepoint) to the
    /// nearest drawable emoji instead of rendering tofu.
    /// </summary>
    public static class Emoji
    {
        private const string AtlasResource = "Sphene.Resources.Emoji.emoji-atlas.png";
        private const string IndexResource = "Sphene.Resources.Emoji.emoji-index.json";
        private const string AtlasKey = "emoji:atlas";
        private const float EmojiCellScale = 1.1f;

        /// <summary>One picker entry from the packed index.</summary>
        public sealed class Entry
        {
            public required string Seq { get; init; }        // atlas key (codepoints, no VS)
            public required string Glyph { get; init; }      // UTF-16 sequence
            public required string Name { get; init; }
            public required string Keywords { get; init; }
            public required int Category { get; init; }
            public required int Cell { get; init; }
            public string[]? ToneTexts { get; set; }        // toned sequences (5) when supported
            public int[]? ToneCells { get; set; }
        }

        private sealed record IndexModel(int Cell, int Cols, int RowCount, int AtlasW, int AtlasH,
            List<string> CatIds, List<string> CatNames, List<Entry> Entries,
            Dictionary<string, int> CellBySeq, HashSet<int> Bases);

        private static IndexModel? _index;
        private static bool _indexFailed;

        /// <summary>The parsed picker catalog (null until the embedded index loads).</summary>
        public static IReadOnlyList<Entry>? Catalog => _index?.Entries;

        /// <summary>Category ids in picker order.</summary>
        public static IReadOnlyList<string>? CategoryIds => _index?.CatIds;

        /// <summary>Category display names in picker order.</summary>
        public static IReadOnlyList<string>? CategoryNames => _index?.CatNames;

        /// <summary>Atlas cell edge in pixels (rendered size of one emoji sprite).</summary>
        public static float EmojiSize => MathF.Max(1f, ImGui.GetTextLineHeight() * EmojiCellScale);

        /// <summary>Whether the atlas texture finished decoding.</summary>
        public static bool IsReady => EnsureIndex() && Atlas() != null;

        /// <summary>Parses the embedded index once; returns false when unavailable.</summary>
        private static bool EnsureIndex()
        {
            if (_index != null)
            {
                return true;
            }
            if (_indexFailed)
            {
                return false;
            }
            try
            {
                var asm = typeof(SpheneUI).Assembly;
                using var stream = asm.GetManifestResourceStream(IndexResource);
                if (stream == null)
                {
                    _indexFailed = true;
                    return false;
                }
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                var cell = root.GetProperty("cell").GetInt32();
                var cols = root.GetProperty("cols").GetInt32();
                var rows = root.GetProperty("rows").GetInt32();
                var catIds = new List<string>();
                var catNames = new List<string>();
                foreach (var c in root.GetProperty("cats").EnumerateArray())
                {
                    catIds.Add(c.GetProperty("id").GetString() ?? string.Empty);
                    catNames.Add(c.GetProperty("n").GetString() ?? string.Empty);
                }
                var entries = new List<Entry>();
                var cellBySeq = new Dictionary<string, int>(StringComparer.Ordinal);
                var bases = new HashSet<int>();
                foreach (var e in root.GetProperty("entries").EnumerateArray())
                {
                    var seq = e.GetProperty("s").GetString() ?? string.Empty;
                    var catId = e.GetProperty("c").GetString() ?? string.Empty;
                    var cat = catIds.IndexOf(catId);
                    var entry = new Entry
                    {
                        Seq = seq,
                        Glyph = KeyToText(seq),
                        Name = e.GetProperty("n").GetString() ?? string.Empty,
                        Keywords = e.TryGetProperty("k", out var k) ? k.GetString() ?? string.Empty : string.Empty,
                        Category = Math.Max(0, cat),
                        Cell = e.GetProperty("cell").GetInt32(),
                    };
                    if (e.TryGetProperty("tv", out var tv) && tv.ValueKind == JsonValueKind.Array)
                    {
                        var toneCells = new int[5];
                        var toneTexts = new string[5];
                        var ti = 0;
                        var ok = true;
                        foreach (var t in tv.EnumerateArray())
                        {
                            if (ti >= 5)
                            {
                                ok = false;
                                break;
                            }
                            toneCells[ti] = t.GetInt32();
                            toneTexts[ti] = entry.Glyph + char.ConvertFromUtf32(EmojiCore.SkinToneBase + ti);
                            ti++;
                        }
                        if (ok && ti == 5)
                        {
                            entry.ToneCells = toneCells;
                            entry.ToneTexts = toneTexts;
                        }
                    }
                    entries.Add(entry);
                    cellBySeq[seq] = entry.Cell;
                    if (entry.ToneCells != null)
                    {
                        for (var i = 0; i < 5; i++)
                        {
                            cellBySeq[seq + "_" + (EmojiCore.SkinToneBase + i).ToString("x")] = entry.ToneCells[i];
                        }
                    }
                    var firstCp = FirstCodepoint(seq);
                    if (firstCp >= 0)
                    {
                        bases.Add(firstCp);
                    }
                }
                _index = new IndexModel(cell, cols, rows, cols * cell, rows * cell,
                    catIds, catNames, entries, cellBySeq, bases);
                return true;
            }
            catch (Exception ex)
            {
                _indexFailed = true;
                LogError(ex, "Emoji: failed to load emoji index");
                return false;
            }
        }

        private static int FirstCodepoint(string key)
        {
            var sep = key.IndexOf('_');
            var head = sep < 0 ? key : key[..sep];
            return int.TryParse(head, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var cp) ? cp : -1;
        }

        private static string KeyToText(string key)
        {
            var sb = new StringBuilder(key.Length);
            foreach (var part in key.Split('_'))
            {
                if (int.TryParse(part, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var cp))
                {
                    sb.Append(char.ConvertFromUtf32(cp));
                }
            }
            return sb.ToString();
        }

        private static byte[]? AtlasPng()
        {
            try
            {
                var asm = typeof(SpheneUI).Assembly;
                using var stream = asm.GetManifestResourceStream(AtlasResource);
                if (stream == null)
                {
                    return null;
                }
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Cache-owned atlas wrap (re-decodes from the embedded PNG on eviction).</summary>
        private static IDalamudTextureWrap? Atlas()
        {
            if (!EnsureIndex())
            {
                return null;
            }
            return Media.Texture(AtlasKey, AtlasPng);
        }

        /// <summary>Atlas texture handle for <see cref="Media.SpriteInRect"/> callers; default until decoded.</summary>
        public static ImTextureID AtlasHandle => Atlas()?.Handle ?? default;

        /// <summary>Sprite UVs for a packed atlas key (no demotion).</summary>
        public static bool TrySpriteForKey(string atlasKey, out Vector2 uv0, out Vector2 uv1)
        {
            uv0 = uv1 = Vector2.Zero;
            if (!EnsureIndex() || !_index!.CellBySeq.TryGetValue(atlasKey, out var cellIdx))
            {
                return false;
            }
            UvForCell(cellIdx, out uv0, out uv1);
            return true;
        }

        /// <summary>
        /// Sprite UVs for an emoji sequence: exact atlas key, then demotion
        /// (drop skin tones, then reduce to the leading codepoint).
        /// </summary>
        public static bool TrySprite(string sequence, out Vector2 uv0, out Vector2 uv1)
        {
            if (TrySpriteForKey(EmojiCore.ToAtlasKey(sequence), out uv0, out uv1))
            {
                return true;
            }
            foreach (var key in EmojiCore.DemotionKeys(sequence))
            {
                if (TrySpriteForKey(key, out uv0, out uv1))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Sprite UVs for a catalog entry (exact cell, no lookup).</summary>
        public static bool TrySprite(Entry entry, out Vector2 uv0, out Vector2 uv1)
        {
            uv0 = uv1 = Vector2.Zero;
            if (!EnsureIndex())
            {
                return false;
            }
            UvForCell(entry.Cell, out uv0, out uv1);
            return true;
        }

        private static void UvForCell(int cellIdx, out Vector2 uv0, out Vector2 uv1)
        {
            var idx = _index!;
            var col = cellIdx % idx.Cols;
            var row = cellIdx / idx.Cols;
            var w = (float)idx.AtlasW;
            var h = (float)idx.AtlasH;
            uv0 = new Vector2(col * idx.Cell / w, row * idx.Cell / h);
            uv1 = new Vector2((col + 1) * idx.Cell / w, (row + 1) * idx.Cell / h);
        }

        /// <summary>Segmentation using the catalog's base-codepoint set -
        /// for inline writers (markdown) that interleave atoms themselves.</summary>
        internal static List<EmojiCore.Run> Segment(string text)
        {
            EnsureIndex();
            return EmojiCore.Segment(text, _index?.Bases);
        }

        /// <summary>Draws one emoji sprite at an explicit position; falls
        /// back to font text when the sequence has no sprite.</summary>
        public static void SpriteAt(ImDrawListPtr drawList, Vector2 pos, float size,
            string sequence, float alpha = 1f)
        {
            var atlas = Atlas();
            if (atlas != null && TrySprite(sequence, out var uv0, out var uv1))
            {
                drawList.AddImage(atlas.Handle, pos, pos + new Vector2(size), uv0, uv1,
                    ColorUtils.ToU32(new Vector4(1f, 1f, 1f, alpha)));
            }
            else
            {
                drawList.AddText(pos, ColorUtils.ToU32(
                    ColorUtils.WithAlpha(Colors.TextPrimary, alpha)), sequence);
            }
        }

        /// <summary>Whether <paramref name="text"/> contains a segmentable emoji sequence.</summary>
        public static bool ContainsEmoji(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            EnsureIndex();
            foreach (var run in EmojiCore.Segment(text, _index?.Bases))
            {
                if (run.Kind == EmojiCore.RunKind.Emoji)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Redraws the full visible line of a single-line input whose native
        /// text was rendered transparent by the caller: text runs via
        /// AddText, emoji sequences via atlas sprites (unmappable sequences
        /// fall back to the font glyph), plus a mirrored blinking caret
        /// because the native caret rides the hidden Text color. Emoji take
        /// a slot of <see cref="EmojiSize"/> so neighboring text never sits
        /// under a sprite. <paramref name="value"/> is the buffer view - every
        /// emoji already occupies one BMP placeholder char the queue's codec
        /// resolves back to its sequence. The queue's caret offset mirrors
        /// ImGui's horizontal scroll so the drawn layout tracks the widget's own.
        /// </summary>
        public static void InputOverlay(string value, EmojiEditQueue queue)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            EnsureIndex();
            var codec = queue.Codec;
            var runs = EmojiCore.Segment(value, codec.PlaceholderMap, _index?.Bases);
            var style = ImGui.GetStyle();
            var itemMin = ImGui.GetItemRectMin();
            var itemMax = ImGui.GetItemRectMax();
            var padX = style.FramePadding.X;
            var padY = style.FramePadding.Y;
            var textOrigin = new Vector2(itemMin.X + padX, itemMin.Y + padY);
            var innerMaxX = itemMax.X - padX;
            var innerW = MathF.Max(8f, innerMaxX - textOrigin.X);
            var lineH = ImGui.GetTextLineHeight();
            var emojiW = EmojiSize;

            // buffer view is BMP-only, so the ImWchar caret is a char index
            var caretChar = queue.CaretCharOffset >= 0
                ? Math.Min(queue.CaretCharOffset, value.Length) : -1;
            var caretW = caretChar >= 0
                ? MeasurePrefix(value, runs, caretChar, emojiW) : 0f;

            // mirror ImGui's single-line scroll: the caret stays inside the
            // inner rect, moving the offset as little as possible.
            var scroll = queue.ScrollEst;
            if (ImGui.IsItemActive() && caretChar >= 0)
            {
                if (caretW < scroll)
                {
                    scroll = caretW;
                }
                else if (caretW > scroll + innerW)
                {
                    scroll = caretW - innerW;
                }
            }
            scroll = Math.Clamp(scroll, 0f,
                MathF.Max(0f, MeasurePrefix(value, runs, value.Length, emojiW) - innerW));
            queue.ScrollEst = scroll;

            var dl = ImGui.GetWindowDrawList();
            var textCol = ColorUtils.ToU32(Colors.TextPrimary);
            var atlas = Atlas();
            ImGui.PushClipRect(new Vector2(textOrigin.X - 1f, itemMin.Y),
                new Vector2(innerMaxX + 1f, itemMax.Y), true);
            var x = textOrigin.X - scroll;
            var emojiYOfs = (lineH - emojiW) * 0.5f;
            foreach (var run in runs)
            {
                var sub = value.Substring(run.Start, run.Length);
                // a 1-char placeholder run resolves to its real sequence for
                // the sprite lookup; anything else draws as the literal run
                var spriteSeq = run.Kind == EmojiCore.RunKind.Emoji
                    && run.Length == 1
                    && codec.TryResolve(sub[0], out var resolved)
                    ? resolved : sub;
                if (run.Kind == EmojiCore.RunKind.Emoji && atlas != null
                    && TrySprite(spriteSeq, out var uv0, out var uv1))
                {
                    dl.AddImage(atlas.Handle, new Vector2(x, textOrigin.Y + emojiYOfs),
                        new Vector2(x + emojiW, textOrigin.Y + emojiYOfs + emojiW),
                        uv0, uv1, 0xFFFFFFFF);
                }
                else
                {
                    dl.AddText(new Vector2(x, textOrigin.Y), textCol, sub);
                }
                x += run.Kind == EmojiCore.RunKind.Emoji
                    ? emojiW : ImGui.CalcTextSize(sub).X;
            }
            if (ImGui.IsItemActive() && caretChar >= 0
                && (int)(ImGui.GetTime() / 0.55f) % 2 == 0)
            {
                var caretX = textOrigin.X + caretW - scroll;
                if (caretX >= textOrigin.X - 1f && caretX <= innerMaxX + 1f)
                {
                    dl.AddRectFilled(new Vector2(caretX, textOrigin.Y),
                        new Vector2(caretX + 1.5f, textOrigin.Y + lineH), textCol);
                }
            }
            ImGui.PopClipRect();
        }

        /// <summary>Render width of everything before <paramref name="charEnd"/>,
        /// matching the overlay's own slot math (emoji occupy <paramref name="emojiW"/>).</summary>
        private static float MeasurePrefix(string value, List<EmojiCore.Run> runs,
            int charEnd, float emojiW)
        {
            var w = 0f;
            foreach (var r in runs)
            {
                if (r.Start >= charEnd)
                {
                    break;
                }
                var len = Math.Min(r.Length, charEnd - r.Start);
                w += r.Kind == EmojiCore.RunKind.Emoji
                    ? emojiW : ImGui.CalcTextSize(value.Substring(r.Start, len)).X;
            }
            return w;
        }


        /// <summary>Atlas sprite at the cursor (picker cells, inline icons).</summary>
        public static void Sprite(Vector2 uv0, Vector2 uv1, float size, float alpha = 1f)
        {
            var atlas = Atlas();
            var pos = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(size, size));
            if (atlas == null)
            {
                return;
            }
            Media.SpriteInRect(ImGui.GetWindowDrawList(), atlas.Handle, pos, pos + new Vector2(size),
                uv0, uv1, new Vector4(1f, 1f, 1f, alpha));
        }

        /// <summary>
        /// Emoji-aware wrapped text: standard text color, wraps at the
        /// remaining content width like <see cref="Text.Plain"/> under a
        /// pushed wrap bound. Advances the cursor past the whole block.
        /// </summary>
        public static void Plain(string text, Vector4? color = null, float alpha = 1f)
        {
            var wrapW = ImGui.GetContentRegionAvail().X;
            Wrapped(text, wrapW, color, alpha);
        }

        /// <summary>Emoji-aware wrapped text with explicit wrap width.</summary>
        public static void Wrapped(string text, float wrapWidth, Vector4? color = null, float alpha = 1f)
        {
            var pos = ImGui.GetCursorScreenPos();
            var size = Measure(text, wrapWidth);
            ImGui.Dummy(size);
            DrawAt(ImGui.GetWindowDrawList(), pos, text, wrapWidth,
                color ?? Colors.TextPrimary, alpha);
        }

        /// <summary>Total rect a wrapped emoji-aware text block occupies.</summary>
        public static Vector2 Measure(string text, float wrapWidth)
        {
            var lineH = ImGui.GetTextLineHeightWithSpacing();
            if (string.IsNullOrEmpty(text))
            {
                return new Vector2(0f, lineH);
            }
            EnsureIndex();
            var lines = EmojiCore.Layout(text, Math.Max(16f, wrapWidth), EmojiSize,
                s => ImGui.CalcTextSize(s).X, _index?.Bases);
            var w = 0f;
            foreach (var l in lines)
            {
                w = MathF.Max(w, l.Width);
            }
            return new Vector2(w, Math.Max(1, lines.Count) * lineH);
        }

        /// <summary>
        /// Emoji-aware measure+draw into the window draw list at an explicit
        /// origin (bubble/chat code that positions rows itself). Returns the
        /// consumed height.
        /// </summary>
        public static float DrawAt(ImDrawListPtr drawList, Vector2 origin, string text,
            float wrapWidth, Vector4 color, float alpha = 1f)
        {
            var lineH = ImGui.GetTextLineHeight();
            var lineStep = ImGui.GetTextLineHeightWithSpacing();
            if (string.IsNullOrEmpty(text))
            {
                return lineStep;
            }
            EnsureIndex();
            var emojiSize = EmojiSize;
            var lines = EmojiCore.Layout(text, Math.Max(16f, wrapWidth), emojiSize,
                s => ImGui.CalcTextSize(s).X, _index?.Bases);
            var atlas = Atlas();
            var textCol = ColorUtils.ToU32(ColorUtils.WithAlpha(color, alpha));
            var tint = ColorUtils.ToU32(new Vector4(1f, 1f, 1f, alpha));
            var y = origin.Y;
            var emojiYOfs = (lineH - emojiSize) * 0.5f;
            foreach (var line in lines)
            {
                foreach (var atom in line.Atoms)
                {
                    var p = new Vector2(origin.X + atom.X, y);
                    if (!atom.IsEmoji)
                    {
                        drawList.AddText(p, textCol, atom.Text);
                        continue;
                    }
                    if (atlas != null && TrySprite(atom.Text, out var uv0, out var uv1))
                    {
                        var eMin = new Vector2(p.X, y + emojiYOfs);
                        drawList.AddImage(atlas.Handle, eMin, eMin + new Vector2(emojiSize), uv0, uv1, tint);
                    }
                    else
                    {
                        // sequence with no sprite (e.g. country flag): honest font fallback
                        drawList.AddText(p, textCol, atom.Text);
                    }
                }
                y += lineStep;
            }
            return lines.Count * lineStep;
        }
    }
}
