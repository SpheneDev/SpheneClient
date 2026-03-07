using Sphene.API.Data;

using Sphene.API.Data.Enum;

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

    public API.Data.CharacterData ToAPI()
    {
        Dictionary<ObjectKind, List<FileReplacementData>> fileReplacements =
            FileReplacements.ToDictionary(k => k.Key, k => k.Value.Where(f => f.HasFileReplacement && !f.IsFileSwap)
            .GroupBy(f => f.Hash, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
        {
            return new FileReplacementData()
            {
                GamePaths = g.SelectMany(f => f.GamePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Hash = g.First().Hash,
            };
        }).ToList());

        foreach (var item in FileReplacements)
        {
            var fileSwapsToAdd = item.Value.Where(f => f.IsFileSwap).Select(f => f.ToFileReplacementDto());
            fileReplacements[item.Key].AddRange(fileSwapsToAdd);
            fileReplacements[item.Key] = DeduplicateGamePaths(fileReplacements[item.Key]);
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

    private static List<FileReplacementData> DeduplicateGamePaths(List<FileReplacementData> entries)
    {
        if (entries.Count == 0) return entries;

        var result = new List<FileReplacementData>(entries.Count);
        var claimedGamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var swapGamePaths = entries
            .Where(e => !string.IsNullOrEmpty(e.FileSwapPath))
            .SelectMany(e => e.GamePaths ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var sourcePaths = entry.GamePaths ?? [];
            var isSwap = !string.IsNullOrEmpty(entry.FileSwapPath);
            var uniquePaths = sourcePaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p => isSwap || !swapGamePaths.Contains(p))
                .Where(claimedGamePaths.Add)
                .ToArray();

            if (uniquePaths.Length == 0) continue;

            result.Add(new FileReplacementData
            {
                FileSwapPath = entry.FileSwapPath,
                Hash = entry.Hash,
                GamePaths = uniquePaths
            });
        }

        return result;
    }
}
