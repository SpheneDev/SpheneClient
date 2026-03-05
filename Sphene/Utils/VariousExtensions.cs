using Dalamud.Game.ClientState.Objects.Types;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.PlayerData.Handlers;
using Sphene.PlayerData.Pairs;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Text.Json;
using System.IO;

namespace Sphene.Utils;

public static class VariousExtensions
{
    public static string ToByteString(this int bytes, bool addSuffix = true)
    {
        string[] suffix = ["B", "KiB", "MiB", "GiB", "TiB"];
        int i;
        double dblSByte = bytes;
        for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            dblSByte = bytes / 1024.0;
        }

        return addSuffix ? $"{dblSByte:0.00} {suffix[i]}" : $"{dblSByte:0.00}";
    }

    public static string ToByteString(this long bytes, bool addSuffix = true)
    {
        string[] suffix = ["B", "KiB", "MiB", "GiB", "TiB"];
        int i;
        double dblSByte = bytes;
        for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            dblSByte = bytes / 1024.0;
        }

        return addSuffix ? $"{dblSByte:0.00} {suffix[i]}" : $"{dblSByte:0.00}";
    }

    public static void CancelDispose(this CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // swallow it
        }
    }

    public static CancellationTokenSource CancelRecreate(this CancellationTokenSource? cts)
    {
        cts?.CancelDispose();
        return new CancellationTokenSource();
    }

    public static Dictionary<ObjectKind, HashSet<PlayerChanges>> CheckUpdatedData(this CharacterData newData, Guid applicationBase,
        CharacterData? oldData, ILogger logger, PairHandler cachedPlayer, bool forceApplyCustomization, bool forceApplyMods, bool suppressRedrawOnEquipmentOrWeaponChanges)
    {
        oldData ??= new();
        var charaDataToUpdate = new Dictionary<ObjectKind, HashSet<PlayerChanges>>();
        var shouldForcePlayerRedraw = newData.RequiresEyeOrEmoteRedraw(oldData);
        foreach (ObjectKind objectKind in Enum.GetValues<ObjectKind>())
        {
            charaDataToUpdate[objectKind] = [];
            oldData.FileReplacements.TryGetValue(objectKind, out var existingFileReplacements);
            newData.FileReplacements.TryGetValue(objectKind, out var newFileReplacements);
            oldData.GlamourerData.TryGetValue(objectKind, out var existingGlamourerData);
            newData.GlamourerData.TryGetValue(objectKind, out var newGlamourerData);
            var suppressEquipmentRedraw = suppressRedrawOnEquipmentOrWeaponChanges
                && objectKind == ObjectKind.Player
                && IsEquipmentOrWeaponOnlyChange(existingFileReplacements, newFileReplacements);

            bool hasNewButNotOldFileReplacements = newFileReplacements != null && existingFileReplacements == null;
            bool hasOldButNotNewFileReplacements = existingFileReplacements != null && newFileReplacements == null;

            bool hasNewButNotOldGlamourerData = newGlamourerData != null && existingGlamourerData == null;
            bool hasOldButNotNewGlamourerData = existingGlamourerData != null && newGlamourerData == null;

            bool hasNewAndOldFileReplacements = newFileReplacements != null && existingFileReplacements != null;
            bool hasNewAndOldGlamourerData = newGlamourerData != null && existingGlamourerData != null;

            if (hasNewButNotOldFileReplacements || hasOldButNotNewFileReplacements || hasNewButNotOldGlamourerData || hasOldButNotNewGlamourerData)
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Some new data arrived: NewButNotOldFiles:{hasNewButNotOldFileReplacements}," +
                    " OldButNotNewFiles:{hasOldButNotNewFileReplacements}, NewButNotOldGlam:{hasNewButNotOldGlamourerData}, OldButNotNewGlam:{hasOldButNotNewGlamourerData}) => {change}, {change2}",
                    applicationBase,
                    cachedPlayer, objectKind, hasNewButNotOldFileReplacements, hasOldButNotNewFileReplacements, hasNewButNotOldGlamourerData, hasOldButNotNewGlamourerData, PlayerChanges.ModFiles, PlayerChanges.Glamourer);
                if (hasNewButNotOldFileReplacements || hasOldButNotNewFileReplacements)
                {
                    charaDataToUpdate[objectKind].Add(PlayerChanges.ModFiles);
                    if (objectKind != ObjectKind.Player || shouldForcePlayerRedraw)
                    {
                        charaDataToUpdate[objectKind].Add(PlayerChanges.ForcedRedraw);
                    }
                }
                if (hasNewButNotOldGlamourerData || hasOldButNotNewGlamourerData)
                {
                    charaDataToUpdate[objectKind].Add(PlayerChanges.Glamourer);
                }
            }
            else
            {
                if (hasNewAndOldFileReplacements)
                {
                    bool listsAreEqual = oldData.FileReplacements[objectKind].SequenceEqual(newData.FileReplacements[objectKind], PlayerData.Data.FileReplacementDataComparer.Instance);
                    if (!listsAreEqual || forceApplyMods)
                    {
                        logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (FileReplacements not equal) => {change}", applicationBase, cachedPlayer, objectKind, PlayerChanges.ModFiles);
                        charaDataToUpdate[objectKind].Add(PlayerChanges.ModFiles);
                        if (forceApplyMods || objectKind != ObjectKind.Player)
                        {
                            if (objectKind != ObjectKind.Player || !suppressEquipmentRedraw)
                            {
                                charaDataToUpdate[objectKind].Add(PlayerChanges.ForcedRedraw);
                            }
                        }
                        else
                        {
                            if (shouldForcePlayerRedraw)
                            {
                                logger.LogDebug("[BASE-{appbase}] Redraw needed for eye textures or new emote data => {change}", applicationBase, PlayerChanges.ForcedRedraw);
                                charaDataToUpdate[objectKind].Add(PlayerChanges.ForcedRedraw);
                            }
                        }
                    }
                }

                if (hasNewAndOldGlamourerData)
                {
                    bool glamourerDataDifferent = !string.Equals(oldData.GlamourerData[objectKind], newData.GlamourerData[objectKind], StringComparison.Ordinal);
                    if (glamourerDataDifferent || forceApplyCustomization)
                    {
                        logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Glamourer different) => {change}", applicationBase, cachedPlayer, objectKind, PlayerChanges.Glamourer);
                        charaDataToUpdate[objectKind].Add(PlayerChanges.Glamourer);
                    }
                }
            }

            oldData.CustomizePlusData.TryGetValue(objectKind, out var oldCustomizePlusData);
            newData.CustomizePlusData.TryGetValue(objectKind, out var newCustomizePlusData);

            oldCustomizePlusData ??= string.Empty;
            newCustomizePlusData ??= string.Empty;

            bool customizeDataDifferent = !string.Equals(oldCustomizePlusData, newCustomizePlusData, StringComparison.Ordinal);
            if (customizeDataDifferent || (forceApplyCustomization && !string.IsNullOrEmpty(newCustomizePlusData)))
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff customize data) => {change}", applicationBase, cachedPlayer, objectKind, PlayerChanges.Customize);
                charaDataToUpdate[objectKind].Add(PlayerChanges.Customize);
            }

            if (objectKind != ObjectKind.Player) continue;

            bool manipDataDifferent = !string.Equals(oldData.ManipulationData, newData.ManipulationData, StringComparison.Ordinal);
            if (manipDataDifferent || forceApplyMods)
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff manip data) => {change}", applicationBase, cachedPlayer, objectKind, PlayerChanges.ModManip);
                charaDataToUpdate[objectKind].Add(PlayerChanges.ModManip);
            }

            bool heelsOffsetDifferent = !string.Equals(oldData.HeelsData, newData.HeelsData, StringComparison.Ordinal);
            if (heelsOffsetDifferent || (forceApplyCustomization && !string.IsNullOrEmpty(newData.HeelsData)))
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff heels data) => {change}", applicationBase, cachedPlayer, objectKind, PlayerChanges.Heels);
                charaDataToUpdate[objectKind].Add(PlayerChanges.Heels);
            }

            bool honorificDataDifferent = !string.Equals(oldData.HonorificData, newData.HonorificData, StringComparison.Ordinal);
            if (honorificDataDifferent || (forceApplyCustomization && !string.IsNullOrEmpty(newData.HonorificData)))
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff honorific data) => {change}", applicationBase, cachedPlayer, objectKind, PlayerChanges.Honorific);
                charaDataToUpdate[objectKind].Add(PlayerChanges.Honorific);
            }

            bool moodlesDataDifferent = !string.Equals(oldData.MoodlesData, newData.MoodlesData, StringComparison.Ordinal);
            if (moodlesDataDifferent || (forceApplyCustomization && !string.IsNullOrEmpty(newData.MoodlesData)))
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff moodles data) => {change}", applicationBase, cachedPlayer, objectKind, PlayerChanges.Moodles);
                charaDataToUpdate[objectKind].Add(PlayerChanges.Moodles);
            }

            bool petNamesDataDifferent = !string.Equals(oldData.PetNamesData, newData.PetNamesData, StringComparison.Ordinal);
            if (petNamesDataDifferent || (forceApplyCustomization && !string.IsNullOrEmpty(newData.PetNamesData)))
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff petnames data) => {change}", applicationBase, cachedPlayer, objectKind, PlayerChanges.PetNames);
                charaDataToUpdate[objectKind].Add(PlayerChanges.PetNames);
            }

            bool bypassEmoteDataDifferent = !string.Equals(oldData.BypassEmoteData, newData.BypassEmoteData, StringComparison.Ordinal);
            if (bypassEmoteDataDifferent || (forceApplyCustomization && !string.IsNullOrEmpty(newData.BypassEmoteData)))
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff BypassEmote data) => {change}", applicationBase, cachedPlayer, objectKind, PlayerChanges.BypassEmote);
                charaDataToUpdate[objectKind].Add(PlayerChanges.BypassEmote);
            }
        }

        foreach (KeyValuePair<ObjectKind, HashSet<PlayerChanges>> data in charaDataToUpdate.ToList())
        {
            if (!data.Value.Any()) charaDataToUpdate.Remove(data.Key);
            else charaDataToUpdate[data.Key] = [.. data.Value.OrderByDescending(p => (int)p)];
        }

        return charaDataToUpdate;
    }

    public static bool RequiresEyeOrEmoteRedraw(this CharacterData newData, CharacterData? oldData)
    {
        oldData ??= new CharacterData();
        oldData.FileReplacements.TryGetValue(ObjectKind.Player, out var existingFileReplacements);
        newData.FileReplacements.TryGetValue(ObjectKind.Player, out var newFileReplacements);
        var eyesChanged = HasEyeTextureChanges(existingFileReplacements, newFileReplacements);
        var hasNewTransient = HasNewTransientEmote(existingFileReplacements, newFileReplacements);
        return eyesChanged || hasNewTransient;
    }

    public static bool HasEyeTextureChanges(this CharacterData newData, CharacterData? oldData)
    {
        oldData ??= new CharacterData();
        oldData.FileReplacements.TryGetValue(ObjectKind.Player, out var existingFileReplacements);
        newData.FileReplacements.TryGetValue(ObjectKind.Player, out var newFileReplacements);
        return HasEyeTextureChanges(existingFileReplacements, newFileReplacements);
    }

    public static bool HasSpecialEmotePap(this CharacterData data)
    {
        if (!data.FileReplacements.TryGetValue(ObjectKind.Player, out var list) || list.Count == 0) return false;
        foreach (var entry in list)
        {
            foreach (var path in entry.GamePaths)
            {
                var file = Path.GetFileName(path);
                if (_specialEmotePaps.Contains(file)) return true;
            }
        }
        return false;
    }

    public static bool HasSpecialEmoteChanges(this CharacterData newData, CharacterData? oldData)
    {
        oldData ??= new CharacterData();
        oldData.FileReplacements.TryGetValue(ObjectKind.Player, out var existingFileReplacements);
        newData.FileReplacements.TryGetValue(ObjectKind.Player, out var newFileReplacements);
        var existingSpecial = FilterSpecialEmoteReplacements(existingFileReplacements);
        var newSpecial = FilterSpecialEmoteReplacements(newFileReplacements);
        return !existingSpecial.SequenceEqual(newSpecial, PlayerData.Data.FileReplacementDataComparer.Instance);
    }

    public static bool IsEquipmentOrWeaponOnlyChange(this CharacterData newData, CharacterData? oldData)
    {
        oldData ??= new CharacterData();
        oldData.FileReplacements.TryGetValue(ObjectKind.Player, out var existingFileReplacements);
        newData.FileReplacements.TryGetValue(ObjectKind.Player, out var newFileReplacements);
        return IsEquipmentOrWeaponOnlyChange(existingFileReplacements, newFileReplacements);
    }

    private static bool HasEyeTextureChanges(IEnumerable<FileReplacementData>? existingFileReplacements, IEnumerable<FileReplacementData>? newFileReplacements)
    {
        var existingEyes = FilterEyeReplacements(existingFileReplacements);
        var newEyes = FilterEyeReplacements(newFileReplacements);
        return !existingEyes.SequenceEqual(newEyes, PlayerData.Data.FileReplacementDataComparer.Instance);
    }

    private static bool HasNewTransientEmote(IEnumerable<FileReplacementData>? existingFileReplacements, IEnumerable<FileReplacementData>? newFileReplacements)
    {
        var existingTransients = FilterTransientReplacements(existingFileReplacements);
        var newTransients = FilterTransientReplacements(newFileReplacements);
        if (newTransients.Count == 0) return false;
        return newTransients.Any(n => !existingTransients.Contains(n, PlayerData.Data.FileReplacementDataComparer.Instance));
    }

    private static List<FileReplacementData> FilterEyeReplacements(IEnumerable<FileReplacementData>? fileReplacements)
    {
        if (fileReplacements == null) return [];
        return fileReplacements
            .Where(g => g.GamePaths.Any(IsEyePath))
            .OrderBy(g => string.IsNullOrEmpty(g.Hash) ? g.FileSwapPath : g.Hash, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<FileReplacementData> FilterTransientReplacements(IEnumerable<FileReplacementData>? fileReplacements)
    {
        if (fileReplacements == null) return [];
        return fileReplacements
            .Where(g => g.GamePaths.Any(IsTransientPath))
            .OrderBy(g => string.IsNullOrEmpty(g.Hash) ? g.FileSwapPath : g.Hash, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<FileReplacementData> FilterSpecialEmoteReplacements(IEnumerable<FileReplacementData>? fileReplacements)
    {
        if (fileReplacements == null) return [];
        return fileReplacements
            .Where(g => g.GamePaths.Any(IsSpecialEmotePath))
            .OrderBy(g => string.IsNullOrEmpty(g.Hash) ? g.FileSwapPath : g.Hash, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsEyePath(string path)
    {
        return path.Contains("/eye/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/face/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransientPath(string path)
    {
        return !path.EndsWith("mdl", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith("tex", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith("mtrl", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSpecialEmotePath(string path)
    {
        var file = Path.GetFileName(path);
        return _specialEmotePaps.Contains(file);
    }

    private static readonly HashSet<string> _specialEmotePaps = new(StringComparer.OrdinalIgnoreCase)
    {
        "s_pose01_loop.pap",
        "s_pose02_loop.pap",
        "s_pose03_loop.pap",
        "s_pose04_loop.pap",
        "s_pose05_loop.pap",
        "j_pose01_loop.pap",
        "j_pose02_loop.pap",
        "j_pose03_loop.pap",
        "j_pose04_loop.pap",
        "l_pose01_loop.pap",
        "l_pose02_loop.pap",
        "l_pose03_loop.pap"
    };

    private static bool IsEquipmentOrWeaponOnlyChange(IEnumerable<FileReplacementData>? existingFileReplacements, IEnumerable<FileReplacementData>? newFileReplacements)
    {
        var existingList = existingFileReplacements?.ToList() ?? [];
        var newList = newFileReplacements?.ToList() ?? [];
        if (existingList.Count == 0 && newList.Count == 0) return false;

        var comparer = PlayerData.Data.FileReplacementDataComparer.Instance;
        var removed = existingList.Where(item => !newList.Contains(item, comparer)).ToList();
        var added = newList.Where(item => !existingList.Contains(item, comparer)).ToList();
        if (removed.Count == 0 && added.Count == 0) return false;

        return removed.All(IsEquipmentOrWeaponReplacement) && added.All(IsEquipmentOrWeaponReplacement);
    }

    private static bool IsEquipmentOrWeaponReplacement(FileReplacementData data)
    {
        if (data.GamePaths == null || data.GamePaths.Length == 0) return false;
        return data.GamePaths.All(IsEquipmentOrWeaponPath);
    }

    private static bool IsEquipmentOrWeaponPath(string path)
    {
        return path.Contains("/equipment/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/weapon/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/accessory/", StringComparison.OrdinalIgnoreCase);
    }

    public static T DeepClone<T>(this T obj)
    {
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(obj))!;
    }

    public static unsafe int? ObjectTableIndex(this IGameObject? gameObject)
    {
        if (gameObject == null || gameObject.Address == IntPtr.Zero)
        {
            return null;
        }

        return ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)gameObject.Address)->ObjectIndex;
    }
}
