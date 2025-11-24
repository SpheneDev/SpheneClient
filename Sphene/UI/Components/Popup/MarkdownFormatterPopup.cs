using System;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;

using Sphene.UI.Components;

namespace Sphene.UI.Components.Popup;

public static class MarkdownFormatterPopup
{
    private static bool _isOpen = false;
    private static string _inputText = "";
    
    // Toggle states for formatting
    private static bool _isBold = false;
    private static bool _isUnderline = false;
    private static int _headerLevel = 0; // 0 = no header, 1-3 = header levels
    private static bool _hasColor = false;
    private static Vector4 _selectedColor = new Vector4(1.0f, 0.0f, 0.0f, 1.0f);
    private static bool _showColorPicker = false;

    public static void Open()
    {
        _isOpen = true;
        ResetToggles();
    }

    public static void Close()
    {
        _isOpen = false;
        _showColorPicker = false;
        ResetToggles();
    }

    private static void ResetToggles()
    {
        _isBold = false;
        _isUnderline = false;
        _headerLevel = 0;
        _hasColor = false;
    }

    public static void Draw()
    {
        if (!_isOpen) return;

        ImGui.SetNextWindowSize(new Vector2(800, 600), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Markdown Formatter", ref _isOpen, ImGuiWindowFlags.None))
        {
            DrawToolbar();
            ImGui.Separator();

            var contentRegion = ImGui.GetContentRegionAvail();
            var inputWidth = contentRegion.X * 0.5f - 5;

            // Input section
            ImGui.BeginChild("InputSection", new Vector2(inputWidth, contentRegion.Y - 40), true);
            ImGui.Text("Input Text:");
            ImGui.Separator();
            
            if (ImGui.InputTextMultiline("##input", ref _inputText, 10000, new Vector2(-1, -1)))
            {
                // Text changed, update formatted output
                UpdateFormattedText();
            }
            ImGui.EndChild();

            ImGui.SameLine();

            // Preview section
            ImGui.BeginChild("PreviewSection", new Vector2(-1, contentRegion.Y - 40), true);
            ImGui.Text("Preview:");
            ImGui.Separator();

            string formattedText = GetFormattedText();
            if (!string.IsNullOrEmpty(formattedText))
            {
                MarkdownRenderer.RenderMarkdown(formattedText);
            }
            else
            {
                ImGui.TextDisabled("Enter text to see preview...");
            }

            ImGui.EndChild();

            // Bottom buttons
            ImGui.Separator();
            DrawBottomButtons();
        }
        ImGui.End();

        // Color picker popup
        if (_showColorPicker)
        {
            DrawColorPicker();
        }
    }

    private static void DrawToolbar()
    {
        ImGui.Text("Formatting Tools:");

        // First row of toggle buttons
        bool boldPressed = ImGui.Button(_isBold ? "Bold [ON]" : "Bold [OFF]");
        if (boldPressed)
        {
            _isBold = !_isBold;
            UpdateFormattedText();
        }
        ImGui.SameLine();
        
        bool underlinePressed = ImGui.Button(_isUnderline ? "Underline [ON]" : "Underline [OFF]");
        if (underlinePressed)
        {
            _isUnderline = !_isUnderline;
            UpdateFormattedText();
        }

        // Second row - Headers and Color
        if (ImGui.Button(_headerLevel == 1 ? "H1 [ON]" : "H1 [OFF]"))
        {
            _headerLevel = _headerLevel == 1 ? 0 : 1;
            UpdateFormattedText();
        }
        ImGui.SameLine();
        
        if (ImGui.Button(_headerLevel == 2 ? "H2 [ON]" : "H2 [OFF]"))
        {
            _headerLevel = _headerLevel == 2 ? 0 : 2;
            UpdateFormattedText();
        }
        ImGui.SameLine();
        
        if (ImGui.Button(_headerLevel == 3 ? "H3 [ON]" : "H3 [OFF]"))
        {
            _headerLevel = _headerLevel == 3 ? 0 : 3;
            UpdateFormattedText();
        }
        ImGui.SameLine();
        
        if (ImGui.Button(_hasColor ? "Color [ON]" : "Color [OFF]"))
        {
            if (_hasColor)
            {
                _hasColor = false;
                UpdateFormattedText();
            }
            else
            {
                _showColorPicker = true;
            }
        }
        
        if (_hasColor)
        {
            ImGui.SameLine();
            if (ImGui.Button("Change Color"))
            {
                _showColorPicker = true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Remove Color"))
            {
                _hasColor = false;
                UpdateFormattedText();
            }
            
            // Show live color preview
            ImGui.SameLine();
            ImGui.Text("Preview:");
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, _selectedColor);
            ImGui.Text("Sample Text");
            ImGui.PopStyleColor();
        }

        // Show current formatting state
        ImGui.NewLine();
        ImGui.Text("Current Format: " + GetFormattingDescription());
    }

    private static void DrawColorPicker()
    {
        if (ImGui.Begin("Color Picker", ref _showColorPicker, ImGuiWindowFlags.AlwaysAutoResize))
        {
            Vector4 previousColor = _selectedColor;
            
            // Color picker with immediate preview
            if (ImGui.ColorPicker4("##colorpicker", ref _selectedColor))
            {
                // Color changed - immediately update preview
                _hasColor = true;
                UpdateFormattedText();
            }
            
            if (ImGui.Button("Done"))
            {
                _hasColor = true;
                _showColorPicker = false;
                UpdateFormattedText();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                // Restore previous color if cancelled
                _selectedColor = previousColor;
                _showColorPicker = false;
                UpdateFormattedText();
            }
        }
        ImGui.End();
    }

    private static void DrawBottomButtons()
    {
        if (ImGui.Button("Copy Formatted Text"))
        {
            string formattedText = GetFormattedText();
            if (!string.IsNullOrEmpty(formattedText))
            {
                ImGui.SetClipboardText(formattedText);
            }
        }
        ImGui.SameLine();
        
        if (ImGui.Button("Copy Raw Text") && !string.IsNullOrEmpty(_inputText))
        {
            ImGui.SetClipboardText(_inputText);
        }
        ImGui.SameLine();
        
        if (ImGui.Button("Clear"))
        {
            _inputText = "";
            UpdateFormattedText();
        }
        ImGui.SameLine();
        
        if (ImGui.Button("Reset Format"))
        {
            ResetToggles();
            UpdateFormattedText();
        }
        ImGui.SameLine();
        
        if (ImGui.Button("Close"))
        {
            Close();
        }
    }

    private static void UpdateFormattedText()
    {
        // This method is called whenever formatting changes
        // The actual formatting is applied in GetFormattedText()
    }

    private static string GetFormattedText()
    {
        if (string.IsNullOrEmpty(_inputText))
            return "";

        string result = _inputText;
        string headerPrefix = "";

        // Step 1: Prepare header prefix (but don't apply it yet)
        if (_headerLevel > 0)
        {
            headerPrefix = new string('#', _headerLevel) + " ";
        }

        // Step 2: Apply Color (innermost)
        if (_hasColor)
        {
            string colorHex = ColorToHex(_selectedColor);
            result = $"<color={colorHex}>" + result + "</color>";
        }

        // Step 3: Apply Underline
        if (_isUnderline)
        {
            result = "<u>" + result + "</u>";
        }

        // Step 4: Apply Bold (outside color and underline)
        if (_isBold)
        {
            result = "**" + result + "**";
        }

        // Step 5: Apply header prefix at the very end (outermost)
        if (!string.IsNullOrEmpty(headerPrefix))
        {
            result = headerPrefix + result;
        }

        return result;
    }

    private static string GetFormattingDescription()
    {
        var parts = new System.Collections.Generic.List<string>();
        
        if (_headerLevel > 0)
            parts.Add($"H{_headerLevel}");
        if (_isBold)
            parts.Add("Bold");
        if (_isUnderline)
            parts.Add("Underline");
        if (_hasColor)
            parts.Add($"Color({ColorToHex(_selectedColor)})");

        return parts.Count > 0 ? string.Join(" + ", parts) : "None";
    }

    private static string ColorToHex(Vector4 color)
    {
        var r = (int)(color.X * 255);
        var g = (int)(color.Y * 255);
        var b = (int)(color.Z * 255);
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}
