using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;

namespace Sphene.UI.Components;

public static class MarkdownRenderer
{
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

    public static void RenderMarkdown(string text, float wrapPos = 0)
    {
        if (string.IsNullOrEmpty(text))
            return;

        // Push zero item spacing to eliminate all spacing between lines
        using var itemSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            RenderLine(line, wrapPos);
        }
    }

    private static void RenderLine(string line, float wrapPos)
    {
        if (string.IsNullOrEmpty(line))
        {
            // Empty lines should take up the same space as a normal text line
            ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeight()));
            return;
        }

        // Check for headers
        if (line.StartsWith("# ", StringComparison.Ordinal))
        {
            RenderHeader(line.Substring(2), 1.5f);
            return;
        }
        if (line.StartsWith("## ", StringComparison.Ordinal))
        {
            RenderHeader(line.Substring(3), 1.3f);
            return;
        }
        if (line.StartsWith("### ", StringComparison.Ordinal))
        {
            RenderHeader(line.Substring(4), 1.1f);
            return;
        }

        // Parse inline formatting
        if (wrapPos > 0)
            ImGui.PushTextWrapPos(wrapPos);
        var segments = ParseInlineFormatting(line);
        RenderSegments(segments);
        if (wrapPos > 0)
            ImGui.PopTextWrapPos();
        // Try completely removing any spacing between lines
    }

    private static void RenderHeader(string text, float scale)
    {
        using var font = ImRaii.PushFont(UiBuilder.DefaultFont);
        ImGui.SetWindowFontScale(scale);
        using var color = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudWhite);
        
        // Parse inline formatting for headers too
        var segments = ParseInlineFormatting(text);
        RenderSegments(segments);
        
        ImGui.SetWindowFontScale(1.0f);
        // Use smaller spacing after headers instead of full Spacing
        ImGuiHelpers.ScaledDummy(0.5f);
    }

    private static List<TextSegment> ParseInlineFormatting(string text)
    {
        var segments = new List<TextSegment>();
        var currentPos = 0;

        // Process text character by character to handle nested formatting
        while (currentPos < text.Length)
        {
            var segment = ParseNextSegment(text, currentPos);
            if (segment != null)
            {
                segments.Add(segment);
                currentPos = segment.Index + segment.Length;
            }
            else
            {
                // Add remaining text as normal
                segments.Add(new TextSegment
                {
                    Text = text.Substring(currentPos),
                    Type = FormatType.Normal,
                    Index = currentPos,
                    Length = text.Length - currentPos
                });
                break;
            }
        }

        return segments;
    }

    private static TextSegment? ParseNextSegment(string text, int startPos)
    {
        if (startPos >= text.Length)
            return null;

        // Check for bold italic formatting (***text***)
        if (startPos + 2 < text.Length && text[startPos] == '*' && text[startPos + 1] == '*' && text[startPos + 2] == '*')
        {
            var endPos = text.IndexOf("***", startPos + 3, StringComparison.Ordinal);
            if (endPos != -1)
            {
                var content = text.Substring(startPos + 3, endPos - startPos - 3);
                var (finalText, formatType, color) = ParseNestedFormatting(content, FormatType.BoldItalic);
                
                return new TextSegment
                {
                    Text = finalText,
                    Type = formatType,
                    Color = color,
                    Index = startPos,
                    Length = endPos + 3 - startPos
                };
            }
        }

        // Check for bold formatting
        if (startPos + 1 < text.Length && text[startPos] == '*' && text[startPos + 1] == '*')
        {
            var endPos = text.IndexOf("**", startPos + 2, StringComparison.Ordinal);
            if (endPos != -1)
            {
                var content = text.Substring(startPos + 2, endPos - startPos - 2);
                var (finalText, formatType, color) = ParseNestedFormatting(content, FormatType.Bold);
                
                return new TextSegment
                {
                    Text = finalText,
                    Type = formatType,
                    Color = color,
                    Index = startPos,
                    Length = endPos + 2 - startPos
                };
            }
        }

        // Check for italic formatting
        if (text[startPos] == '*')
        {
            var endPos = text.IndexOf('*', startPos + 1);
            if (endPos != -1)
            {
                var content = text.Substring(startPos + 1, endPos - startPos - 1);
                var (finalText, formatType, color) = ParseNestedFormatting(content, FormatType.Italic);
                
                return new TextSegment
                {
                    Text = finalText,
                    Type = formatType,
                    Color = color,
                    Index = startPos,
                    Length = endPos + 1 - startPos
                };
            }
        }

        // Check for color formatting
        var colorRegex = new Regex(@"<color=(?<color>[#\w]+)>(?<text>.+?)</color>", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromMilliseconds(50));
        var match = colorRegex.Match(text, startPos);
        if (match.Success && match.Index == startPos)
        {
            var colorValue = match.Groups["color"].Value.ToLower();
            var colorText = match.Groups["text"].Value;
            Vector4 color;
            
            if (colorValue.StartsWith('#'))
            {
                color = ParseHexColor(colorValue);
            }
            else
            {
                color = ColorMap.ContainsKey(colorValue) ? ColorMap[colorValue] : ImGuiColors.DalamudWhite;
            }

            var (finalText, formatType, _) = ParseNestedFormatting(colorText, FormatType.Color);
            
            return new TextSegment
            {
                Text = finalText,
                Type = formatType,
                Color = color,
                Index = startPos,
                Length = match.Length
            };
        }

        // Check for underline formatting
        var underlineRegex = new Regex(@"<u>(?<text>.+?)</u>", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromMilliseconds(50));
        var underlineMatch = underlineRegex.Match(text, startPos);
        if (underlineMatch.Success && underlineMatch.Index == startPos)
        {
            var underlineText = underlineMatch.Groups["text"].Value;
            var (finalText, formatType, color) = ParseNestedFormatting(underlineText, FormatType.Underline);
            
            return new TextSegment
            {
                Text = finalText,
                Type = formatType,
                Color = color,
                Index = startPos,
                Length = underlineMatch.Length
            };
        }

        // Check for code formatting
        if (text[startPos] == '`')
        {
            var endPos = text.IndexOf('`', startPos + 1);
            if (endPos != -1)
            {
                var content = text.Substring(startPos + 1, endPos - startPos - 1);
                return new TextSegment
                {
                    Text = content,
                    Type = FormatType.Code,
                    Index = startPos,
                    Length = endPos + 1 - startPos
                };
            }
        }

        // Find next formatting marker or return single character
        var nextMarkerPos = FindNextFormattingMarker(text, startPos + 1);
        if (nextMarkerPos == -1)
        {
            return new TextSegment
            {
                Text = text.Substring(startPos),
                Type = FormatType.Normal,
                Index = startPos,
                Length = text.Length - startPos
            };
        }
        else
        {
            return new TextSegment
            {
                Text = text.Substring(startPos, nextMarkerPos - startPos),
                Type = FormatType.Normal,
                Index = startPos,
                Length = nextMarkerPos - startPos
            };
        }
    }

    private static (string text, FormatType type, Vector4 color) ParseNestedFormatting(string content, FormatType baseType)
    {
        var finalText = content;
        var formatType = baseType;
        var color = ImGuiColors.DalamudWhite;
        
        // Check for color formatting
        var colorMatch = Regex.Match(content, @"<color=(?<color>[#\w]+)>(?<text>.+?)</color>", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromMilliseconds(50));
        if (colorMatch.Success)
        {
            var colorValue = colorMatch.Groups["color"].Value.ToLower();
            finalText = colorMatch.Groups["text"].Value;
            
            if (colorValue.StartsWith('#'))
            {
                color = ParseHexColor(colorValue);
            }
            else
            {
                color = ColorMap.ContainsKey(colorValue) ? ColorMap[colorValue] : ImGuiColors.DalamudWhite;
            }
            
            // Combine with base type
            formatType = CombineFormatTypes(baseType, FormatType.Color);
        }
        
        // Check for underline formatting
        var underlineMatch = Regex.Match(finalText, @"<u>(?<text>.+?)</u>", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromMilliseconds(50));
        if (underlineMatch.Success)
        {
            finalText = underlineMatch.Groups["text"].Value;
            formatType = CombineFormatTypes(formatType, FormatType.Underline);
        }
        
        return (finalText, formatType, color);
    }
    
    private static FormatType CombineFormatTypes(FormatType type1, FormatType type2)
    {
        var types = new HashSet<FormatType> { type1, type2 };
        
        // Remove Normal type if present
        types.Remove(FormatType.Normal);
        
        if (types.Contains(FormatType.BoldItalic))
        {
            types.Remove(FormatType.BoldItalic);
            types.Add(FormatType.Bold);
            types.Add(FormatType.Italic);
        }
        
        var hasBold = types.Contains(FormatType.Bold) || types.Contains(FormatType.BoldColor) || types.Contains(FormatType.BoldItalicColor);
        var hasItalic = types.Contains(FormatType.Italic) || types.Contains(FormatType.ItalicColor) || types.Contains(FormatType.BoldItalicColor);
        var hasColor = types.Contains(FormatType.Color) || types.Contains(FormatType.BoldColor) || types.Contains(FormatType.ItalicColor) || types.Contains(FormatType.BoldItalicColor) || types.Contains(FormatType.ColorUnderline);
        var hasUnderline = types.Contains(FormatType.Underline) || types.Contains(FormatType.ColorUnderline) || types.Contains(FormatType.BoldUnderline) || types.Contains(FormatType.ItalicUnderline);
        
        // Return the appropriate combined type
        if (hasBold && hasItalic && hasColor && hasUnderline)
            return FormatType.BoldItalicColorUnderline;
        if (hasBold && hasItalic && hasColor)
            return FormatType.BoldItalicColor;
        if (hasBold && hasItalic && hasUnderline)
            return FormatType.BoldItalicUnderline;
        if (hasItalic && hasColor && hasUnderline)
            return FormatType.ItalicColorUnderline;
        if (hasBold && hasColor && hasUnderline)
            return FormatType.BoldColorUnderline;
        if (hasBold && hasItalic)
            return FormatType.BoldItalic;
        if (hasBold && hasColor)
            return FormatType.BoldColor;
        if (hasBold && hasUnderline)
            return FormatType.BoldUnderline;
        if (hasItalic && hasColor)
            return FormatType.ItalicColor;
        if (hasItalic && hasUnderline)
            return FormatType.ItalicUnderline;
        if (hasColor && hasUnderline)
            return FormatType.ColorUnderline;
        if (hasBold)
            return FormatType.Bold;
        if (hasItalic)
            return FormatType.Italic;
        if (hasColor)
            return FormatType.Color;
        if (hasUnderline)
            return FormatType.Underline;
            
        return FormatType.Normal;
    }

    private static int FindNextFormattingMarker(string text, int startPos)
    {
        for (int i = startPos; i < text.Length; i++)
        {
            if (text[i] == '*' || text[i] == '`' || text[i] == '<')
            {
                return i;
            }
        }
        return -1;
    }


    private static Vector4 ParseHexColor(string hexColor)
    {
        try
        {
            // Remove # if present
            if (hexColor.StartsWith('#'))
                hexColor = hexColor.Substring(1);
            
            // Parse RGB components
            if (hexColor.Length == 6)
            {
                var r = Convert.ToInt32(hexColor.Substring(0, 2), 16) / 255f;
                var g = Convert.ToInt32(hexColor.Substring(2, 2), 16) / 255f;
                var b = Convert.ToInt32(hexColor.Substring(4, 2), 16) / 255f;
                return new Vector4(r, g, b, 1.0f);
            }
        }
        catch
        {
            // Fall back to white if parsing fails
        }
        
        return ImGuiColors.DalamudWhite;
    }

    private static void RenderSegments(List<TextSegment> segments)
    {
        foreach (var segment in segments)
        {
            switch (segment.Type)
            {
                case FormatType.Normal:
                    ImGui.TextUnformatted(segment.Text);
                    break;
                
                case FormatType.Bold:
                    using (ImRaii.PushFont(UiBuilder.DefaultFont))
                    {
                        ImGui.SetWindowFontScale(1.1f);
                        ImGui.TextUnformatted(segment.Text);
                        ImGui.SetWindowFontScale(1.0f);
                    }
                    break;
                
                case FormatType.Italic:
                    // Since ImGui doesn't have italic, we'll use a slightly different color
                    using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3))
                    {
                        ImGui.TextUnformatted(segment.Text);
                    }
                    break;
                
                case FormatType.Color:
                    using (ImRaii.PushColor(ImGuiCol.Text, segment.Color))
                    {
                        ImGui.TextUnformatted(segment.Text);
                    }
                    break;
                
                case FormatType.Code:
                    using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey2))
                    {
                        ImGui.TextUnformatted($" {segment.Text} ");
                    }
                    break;
                
                case FormatType.BoldColor:
                    using (ImRaii.PushColor(ImGuiCol.Text, segment.Color))
                    using (ImRaii.PushFont(UiBuilder.DefaultFont))
                    {
                        ImGui.SetWindowFontScale(1.1f);
                        ImGui.TextUnformatted(segment.Text);
                        ImGui.SetWindowFontScale(1.0f);
                    }
                    break;
                
                case FormatType.ItalicColor:
                    using (ImRaii.PushColor(ImGuiCol.Text, segment.Color))
                    {
                        ImGui.TextUnformatted(segment.Text);
                    }
                    break;
                
                case FormatType.BoldItalicColor:
                    using (ImRaii.PushColor(ImGuiCol.Text, segment.Color))
                    using (ImRaii.PushFont(UiBuilder.DefaultFont))
                    {
                        ImGui.SetWindowFontScale(1.1f);
                        ImGui.TextUnformatted(segment.Text);
                        ImGui.SetWindowFontScale(1.0f);
                    }
                    break;
                
                case FormatType.BoldItalic:
                    using (ImRaii.PushFont(UiBuilder.DefaultFont))
                    {
                        ImGui.SetWindowFontScale(1.1f);
                        ImGui.TextUnformatted(segment.Text);
                        ImGui.SetWindowFontScale(1.0f);
                    }
                    break;
                
                case FormatType.Underline:
                    // ImGui doesn't have native underline support, so we'll draw a line under the text
                    var textSize = ImGui.CalcTextSize(segment.Text);
                    var cursorPos = ImGui.GetCursorScreenPos();
                    ImGui.TextUnformatted(segment.Text);
                    var drawList = ImGui.GetWindowDrawList();
                    drawList.AddLine(
                        new Vector2(cursorPos.X, cursorPos.Y + textSize.Y),
                        new Vector2(cursorPos.X + textSize.X, cursorPos.Y + textSize.Y),
                        ImGui.GetColorU32(ImGuiCol.Text),
                        1.0f
                    );
                    break;
                
                case FormatType.ColorUnderline:
                    using (ImRaii.PushColor(ImGuiCol.Text, segment.Color))
                    {
                        var colorUnderlineTextSize = ImGui.CalcTextSize(segment.Text);
                        var colorUnderlineCursorPos = ImGui.GetCursorScreenPos();
                        ImGui.TextUnformatted(segment.Text);
                        var colorUnderlineDrawList = ImGui.GetWindowDrawList();
                        colorUnderlineDrawList.AddLine(
                            new Vector2(colorUnderlineCursorPos.X, colorUnderlineCursorPos.Y + colorUnderlineTextSize.Y),
                            new Vector2(colorUnderlineCursorPos.X + colorUnderlineTextSize.X, colorUnderlineCursorPos.Y + colorUnderlineTextSize.Y),
                            ImGui.GetColorU32(segment.Color),
                            1.0f
                        );
                    }
                    break;
                
                case FormatType.BoldUnderline:
                    using (ImRaii.PushFont(UiBuilder.DefaultFont))
                    {
                        ImGui.SetWindowFontScale(1.1f);
                        var boldUnderlineTextSize = ImGui.CalcTextSize(segment.Text);
                        var boldUnderlineCursorPos = ImGui.GetCursorScreenPos();
                        ImGui.TextUnformatted(segment.Text);
                        ImGui.SetWindowFontScale(1.0f);
                        var boldUnderlineDrawList = ImGui.GetWindowDrawList();
                        boldUnderlineDrawList.AddLine(
                            new Vector2(boldUnderlineCursorPos.X, boldUnderlineCursorPos.Y + boldUnderlineTextSize.Y),
                            new Vector2(boldUnderlineCursorPos.X + boldUnderlineTextSize.X, boldUnderlineCursorPos.Y + boldUnderlineTextSize.Y),
                            ImGui.GetColorU32(ImGuiCol.Text),
                            1.0f
                        );
                    }
                    break;
                
                case FormatType.ItalicUnderline:
                    using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3))
                    {
                        var italicUnderlineTextSize = ImGui.CalcTextSize(segment.Text);
                        var italicUnderlineCursorPos = ImGui.GetCursorScreenPos();
                        ImGui.TextUnformatted(segment.Text);
                        var italicUnderlineDrawList = ImGui.GetWindowDrawList();
                        italicUnderlineDrawList.AddLine(
                            new Vector2(italicUnderlineCursorPos.X, italicUnderlineCursorPos.Y + italicUnderlineTextSize.Y),
                            new Vector2(italicUnderlineCursorPos.X + italicUnderlineTextSize.X, italicUnderlineCursorPos.Y + italicUnderlineTextSize.Y),
                            ImGui.GetColorU32(ImGuiColors.DalamudGrey3),
                            1.0f
                        );
                    }
                    break;
                
                case FormatType.BoldColorUnderline:
                    using (ImRaii.PushColor(ImGuiCol.Text, segment.Color))
                    using (ImRaii.PushFont(UiBuilder.DefaultFont))
                    {
                        ImGui.SetWindowFontScale(1.1f);
                        var boldColorUnderlineTextSize = ImGui.CalcTextSize(segment.Text);
                        var boldColorUnderlineCursorPos = ImGui.GetCursorScreenPos();
                        ImGui.TextUnformatted(segment.Text);
                        ImGui.SetWindowFontScale(1.0f);
                        var boldColorUnderlineDrawList = ImGui.GetWindowDrawList();
                        boldColorUnderlineDrawList.AddLine(
                            new Vector2(boldColorUnderlineCursorPos.X, boldColorUnderlineCursorPos.Y + boldColorUnderlineTextSize.Y),
                            new Vector2(boldColorUnderlineCursorPos.X + boldColorUnderlineTextSize.X, boldColorUnderlineCursorPos.Y + boldColorUnderlineTextSize.Y),
                            ImGui.GetColorU32(segment.Color),
                            1.0f
                        );
                    }
                    break;
                
                case FormatType.ItalicColorUnderline:
                    using (ImRaii.PushColor(ImGuiCol.Text, segment.Color))
                    {
                        var italicColorUnderlineTextSize = ImGui.CalcTextSize(segment.Text);
                        var italicColorUnderlineCursorPos = ImGui.GetCursorScreenPos();
                        ImGui.TextUnformatted(segment.Text);
                        var italicColorUnderlineDrawList = ImGui.GetWindowDrawList();
                        italicColorUnderlineDrawList.AddLine(
                            new Vector2(italicColorUnderlineCursorPos.X, italicColorUnderlineCursorPos.Y + italicColorUnderlineTextSize.Y),
                            new Vector2(italicColorUnderlineCursorPos.X + italicColorUnderlineTextSize.X, italicColorUnderlineCursorPos.Y + italicColorUnderlineTextSize.Y),
                            ImGui.GetColorU32(segment.Color),
                            1.0f
                        );
                    }
                    break;
                
                case FormatType.BoldItalicUnderline:
                    using (ImRaii.PushFont(UiBuilder.DefaultFont))
                    {
                        ImGui.SetWindowFontScale(1.1f);
                        var boldItalicUnderlineTextSize = ImGui.CalcTextSize(segment.Text);
                        var boldItalicUnderlineCursorPos = ImGui.GetCursorScreenPos();
                        ImGui.TextUnformatted(segment.Text);
                        ImGui.SetWindowFontScale(1.0f);
                        var boldItalicUnderlineDrawList = ImGui.GetWindowDrawList();
                        boldItalicUnderlineDrawList.AddLine(
                            new Vector2(boldItalicUnderlineCursorPos.X, boldItalicUnderlineCursorPos.Y + boldItalicUnderlineTextSize.Y),
                            new Vector2(boldItalicUnderlineCursorPos.X + boldItalicUnderlineTextSize.X, boldItalicUnderlineCursorPos.Y + boldItalicUnderlineTextSize.Y),
                            ImGui.GetColorU32(ImGuiCol.Text),
                            1.0f
                        );
                    }
                    break;
                
                case FormatType.BoldItalicColorUnderline:
                    using (ImRaii.PushColor(ImGuiCol.Text, segment.Color))
                    using (ImRaii.PushFont(UiBuilder.DefaultFont))
                    {
                        ImGui.SetWindowFontScale(1.1f);
                        var boldItalicColorUnderlineTextSize = ImGui.CalcTextSize(segment.Text);
                        var boldItalicColorUnderlineCursorPos = ImGui.GetCursorScreenPos();
                        ImGui.TextUnformatted(segment.Text);
                        ImGui.SetWindowFontScale(1.0f);
                        var boldItalicColorUnderlineDrawList = ImGui.GetWindowDrawList();
                        boldItalicColorUnderlineDrawList.AddLine(
                            new Vector2(boldItalicColorUnderlineCursorPos.X, boldItalicColorUnderlineCursorPos.Y + boldItalicColorUnderlineTextSize.Y),
                            new Vector2(boldItalicColorUnderlineCursorPos.X + boldItalicColorUnderlineTextSize.X, boldItalicColorUnderlineCursorPos.Y + boldItalicColorUnderlineTextSize.Y),
                            ImGui.GetColorU32(segment.Color),
                            1.0f
                        );
                    }
                    break;
            }
            
            // Add same line for inline elements
            if (segment != segments[segments.Count - 1])
            {
                ImGui.SameLine(0, 0);
            }
        }
    }

    private sealed class TextSegment
    {
        public string Text { get; set; } = string.Empty;
        public FormatType Type { get; set; }
        public Vector4 Color { get; set; }
        public int Index { get; set; }
        public int Length { get; set; }
    }

    private enum FormatType
    {
        Normal,
        Bold,
        Italic,
        Color,
        Code,
        BoldColor,
        ItalicColor,
        BoldItalic,
        BoldItalicColor,
        Underline,
        ColorUnderline,
        BoldUnderline,
        ItalicUnderline,
        BoldColorUnderline,
        ItalicColorUnderline,
        BoldItalicUnderline,
        BoldItalicColorUnderline
    }
}
