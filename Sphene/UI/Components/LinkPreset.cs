using System.Numerics;
using Dalamud.Interface;

namespace Sphene.UI.Components;

/// <summary>One link-chip brand preset. <see cref="Brand"/> resolves to an
/// embedded brand texture; <see cref="Glyph"/> is drawn as plain text for
/// marks FontAwesome does not carry (e.g. "X").</summary>
internal sealed record LinkPreset(
    string Key,
    string Label,
    FontAwesomeIcon? Icon,
    Vector4? Color,
    string UrlHint,
    SpheneBrandIcons.Brand? Brand = null,
    string? Glyph = null);
