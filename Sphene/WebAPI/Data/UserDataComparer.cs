using System;
using System.Collections.Generic;
using Sphene.API.Data;
using Sphene.WebAPI.Data;

namespace Sphene.WebAPI.Data;

public class UserDataComparer : IEqualityComparer<UserData>
{
    private static readonly UserDataComparer _instance = new();

    private UserDataComparer()
    { }

    public static UserDataComparer Instance => _instance;

    public bool Equals(UserData? x, UserData? y)
    {
        if (x == null || y == null) return false;
        return string.Equals(x.UID, y.UID, StringComparison.Ordinal);
    }

    public int GetHashCode(UserData obj)
    {
        return obj.UID.GetHashCode(StringComparison.Ordinal);
    }
}