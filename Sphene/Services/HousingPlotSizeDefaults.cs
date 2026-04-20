using System;
using System.Collections.Generic;

namespace Sphene.Services;

public static class HousingPlotSizeDefaults
{
    public static readonly IReadOnlyDictionary<uint, byte[]> ByTerritoryId = new Dictionary<uint, byte[]>
    {
        {
            641u,
            [
                1, 0, 0, 0, 0, 0, 2, 1, 0, 0,
                0, 0, 1, 0, 1, 2, 0, 0, 1, 0,
                0, 0, 0, 1, 0, 0, 0, 1, 0, 2
            ]
        },
        {
            340u,
            [
                1, 0, 2, 0, 1, 2, 0, 0, 0, 0,
                1, 0, 0, 0, 0, 1, 0, 0, 0, 0,
                1, 0, 0, 0, 0, 0, 1, 2, 0, 1
            ]
        },
        {
            339u,
            [
                1, 2, 0, 1, 2, 1, 1, 0, 0, 0,
                0, 0, 0, 1, 2, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 1, 1
            ]
        },
        {
            341u,
            [
                0, 0, 0, 1, 2, 1, 0, 1, 0, 0,
                1, 1, 2, 0, 0, 0, 0, 0, 1, 0,
                0, 0, 0, 0, 1, 0, 0, 0, 0, 2
            ]
        },
        {
            979u,
            [
                0, 1, 0, 0, 0, 0, 1, 1, 0, 0,
                0, 2, 0, 0, 0, 0, 1, 1, 0, 0,
                1, 2, 0, 0, 0, 1, 0, 0, 0, 2
            ]
        }
    };

    public static bool TryGet(uint territoryId, out byte[] sizes)
    {
        if (ByTerritoryId.TryGetValue(territoryId, out var arr) && arr.Length == 30)
        {
            sizes = arr;
            return true;
        }

        sizes = Array.Empty<byte>();
        return false;
    }
}
