using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

/// <summary>
/// Embedded plugin brand icons for the Quick Access grid - the real logos
/// shipped in each plugin's repo manifest (IconUrl), embedded under
/// <c>images/plugins/</c>. Missing plugins fall back to a glyph.
/// </summary>
internal static class SphenePluginIcons
{
    private static readonly (string InternalName, string File, FontAwesomeIcon Fallback)[] Map =
    {
        ("Penumbra",      "penumbra",    FontAwesomeIcon.Wrench),
        ("Glamourer",     "glamourer",   FontAwesomeIcon.HatWizard),
        ("CustomizePlus", "customize",   FontAwesomeIcon.SlidersH),
        ("SimpleHeels",   "",            FontAwesomeIcon.ShoePrints),
        ("Moodles",       "moodles",     FontAwesomeIcon.Icons),
        ("Livepose",      "",            FontAwesomeIcon.Walking),
        ("Honorific",     "honorific",   FontAwesomeIcon.Crown),
        ("Brio",          "brio",        FontAwesomeIcon.Camera),
        ("BypassEmote",   "bypassemote", FontAwesomeIcon.Smile),
        ("PetRenamer",    "petrenamer",  FontAwesomeIcon.Paw),
        ("ShrinkU.Builtin","shrinku",   FontAwesomeIcon.FileArchive),
    };

    /// <summary>Embedded logo texture for the plugin, or null when it has none.</summary>
    public static IDalamudTextureWrap? Texture(string internalName)
    {
        var key = internalName.Replace(" ", string.Empty).Replace("+", "plus");
        foreach (var (name, file, _) in Map)
        {
            if (!string.Equals(name, internalName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (file.Length == 0)
            {
                return null;
            }
            return SpheneUI.Media.Texture($"pluginicon:{file}", () => Load(file));
        }
        return null;
    }

    /// <summary>FA fallback glyph when the plugin ships no icon.</summary>
    public static FontAwesomeIcon FallbackGlyph(string internalName)
    {
        var key = internalName.Replace(" ", string.Empty).Replace("+", "plus");
        foreach (var (name, _, glyph) in Map)
        {
            if (string.Equals(name, internalName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                return glyph;
            }
        }
        return FontAwesomeIcon.PuzzlePiece;
    }

    private static byte[]? Load(string file)
    {
        var asm = typeof(SphenePluginIcons).Assembly;
        var res = asm.GetManifestResourceNames().FirstOrDefault(
            n => n.EndsWith($"plugins.{file}.png", StringComparison.OrdinalIgnoreCase)
                 || n.EndsWith($"plugins/{file}.png", StringComparison.OrdinalIgnoreCase));
        if (res == null)
        {
            return null;
        }
        using var stream = asm.GetManifestResourceStream(res);
        if (stream == null)
        {
            return null;
        }
        using var mem = new MemoryStream();
        stream.CopyTo(mem);
        return mem.ToArray();
    }
}
