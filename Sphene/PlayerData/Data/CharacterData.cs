using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.Utils;
using System.Globalization;

namespace Sphene.PlayerData.Data;

public class CharacterData
{
    public Dictionary<ObjectKind, string> CustomizePlusScale { get; set; } = [];
    public Dictionary<ObjectKind, HashSet<FileReplacement>> FileReplacements { get; set; } = [];
    public Dictionary<ObjectKind, string> GlamourerString { get; set; } = [];
    public string HeelsData { get; set; } = string.Empty;
    public string HonorificData { get; set; } = string.Empty;
    public string ManipulationString { get; set; } = string.Empty;
    public string MoodlesData { get; set; } = string.Empty;
    public string PetNamesData { get; set; } = string.Empty;
    public string BypassEmoteData { get; set; } = string.Empty;

    public void SetFragment(ObjectKind kind, CharacterDataFragment? fragment)
    {
        if (kind == ObjectKind.Player)
        {
            var playerFragment = (fragment as CharacterDataFragmentPlayer);
            HeelsData = playerFragment?.HeelsData ?? string.Empty;
            HonorificData = playerFragment?.HonorificData ?? string.Empty;
            ManipulationString = playerFragment?.ManipulationString ?? string.Empty;
            MoodlesData = playerFragment?.MoodlesData ?? string.Empty;
            PetNamesData = playerFragment?.PetNamesData ?? string.Empty;
            BypassEmoteData = playerFragment?.BypassEmoteData ?? string.Empty;
        }

        if (fragment is null)
        {
            CustomizePlusScale.Remove(kind);
            FileReplacements.Remove(kind);
            GlamourerString.Remove(kind);
        }
        else
        {
            CustomizePlusScale[kind] = fragment.CustomizePlusScale;
            FileReplacements[kind] = fragment.FileReplacements;
            GlamourerString[kind] = fragment.GlamourerString;
        }
    }

    public API.Data.CharacterData ToAPI(bool stripModInfo = false, bool anonymizeModNames = false)
    {
        var useAnonymizedNames = anonymizeModNames && !stripModInfo;
        Dictionary<ObjectKind, List<FileReplacementData>> fileReplacements =
            FileReplacements.ToDictionary(k => k.Key, k => k.Value.Where(f => f.HasFileReplacement && !f.IsFileSwap)
            .GroupBy(f => (f.Hash, GetExportedModName(f.ModName, useAnonymizedNames), GetExportedOptionName(f.ModName, f.OptionName, useAnonymizedNames)))
            .Select(g =>
        {
            return new FileReplacementData()
            {
                GamePaths = g.SelectMany(f => f.GamePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Hash = g.Key.Hash,
                ModName = g.Key.Item2,
                OptionName = g.Key.Item3,
                IsActive = !stripModInfo && g.Any(f => f.IsActive)
            };
        }).ToList());

        foreach (var item in FileReplacements)
        {
            var fileSwapsToAdd = item.Value.Where(f => f.IsFileSwap).Select(f => new FileReplacementData
            {
                GamePaths = [.. f.GamePaths],
                Hash = f.Hash,
                FileSwapPath = f.ResolvedPath,
                ModName = GetExportedModName(f.ModName, useAnonymizedNames),
                OptionName = GetExportedOptionName(f.ModName, f.OptionName, useAnonymizedNames),
                IsActive = !stripModInfo && f.IsActive,
            });
            fileReplacements[item.Key].AddRange(fileSwapsToAdd);
        }

        return new API.Data.CharacterData()
        {
            FileReplacements = fileReplacements,
            GlamourerData = GlamourerString.ToDictionary(d => d.Key, d => d.Value),
            ManipulationData = ManipulationString,
            HeelsData = HeelsData,
            CustomizePlusData = CustomizePlusScale.ToDictionary(d => d.Key, d => d.Value),
            HonorificData = HonorificData,
            MoodlesData = MoodlesData,
            PetNamesData = PetNamesData,
            BypassEmoteData = BypassEmoteData
        };
    }

    private static string? GetExportedModName(string? modName, bool anonymize)
    {
        if (string.IsNullOrWhiteSpace(modName)) return null;
        if (!anonymize) return modName;
        return "M" + GetAnonymizedNumber(modName);
    }

    private static string? GetExportedOptionName(string? modName, string? optionName, bool anonymize)
    {
        if (string.IsNullOrWhiteSpace(optionName)) return null;
        if (!anonymize) return optionName;
        var source = string.IsNullOrWhiteSpace(modName) ? optionName : $"{modName}|{optionName}";
        return "O" + GetAnonymizedNumber(source);
    }

    private static string GetAnonymizedNumber(string value)
    {
        var hash = value.GetHash256();
        var shortHex = hash.Length >= 8 ? hash[..8] : hash;
        if (!uint.TryParse(shortHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number))
        {
            return "0";
        }
        return number.ToString(CultureInfo.InvariantCulture);
    }
}
