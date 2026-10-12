using Dalamud.Interface;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// One supporter benefit: icon + accent color + title + description shown in
/// the supporter perk lists (settings page, hub promo card). New perks are
/// registered in <see cref="SupporterPerks.All"/> — no UI changes needed.
/// </summary>
internal readonly record struct SupporterPerk(
    FontAwesomeIcon Icon, Vector4 Color, string Title, string Description);
