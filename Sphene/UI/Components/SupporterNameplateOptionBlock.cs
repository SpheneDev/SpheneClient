using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Sphene.Services;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Components;

public static class SupporterNameplateOptionBlock
{
    public static void DrawShowSupporterNameplateOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "ShowSupporterNameplate")
    {
        var show = configService.Current.ShowSupporterNameplate;
        if (SpheneUI.Controls.Toggle("Show Supporter Nameplate Indicators##" + blockId, ref show))
        {
            configService.Current.ShowSupporterNameplate = show;
            configService.Save();
        }

        SpheneUI.Controls.HelpText("Display a symbol, text or the Sphene icon next to the nameplates of Sphene supporters.");
    }

    public static void DrawSupporterIconOverlayOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "SupporterIconOverlay")
    {
        var enabled = configService.Current.SupporterIconEnabled;
        if (SpheneUI.Controls.Toggle("Show Sphene icon above supporter nameplates##" + blockId, ref enabled))
        {
            configService.Current.SupporterIconEnabled = enabled;
            configService.Save();
        }

        SpheneUI.Controls.HelpText("Draws the Sphene icon above the nameplate of every supporter.");
    }

    public static void DrawSupporterIconLabelOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "SupporterIconLabel")
    {
        var enabled = configService.Current.SupporterIconLabelEnabled;
        if (SpheneUI.Controls.Toggle("Show label text next to the Sphene icon##" + blockId, ref enabled))
        {
            configService.Current.SupporterIconLabelEnabled = enabled;
            configService.Save();
        }

        SpheneUI.Controls.HelpText("Draws the configured icon label text as an overlay next to the Sphene icon.");
    }

    public static void DrawSupporterDepthOcclusionOption()
    {
        SpheneUI.Controls.HelpText("The old CPU depth readback is retired. Use GPU occlusion for per-pixel clipping, or leave it off for the original collision checks.");
    }

    public static void DrawGpuDepthOcclusionOption(SpheneConfigService configService, string blockId = "GpuDepthOcclusion")
    {
        var enabled = configService.Current.SupporterNameplateGpuOcclusion;
        if (SpheneUI.Controls.Toggle("GPU occlusion for supporter icons and labels##" + blockId, ref enabled))
        {
            configService.Current.SupporterNameplateGpuOcclusion = enabled;
            configService.Save();
        }
        SpheneUI.Controls.HelpText("Depth-tests icons, animations, labels and outlines per pixel against the scene and native nameplate glyph pixels. Enabled by default; falls back to collision checks if the GPU path is unavailable.");
    }

    public static void DrawSupporterDebugOverlayOption(SpheneConfigService configService, string blockId = "SupporterDebugOverlay")
    {
        var enabled = configService.Current.SupporterNameplateDebugOverlay;
        if (SpheneUI.Controls.Toggle("Show nameplate debug overlay##" + blockId, ref enabled))
        {
            configService.Current.SupporterNameplateDebugOverlay = enabled;
            configService.Save();
        }
        SpheneUI.Controls.HelpText("Draws diagnostic markers on every nameplate: the measured text block frame, name/title bands and the game's plate anchor. Pick the layers below; intended for development testing only.");
    }

    public static void DrawSupporterDebugOverlayFiltersOption(SpheneConfigService configService, string blockId = "SupporterDebugOverlayFilters")
    {
        var overlay = configService.Current.SupporterNameplateDebugOverlay;
        using (ImRaii.Disabled(!overlay))
        {
            ImGui.Indent();
            var bands = configService.Current.SupporterNameplateDebugBands;
            if (SpheneUI.Controls.Toggle("Name and title line bands##" + blockId, ref bands))
            {
                configService.Current.SupporterNameplateDebugBands = bands;
                configService.Save();
            }
            var quads = configService.Current.SupporterNameplateDebugQuads;
            if (SpheneUI.Controls.Toggle("Mask quads##" + blockId, ref quads))
            {
                configService.Current.SupporterNameplateDebugQuads = quads;
                configService.Save();
            }
            var hudQuads = configService.Current.SupporterNameplateDebugHudQuads;
            if (SpheneUI.Controls.Toggle("HUD mask quads##" + blockId, ref hudQuads))
            {
                configService.Current.SupporterNameplateDebugHudQuads = hudQuads;
                configService.Save();
            }
            var layerMask = configService.Current.SupporterNameplateHudLayerMask;
            var layerChanged = false;
            SpheneUI.Text.Muted("HUD layers");
            for (var l = 0; l < 14; l++)
            {
                if (l > 0)
                    ImGui.SameLine();
                var on = (layerMask & (1 << l)) != 0;
                if (SpheneUI.Controls.Switch("##hudlayer" + l + blockId, ref on))
                {
                    layerMask = on ? layerMask | (1 << l) : layerMask & ~(1 << l);
                    layerChanged = true;
                }
                ImGui.SameLine();
                SpheneUI.Text.Muted(l < 13 ? (l + 1).ToString() : "D");
            }
            if (layerChanged)
            {
                configService.Current.SupporterNameplateHudLayerMask = layerMask;
                configService.Save();
            }
            var nineOrder = configService.Current.SupporterNameplateHudNineOrder;
            if (SpheneUI.Controls.SliderInt("Nine-grid part order##" + blockId, ref nineOrder, 0, 3, "mode %d"))
            {
                configService.Current.SupporterNameplateHudNineOrder = nineOrder;
                configService.Save();
            }
            var block = configService.Current.SupporterNameplateDebugBlock;
            if (SpheneUI.Controls.Toggle("Text block frame##" + blockId, ref block))
            {
                configService.Current.SupporterNameplateDebugBlock = block;
                configService.Save();
            }
            var anchors = configService.Current.SupporterNameplateDebugAnchors;
            if (SpheneUI.Controls.Toggle("Plate anchors##" + blockId, ref anchors))
            {
                configService.Current.SupporterNameplateDebugAnchors = anchors;
                configService.Save();
            }
            var labels = configService.Current.SupporterNameplateDebugLabels;
            if (SpheneUI.Controls.Toggle("Per-plate info labels##" + blockId, ref labels))
            {
                configService.Current.SupporterNameplateDebugLabels = labels;
                configService.Save();
            }
            var fullBound = configService.Current.SupporterNameplateHudFullBound;
            if (SpheneUI.Controls.Toggle("Full-screen HUD capture##" + blockId, ref fullBound))
            {
                configService.Current.SupporterNameplateHudFullBound = fullBound;
                configService.Save();
            }
            ImGui.Unindent();
        }
        SpheneUI.Controls.HelpText("Selects which diagnostic layers the overlay draws. Bands mark the reconstructed name/title lines; mask quads are the exact pixels cut out of the supporter icon. HUD mask quads draw every captured window/HUD mask with its sampled texture and slot+part label · independent of the nameplate quads. Full-screen HUD capture scans the whole screen instead of the nameplate area, so no nameplate needs to sit behind a window. Nine-grid part order cycles candidate cell mappings for multi-part window nine-grids: 0 row-major, 1 corners first, 2 corners then sides, 3 column-major · compare the sampled textures against the real window chrome. HUD layers toggles the game's draw layers 1-13 individually · each quad is drawn in its layer's color, D marks docked/hosted panels that paint inside another window.");
    }

    public static void DrawSupporterTestRectOption(SpheneConfigService configService, string blockId = "SupporterTestRect")
    {
        var enabled = configService.Current.SupporterNameplateTestRect;
        if (SpheneUI.Controls.Toggle("Show occlusion test rectangle##" + blockId, ref enabled))
        {
            configService.Current.SupporterNameplateTestRect = enabled;
            configService.Save();
        }
        SpheneUI.Controls.HelpText("Draws a solid rectangle through the same occlusion pipeline as the icon and label · letter-shaped holes show exactly where the glyph masks sit.");
        if (!enabled)
            return;
        var color = ImGui.ColorConvertU32ToFloat4(configService.Current.SupporterNameplateTestRectColor);
        if (SpheneUI.Controls.ColorEdit("Test rectangle color##" + blockId, ref color))
        {
            configService.Current.SupporterNameplateTestRectColor = ImGui.ColorConvertFloat4ToU32(color);
            configService.Save();
        }
        var width = configService.Current.SupporterNameplateTestRectWidth;
        if (SpheneUI.Controls.SliderInt("Test rectangle width##" + blockId, ref width, 10, 400, "%dpx"))
        {
            configService.Current.SupporterNameplateTestRectWidth = width;
            configService.Save();
        }
        var height = configService.Current.SupporterNameplateTestRectHeight;
        if (SpheneUI.Controls.SliderInt("Test rectangle height##" + blockId, ref height, 4, 200, "%dpx"))
        {
            configService.Current.SupporterNameplateTestRectHeight = height;
            configService.Save();
        }
        var selfMask = configService.Current.SupporterNameplateTestRectSelfMask;
        if (SpheneUI.Controls.Toggle("Cover own text (self-test)##" + blockId, ref selfMask))
        {
            configService.Current.SupporterNameplateTestRectSelfMask = selfMask;
            configService.Save();
        }
        if (selfMask)
            SpheneUI.Controls.HelpText("Stretches the rectangle across your own plate's text block and lets your own glyphs cut it · the holes should line up exactly with the letters on your plate.");
        var iconMaskScale = configService.Current.SupporterNameplateIconMaskScalePercent;
        if (SpheneUI.Controls.SliderInt("Icon mask scale##" + blockId, ref iconMaskScale, 25, 200, "%d%%"))
        {
            configService.Current.SupporterNameplateIconMaskScalePercent = iconMaskScale;
            configService.Save();
        }
        SpheneUI.Controls.HelpText("Scales the native icon occlusion quads (job/status/marker icons). Shrink it until the icon cutout matches the drawn icon exactly.");
        var gfdMaskScale = configService.Current.SupporterNameplateGfdMaskScalePercent;
        if (SpheneUI.Controls.SliderInt("Text icon mask scale##" + blockId, ref gfdMaskScale, 25, 200, "%d%%"))
        {
            configService.Current.SupporterNameplateGfdMaskScalePercent = gfdMaskScale;
            configService.Save();
        }
        var gfdMaskOffX = configService.Current.SupporterNameplateGfdMaskOffsetX;
        if (SpheneUI.Controls.SliderInt("Text icon mask offset X##" + blockId, ref gfdMaskOffX, -32, 32, "%dpx"))
        {
            configService.Current.SupporterNameplateGfdMaskOffsetX = gfdMaskOffX;
            configService.Save();
        }
        var gfdMaskOffY = configService.Current.SupporterNameplateGfdMaskOffsetY;
        if (SpheneUI.Controls.SliderInt("Text icon mask offset Y##" + blockId, ref gfdMaskOffY, -32, 32, "%dpx"))
        {
            configService.Current.SupporterNameplateGfdMaskOffsetY = gfdMaskOffY;
            configService.Save();
        }
        SpheneUI.Controls.HelpText("Tunes only the inline text-icon cutouts (status and « » icons inside the name text): scale shrinks around the icon center, the offsets nudge its position. Leaves glyphs and node icons untouched.");
        var iconOverlay = configService.Current.SupporterNameplateIconOverlay;
        if (SpheneUI.Controls.Toggle("Icon capture overlay##" + blockId, ref iconOverlay))
        {
            configService.Current.SupporterNameplateIconOverlay = iconOverlay;
            configService.Save();
        }
        if (iconOverlay)
            SpheneUI.Controls.HelpText("Draws the captured icon slots on screen: yellow box + slot label + the sampled texture region in magenta. Dimmed boxes were captured but emit no mask. Purely diagnostic.");
    }

    public static void DrawSupporterDebugLogOption(SpheneConfigService configService, string blockId = "SupporterDebugLog")
    {
        var log = configService.Current.SupporterNameplateDebugLog;
        if (SpheneUI.Controls.Toggle("Nameplate debug logs##" + blockId, ref log))
        {
            configService.Current.SupporterNameplateDebugLog = log;
            configService.Save();
        }
        SpheneUI.Controls.HelpText("Writes the recurring nameplate diagnostics to the log: per-tick plate/glyph traces, icon node captures, inline icon outcomes and startup gate signatures. Off keeps the log clean.");
        var dump = configService.Current.SupporterNameplateIconDump;
        if (SpheneUI.Controls.Toggle("Icon texture dump##" + blockId, ref dump))
        {
            configService.Current.SupporterNameplateIconDump = dump;
            configService.Save();
        }
        SpheneUI.Controls.HelpText("Writes the sampled icon texture pages and per-icon regions as PNGs into the plugin configuration directory · inspect which pixels a cutout actually taps. Off by default; dumps once per texture.");
    }

    public static void DrawSupporterAnchorExtrapolateOption(SpheneConfigService configService, string blockId = "SupporterAnchorExtrapolate")
    {
        var beta = configService.Current.SupporterNameplateAnchorExtrapolate;
        if (SpheneUI.Controls.SliderFloat("Anchor extrapolation##" + blockId, ref beta, -1.5f, 1.5f, "%.2f frames"))
        {
            configService.Current.SupporterNameplateAnchorExtrapolate = beta;
            configService.Save();
        }
        SpheneUI.Controls.HelpText("Shifts the nameplate anchor along its per-frame velocity · negative values trail the motion, positive lead it. If the cutouts run ahead of the title while the camera moves, reduce this; if they trail, increase it. 0 disables. Development aid.");
    }

    public static void DrawSupporterNameplateTuningOption(SpheneConfigService configService, string blockId = "SupporterNameplateTuning")
    {
        var offX = configService.Current.SupporterNameplateTuneOffX;
        if (SpheneUI.Controls.SliderFloat("Title line offset X##" + blockId + "OffX", ref offX, -30f, 30f, "%.1f px"))
        {
            configService.Current.SupporterNameplateTuneOffX = offX;
            configService.Save();
        }
        var offY = configService.Current.SupporterNameplateTuneOffY;
        if (SpheneUI.Controls.SliderFloat("Title line offset Y##" + blockId + "OffY", ref offY, -30f, 30f, "%.1f px"))
        {
            configService.Current.SupporterNameplateTuneOffY = offY;
            configService.Save();
        }
        var nameLineOffX = configService.Current.SupporterNameplateTuneNameLineOffX;
        if (SpheneUI.Controls.SliderFloat("Name line offset X##" + blockId + "NameLineOffX", ref nameLineOffX, -30f, 30f, "%.1f px"))
        {
            configService.Current.SupporterNameplateTuneNameLineOffX = nameLineOffX;
            configService.Save();
        }
        var nameLineOffY = configService.Current.SupporterNameplateTuneNameLineOffY;
        if (SpheneUI.Controls.SliderFloat("Name line offset Y##" + blockId + "NameLineOffY", ref nameLineOffY, -30f, 30f, "%.1f px"))
        {
            configService.Current.SupporterNameplateTuneNameLineOffY = nameLineOffY;
            configService.Save();
        }
        var nameOffX = configService.Current.SupporterNameplateTuneNameOffX;
        if (SpheneUI.Controls.SliderFloat("Name-only offset X##" + blockId + "NameOffX", ref nameOffX, -30f, 30f, "%.1f px"))
        {
            configService.Current.SupporterNameplateTuneNameOffX = nameOffX;
            configService.Save();
        }
        var nameOffY = configService.Current.SupporterNameplateTuneNameOffY;
        if (SpheneUI.Controls.SliderFloat("Name-only offset Y##" + blockId + "NameOffY", ref nameOffY, -30f, 30f, "%.1f px"))
        {
            configService.Current.SupporterNameplateTuneNameOffY = nameOffY;
            configService.Save();
        }
        var pitchMul = configService.Current.SupporterNameplateTunePitchMul;
        if (SpheneUI.Controls.SliderFloat("Line pitch multiplier##" + blockId + "Pitch", ref pitchMul, 0.5f, 1.5f, "%.2f x"))
        {
            configService.Current.SupporterNameplateTunePitchMul = pitchMul;
            configService.Save();
        }
        var advMul = configService.Current.SupporterNameplateTuneAdvanceMul;
        if (SpheneUI.Controls.SliderFloat("Horizontal advance multiplier##" + blockId + "Adv", ref advMul, 0.9f, 1.1f, "%.3f x"))
        {
            configService.Current.SupporterNameplateTuneAdvanceMul = advMul;
            configService.Save();
        }
        var padX = configService.Current.SupporterNameplateTuneGlyphPadX;
        if (SpheneUI.Controls.SliderFloat("Edge bleed X##" + blockId + "PadX", ref padX, 0f, 8f, "%.1f units"))
        {
            configService.Current.SupporterNameplateTuneGlyphPadX = padX;
            configService.Save();
        }
        var padY = configService.Current.SupporterNameplateTuneGlyphPadY;
        if (SpheneUI.Controls.SliderFloat("Edge bleed Y##" + blockId + "PadY", ref padY, 0f, 8f, "%.1f units"))
        {
            configService.Current.SupporterNameplateTuneGlyphPadY = padY;
            configService.Save();
        }
        var edgeMask = configService.Current.SupporterNameplateEdgeMask;
        if (SpheneUI.Controls.Toggle("Outline mask##" + blockId + "EdgeMask", ref edgeMask))
        {
            configService.Current.SupporterNameplateEdgeMask = edgeMask;
            configService.Save();
        }
        var presets = configService.Current.SupporterNameplateDistancePresets ??= [];
        if (SpheneUI.Controls.SmallButton("Add distance preset##" + blockId + "Add"))
        {
            presets.Add(new SupporterNameplateDistancePreset { Distance = 20f });
            configService.Save();
        }
        for (var i = 0; i < presets.Count; i++)
        {
            var p = presets[i];
            var remove = false;
            SpheneUI.Layout.Collapsible($"Preset {i + 1}##{blockId}Ph{i}", () =>
            {
                var dist = p.Distance;
                if (SpheneUI.Controls.SliderFloat($"Distance##{blockId}Dp{i}", ref dist, 0.5f, 60f, "%.1f y"))
                {
                    p.Distance = dist;
                    configService.Save();
                }
                var titleOff = new Vector2(p.TitleOffsetX, p.TitleOffsetY);
                if (SpheneUI.Controls.SliderFloat2($"Title offset##{blockId}To{i}", ref titleOff, -30f, 30f, "%.1f px"))
                {
                    p.TitleOffsetX = titleOff.X;
                    p.TitleOffsetY = titleOff.Y;
                    configService.Save();
                }
                var nameOff = new Vector2(p.NameOffsetX, p.NameOffsetY);
                if (SpheneUI.Controls.SliderFloat2($"Name offset##{blockId}No{i}", ref nameOff, -30f, 30f, "%.1f px"))
                {
                    p.NameOffsetX = nameOff.X;
                    p.NameOffsetY = nameOff.Y;
                    configService.Save();
                }
                var nameOnlyOff = new Vector2(p.NameOnlyOffsetX, p.NameOnlyOffsetY);
                if (SpheneUI.Controls.SliderFloat2($"Name-only offset##{blockId}Sno{i}", ref nameOnlyOff, -30f, 30f, "%.1f px"))
                {
                    p.NameOnlyOffsetX = nameOnlyOff.X;
                    p.NameOnlyOffsetY = nameOnlyOff.Y;
                    configService.Save();
                }
                var pitch = p.PitchMul;
                if (SpheneUI.Controls.SliderFloat($"Pitch multiplier##{blockId}Pp{i}", ref pitch, 0.5f, 1.5f, "%.2f x"))
                {
                    p.PitchMul = pitch;
                    configService.Save();
                }
                var adv = p.AdvanceMul;
                if (SpheneUI.Controls.SliderFloat($"Advance multiplier##{blockId}Ap{i}", ref adv, 0.9f, 1.1f, "%.3f x"))
                {
                    p.AdvanceMul = adv;
                    configService.Save();
                }
                var edge = new Vector2(p.EdgeX, p.EdgeY);
                if (SpheneUI.Controls.SliderFloat2($"Edge bleed##{blockId}Ep{i}", ref edge, 0f, 8f, "%.1f units"))
                {
                    p.EdgeX = edge.X;
                    p.EdgeY = edge.Y;
                    configService.Save();
                }
                var iconMask = p.IconMaskScale;
                if (SpheneUI.Controls.SliderFloat($"Icon mask scale##{blockId}Im{i}", ref iconMask, 25f, 200f, "%.0f %%"))
                {
                    p.IconMaskScale = iconMask;
                    configService.Save();
                }
                var iconSize = p.IconSizePercent;
                if (SpheneUI.Controls.SliderFloat($"Icon size##{blockId}Is{i}", ref iconSize, 50f, 200f, "%.0f %%"))
                {
                    p.IconSizePercent = iconSize;
                    configService.Save();
                }
                var gfdScale = p.GfdMaskScale;
                if (SpheneUI.Controls.SliderFloat($"Text icon mask scale##{blockId}Gs{i}", ref gfdScale, 25f, 200f, "%.0f %%"))
                {
                    p.GfdMaskScale = gfdScale;
                    configService.Save();
                }
                var gfdOff = new Vector2(p.GfdMaskOffsetX, p.GfdMaskOffsetY);
                if (SpheneUI.Controls.SliderFloat2($"Text icon mask offset##{blockId}Go{i}", ref gfdOff, -32f, 32f, "%.0f px"))
                {
                    p.GfdMaskOffsetX = gfdOff.X;
                    p.GfdMaskOffsetY = gfdOff.Y;
                    configService.Save();
                }
                if (SpheneUI.Controls.SmallButton($"Remove preset##{blockId}Rm{i}"))
                    remove = true;
            }, hint: $"{p.Distance:0.0} y");
            if (remove)
            {
                presets.RemoveAt(i);
                configService.Save();
                break;
            }
        }
        SpheneUI.Controls.HelpText("Live tuning for the nameplate cutout layout: the title and name offsets shift each cutout line independently on plates with a title line, while name-only offsets apply to plates without a title line. The pitch multiplier widens/narrows the gap between name and title lines, the advance multiplier fixes first-letters-fit-but-tail-drifts, and edge bleed sets the cutout's outline radius in font units · 0 means auto (1 unit when the plate renders an outline). Outline mask off removes the whole dilated outline from cutouts, leaving only the glyph fill. Icon mask scale sizes the native status/job icon cutouts, icon size scales the drawn supporter icon, and the text icon mask scale/offsets tune the inline status and « » icons inside the name text. Distance presets replace every tuning value for a plate once at least one exists · each plate uses the nearest preset at-or-below its yalm distance and holds it until the next preset, matching the game's own stepwise plate scaling; below the first preset the lowest preset applies. Presets only affect other players' plates · your own nameplate always uses the global sliders. Development aid.");
    }

    public static void DrawSupporterDebugInfoWindowOption(SpheneConfigService configService, string blockId = "SupporterDebugInfoWindow")
    {
        var enabled = configService.Current.SupporterNameplateDebugInfoWindow;
        if (SpheneUI.Controls.Toggle("Live values window##" + blockId, ref enabled))
        {
            configService.Current.SupporterNameplateDebugInfoWindow = enabled;
            configService.Save();
        }
        SpheneUI.Controls.HelpText("Pins a live readout to the top-left corner: per-plate line spacing, node scale, text flags, computed pitch/block/anchors and line band positions · read while tuning.");
    }

    public static void DrawSupporterIconSizeOption(SpheneConfigService configService, string blockId = "SupporterIconSize")
    {
        var percent = configService.Current.SupporterIconSizePercent;
        if (SpheneUI.Controls.SliderInt("Supporter icon size##" + blockId, ref percent, 50, 200, "%d%%"))
        {
            configService.Current.SupporterIconSizePercent = percent;
            configService.Save();
        }
        SpheneUI.Controls.HelpText("Scales the Sphene icon drawn next to supporter nameplates on your screen.");
    }

    public static void DrawSupporterCustomizationInfo(SpheneConfigService configService, UiSharedService uiShared, string blockId = "SupporterCustomization")
    {
        SpheneUI.Text.Wrapped("Customize the supporter nameplate: preset or custom symbols, separate label texts for the nameplate and the Sphene icon, game palette colors for symbol and label, optional glow colors for both, and a free color picker for the icon label.");
        SpheneUI.Controls.HelpText("Open Settings > Supporter to configure all customization options.");
    }

    public static void DrawSupporterGlowColorsInfo(SpheneConfigService configService, UiSharedService uiShared, string blockId = "SupporterGlowColors")
    {
        SpheneUI.Text.Wrapped("Supporter nameplate symbol and label can now have an edge glow color · a colored outline that sits behind the glyph like a background halo.");
        SpheneUI.Controls.HelpText("Pick glow colors in Settings > Supporter, or choose None to keep the default.");
    }

    public static void DrawSupporterStyleSyncInfo(SpheneConfigService configService, UiSharedService uiShared, string blockId = "SupporterStyleSync")
    {
        SpheneUI.Text.Wrapped("Your supporter nameplate style is now shared with other Sphene users: they see your nameplate exactly the way you configured it.");
        SpheneUI.Controls.HelpText("Configure your personal supporter look in Settings > Supporter.");
    }

    public static void DrawSupporterServerIconsInfo(SpheneConfigService configService, UiSharedService uiShared, string blockId = "SupporterServerIcons")
    {
        SpheneUI.Text.Wrapped("Servers can now provide custom supporter icons, including animated ones. Pick your favorite in the supporter settings and other users will see it on your nameplate.");
        SpheneUI.Controls.HelpText("Open Settings > Supporter to choose an icon provided by your server.");
    }
}
