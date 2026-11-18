using System.Collections.Generic;
using Sphene.SpheneConfiguration.Models;

namespace Sphene.Services.Gifs;

/// <summary>
/// Pin/unpin logic for the picker's favorite GIFs. Operates on the
/// persisted config list directly: pinned entries move to the front
/// (newest first) and the list is capped so config stays small. Pure
/// list operations - the caller saves.
/// </summary>
public static class GifFavorites
{
    public const int MaxFavorites = 48;

    /// <summary>Reconstructs a selectable item from a stored favorite.</summary>
    public static GifItem ToItem(GifFavorite fav)
        => new(fav.Id, fav.Title, fav.PreviewUrl, fav.MediaUrl,
            fav.Width, fav.Height) { IsSticker = fav.IsSticker };

    public static bool IsPinned(List<GifFavorite> favorites,
        string provider, string id)
    {
        for (var i = 0; i < favorites.Count; i++)
        {
            var f = favorites[i];
            if (string.Equals(f.Provider, provider, System.StringComparison.Ordinal)
                && string.Equals(f.Id, id, System.StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Pins <paramref name="item"/> (front, capped) or unpins it
    /// when already present. Returns true when the item is now pinned.</summary>
    public static bool Toggle(List<GifFavorite> favorites,
        string provider, GifItem item)
    {
        for (var i = 0; i < favorites.Count; i++)
        {
            var f = favorites[i];
            if (string.Equals(f.Provider, provider, System.StringComparison.Ordinal)
                && string.Equals(f.Id, item.Id, System.StringComparison.Ordinal))
            {
                favorites.RemoveAt(i);
                return false;
            }
        }
        favorites.Insert(0, new GifFavorite
        {
            Provider = provider,
            Id = item.Id,
            Title = item.Title,
            PreviewUrl = item.PreviewUrl,
            MediaUrl = item.MediaUrl,
            Width = item.Width,
            Height = item.Height,
            IsSticker = item.IsSticker,
        });
        while (favorites.Count > MaxFavorites)
        {
            favorites.RemoveAt(favorites.Count - 1);
        }
        return true;
    }
}
