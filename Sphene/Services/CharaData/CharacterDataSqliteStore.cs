using Dalamud.Plugin;
using MessagePack;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.Services;
using Sphene.Services.Mediator;
using System.Text.Json;
using System.Linq;

using Sphene.Services.ModLearning.Models;

using Sphene.PlayerData.Data;
using Sphene.API.Data.Enum;

namespace Sphene.Services.CharaData;

/// <summary>
/// Provides SQLite persistence for character data and pair character data.
/// </summary>
public sealed class CharacterDataSqliteStore : DisposableMediatorSubscriberBase, IHostedService
{
    private static readonly MessagePackSerializerOptions SerializerOptions = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);
    private readonly string _databasePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly DalamudUtilService _dalamudUtilService;

    public CharacterDataSqliteStore(ILogger<CharacterDataSqliteStore> logger, SpheneMediator mediator, IDalamudPluginInterface pluginInterface, DalamudUtilService dalamudUtilService)
        : base(logger, mediator)
    {
        _databasePath = Path.Combine(pluginInterface.ConfigDirectory.FullName, "characterdata.db");
        _dalamudUtilService = dalamudUtilService;
        Mediator.Subscribe<CharacterDataCreatedMessage>(this, message => { _ = StoreLocalCharacterDataAsync(message.CharacterData); });
    }

    /// <summary>
    /// Gets the SQLite database file path.
    /// </summary>
    public string DatabasePath => _databasePath;

    /// <summary>
    /// Initializes the SQLite database and schema.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await InitializeDatabaseAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the store.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Resets the SQLite database by deleting all stored data.
    /// </summary>
    public async Task ResetDatabaseAsync(bool deleteFile = true)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (deleteFile)
            {
                try
                {
                    if (File.Exists(_databasePath))
                    {
                        File.Delete(_databasePath);
                    }
                    return;
                }
                catch (IOException ex)
                {
                    Logger.LogWarning(ex, "Database file locked, falling back to table reset");
                }
            }

            using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM local_character_data;" +
                "DELETE FROM pair_character_data;" +
                "DELETE FROM learned_mods;" +
                "DELETE FROM resource_links;" +
                "VACUUM;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to reset character data database");
        }
        finally
        {
            _writeLock.Release();
        }

        await InitializeDatabaseAsync(CancellationToken.None).ConfigureAwait(false);
    }

    internal sealed record CharacterDataDatabaseStats(
        string DatabasePath,
        long DatabaseSizeBytes,
        long LocalCharacterCount,
        long PairCharacterCount,
        long LearnedModsCount,
        long ResourceLinksCount,
        long CacheEntryCount,
        long LocalDataBytes,
        long PairDataBytes,
        DateTimeOffset? LastPairReceivedUtc,
        string? Error);

    internal async Task<CharacterDataDatabaseStats> GetDatabaseStatsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var fileSize = File.Exists(_databasePath) ? new FileInfo(_databasePath).Length : 0;
            using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            async Task<long> ScalarLongAsync(string sql)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (result == null || result is DBNull) return 0;
                return Convert.ToInt64(result);
            }

            var localCount = await ScalarLongAsync("SELECT COUNT(*) FROM local_character_data;").ConfigureAwait(false);
            var pairCount = await ScalarLongAsync("SELECT COUNT(*) FROM pair_character_data;").ConfigureAwait(false);
            var learnedModsCount = await ScalarLongAsync("SELECT COUNT(*) FROM learned_mods;").ConfigureAwait(false);
            var resourceLinksCount = await ScalarLongAsync("SELECT COUNT(*) FROM resource_links;").ConfigureAwait(false);
            var cacheEntryCount = await ScalarLongAsync("SELECT COUNT(*) FROM content_addressable_cache;").ConfigureAwait(false);
            var localBytes = await ScalarLongAsync("SELECT COALESCE(SUM(LENGTH(data_blob)), 0) FROM local_character_data;").ConfigureAwait(false);
            var pairBytes = await ScalarLongAsync("SELECT COALESCE(SUM(LENGTH(data_blob)), 0) FROM pair_character_data;").ConfigureAwait(false);
            var lastPairReceived = await ScalarLongAsync("SELECT COALESCE(MAX(received_utc), 0) FROM pair_character_data;").ConfigureAwait(false);
            DateTimeOffset? lastReceivedUtc = lastPairReceived > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(lastPairReceived)
                : null;

            return new CharacterDataDatabaseStats(
                _databasePath,
                fileSize,
                localCount,
                pairCount,
                learnedModsCount,
                resourceLinksCount,
                cacheEntryCount,
                localBytes,
                pairBytes,
                lastReceivedUtc,
                null);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to read character data database stats");
            return new CharacterDataDatabaseStats(
                _databasePath,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                ex.Message);
        }
    }

    internal async Task<(bool IsOk, string Message)> CheckDatabaseHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check(1);";
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var message = Convert.ToString(result) ?? "unknown";
            return (string.Equals(message, "ok", StringComparison.OrdinalIgnoreCase), message);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to run database health check");
            return (false, ex.Message);
        }
    }

    internal async Task VacuumDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to vacuum character data database.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Stores received pair character data in SQLite.
    /// </summary>
    public async Task StoreReceivedPairDataAsync(string userUid, API.Data.CharacterData characterData, string? playerName, int? worldId, byte? raceId, byte? tribeId, byte? gender, string? sessionId, long? sequenceNumber, DateTimeOffset? receivedAt = null)
    {
        if (string.IsNullOrWhiteSpace(userUid)) return;
        var dataHash = characterData.DataHash?.Value;
        if (string.IsNullOrWhiteSpace(dataHash)) return;

        var dataBlob = MessagePackSerializer.Serialize(characterData, SerializerOptions);
        var received = (receivedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync().ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT OR REPLACE INTO pair_character_data " +
                "(user_uid, data_hash, received_utc, player_name, world_id, race_id, tribe_id, gender, session_id, sequence_number, data_blob) " +
                "VALUES ($user_uid, $data_hash, $received_utc, $player_name, $world_id, $race_id, $tribe_id, $gender, $session_id, $sequence_number, $data_blob);";

            command.Parameters.AddWithValue("$user_uid", userUid);
            command.Parameters.AddWithValue("$data_hash", dataHash);
            command.Parameters.AddWithValue("$received_utc", received);
            command.Parameters.AddWithValue("$player_name", (object?)playerName ?? DBNull.Value);
            command.Parameters.AddWithValue("$world_id", (object?)worldId ?? DBNull.Value);
            command.Parameters.AddWithValue("$race_id", (object?)raceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$tribe_id", (object?)tribeId ?? DBNull.Value);
            command.Parameters.AddWithValue("$gender", (object?)gender ?? DBNull.Value);
            command.Parameters.AddWithValue("$session_id", (object?)sessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$sequence_number", (object?)sequenceNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("$data_blob", dataBlob);

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to store pair character data");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Upserts a learned mod state into the database.
    /// </summary>
    public async Task UpsertLearnedModAsync(string characterKey, LearnedModState state)
    {
        var settingsJson = JsonSerializer.Serialize(state.Settings);
        var settingsHash = ComputeHash(settingsJson);
        var fragmentsJson = JsonSerializer.Serialize(state.Fragments);
        var scdLinksJson = JsonSerializer.Serialize(state.ScdLinks);
        var papEmotesJson = JsonSerializer.Serialize(state.PapEmotes);
        var lastUpdated = new DateTimeOffset(state.LastUpdated).ToUnixTimeMilliseconds();
        Logger.LogDebug("Upsert learned mod: key={key} mod={mod} settingsHash={settingsHash} scdLinksLength={length}",
            characterKey, state.ModDirectoryName, settingsHash, scdLinksJson.Length);

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync().ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO learned_mods (character_key, mod_directory_name, mod_version, settings_hash, settings_json, fragments_json, scd_links_json, pap_emotes_json, last_updated_utc)
                VALUES ($character_key, $mod_directory_name, $mod_version, $settings_hash, $settings_json, $fragments_json, $scd_links_json, $pap_emotes_json, $last_updated_utc)
                ON CONFLICT(character_key, mod_directory_name, settings_hash) DO UPDATE SET
                    mod_version = excluded.mod_version,
                    fragments_json = excluded.fragments_json,
                    scd_links_json = excluded.scd_links_json,
                    pap_emotes_json = excluded.pap_emotes_json,
                    last_updated_utc = excluded.last_updated_utc;
            ";

            command.Parameters.AddWithValue("$character_key", characterKey);
            command.Parameters.AddWithValue("$mod_directory_name", state.ModDirectoryName);
            command.Parameters.AddWithValue("$mod_version", (object?)state.ModVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("$settings_hash", settingsHash);
            command.Parameters.AddWithValue("$settings_json", settingsJson);
            command.Parameters.AddWithValue("$fragments_json", fragmentsJson);
            command.Parameters.AddWithValue("$scd_links_json", scdLinksJson);
            command.Parameters.AddWithValue("$pap_emotes_json", papEmotesJson);
            command.Parameters.AddWithValue("$last_updated_utc", lastUpdated);

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to upsert learned mod state");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Stores local character data in SQLite.
    /// </summary>
    public async Task UpsertLocalCharacterDataAsync(API.Data.CharacterData characterData)
    {
        await StoreLocalCharacterDataAsync(characterData).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves the local character data from SQLite.
    /// </summary>
    public async Task<API.Data.CharacterData?> GetLocalCharacterDataAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync().ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT data_blob FROM local_character_data LIMIT 1;";

            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            if (await reader.ReadAsync().ConfigureAwait(false))
            {
                var blob = (byte[])reader["data_blob"];
                return MessagePackSerializer.Deserialize<API.Data.CharacterData>(blob, SerializerOptions);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to retrieve local character data");
        }
        finally
        {
            _writeLock.Release();
        }

        return null;
    }

    /// <summary>
    /// Retrieves all learned mods for a specific character.
    /// </summary>
    public async Task<List<LearnedModState>> GetLearnedModsAsync(string characterKey)
    {
        var results = new List<LearnedModState>();
        
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync().ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT mod_directory_name, mod_version, settings_json, fragments_json, scd_links_json, pap_emotes_json, last_updated_utc FROM learned_mods WHERE character_key = $character_key";
            command.Parameters.AddWithValue("$character_key", characterKey);

            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var modDir = reader.GetString(0);
                var modVersion = await reader.IsDBNullAsync(1).ConfigureAwait(false) ? null : reader.GetString(1);
                var settingsJson = reader.GetString(2);
                var fragmentsJson = reader.GetString(3);
                var scdLinksJson = await reader.IsDBNullAsync(4).ConfigureAwait(false) ? null : reader.GetString(4);
                var papEmotesJson = await reader.IsDBNullAsync(5).ConfigureAwait(false) ? null : reader.GetString(5);
                var lastUpdated = reader.GetInt64(6);

                var settings = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(settingsJson) ?? [];
                var fragments = JsonSerializer.Deserialize<Dictionary<ObjectKind, ModFileFragment>>(fragmentsJson) ?? [];
                var scdLinks = string.IsNullOrWhiteSpace(scdLinksJson)
                    ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    : JsonSerializer.Deserialize<Dictionary<string, List<string>>>(scdLinksJson) ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var papEmotes = string.IsNullOrWhiteSpace(papEmotesJson)
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(papEmotesJson) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var fragment in fragments.Values)
                {
                    if (fragment == null) continue;
                    fragment.FileReplacements ??= [];
                    fragment.JobFileReplacements ??= [];
                    foreach (var jobEntry in fragment.JobFileReplacements.ToList())
                    {
                        if (jobEntry.Value == null)
                        {
                            fragment.JobFileReplacements[jobEntry.Key] = [];
                        }
                    }
                }

                results.Add(new LearnedModState
                {
                    ModDirectoryName = modDir,
                    ModVersion = modVersion,
                    Settings = settings,
                    Fragments = fragments,
                    ScdLinks = scdLinks,
                    PapEmotes = papEmotes,
                    LastUpdated = DateTimeOffset.FromUnixTimeMilliseconds(lastUpdated).UtcDateTime
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to retrieve learned mods");
        }
        finally
        {
            _writeLock.Release();
        }

        return results;
    }

    /// <summary>
    /// Retrieves the distinct character keys that have learned mods.
    /// </summary>
    public async Task<List<string>> GetLearnedModCharacterKeysAsync()
    {
        var results = new List<string>();
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync().ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT character_key FROM learned_mods ORDER BY character_key;";

            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                results.Add(reader.GetString(0));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to retrieve learned mod character keys");
        }
        finally
        {
            _writeLock.Release();
        }

        return results;
    }

    private static string ComputeHash(string input)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Reads the latest pair character data for a user.
    /// </summary>
    public async Task<PersistedPairCharacterData?> GetLatestPairDataAsync(string userUid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userUid)) return null;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT data_hash, received_utc, player_name, world_id, race_id, tribe_id, gender, data_blob " +
                "FROM pair_character_data WHERE user_uid = $user_uid ORDER BY received_utc DESC LIMIT 1;";
            command.Parameters.AddWithValue("$user_uid", userUid);

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var dataHash = reader.GetString(0);
            var receivedUtc = reader.GetInt64(1);
            var playerName = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2);
            var worldId = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? (int?)null : reader.GetInt32(3);
            var raceId = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? (byte?)null : reader.GetByte(4);
            var tribeId = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? (byte?)null : reader.GetByte(5);
            var gender = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? (byte?)null : reader.GetByte(6);
            var blob = (byte[])reader["data_blob"];
            var characterData = MessagePackSerializer.Deserialize<API.Data.CharacterData>(blob, SerializerOptions);
            return new PersistedPairCharacterData(characterData, dataHash, DateTimeOffset.FromUnixTimeMilliseconds(receivedUtc), playerName, worldId, raceId, tribeId, gender);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to retrieve pair character data");
        }
        finally
        {
            _writeLock.Release();
        }

        return null;
    }

    /// <summary>
    /// Stores local character data in SQLite.
    /// </summary>
    private async Task StoreLocalCharacterDataAsync(API.Data.CharacterData characterData)
    {
        var dataHash = characterData.DataHash?.Value;
        if (string.IsNullOrWhiteSpace(dataHash)) return;

        var playerName = await _dalamudUtilService.GetPlayerNameAsync().ConfigureAwait(false);
        var homeWorldId = await _dalamudUtilService.GetHomeWorldIdAsync().ConfigureAwait(false);
        var dataJson = JsonSerializer.Serialize(characterData);
        var dataBlob = MessagePackSerializer.Serialize(characterData, SerializerOptions);
        var created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync().ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM local_character_data;" +
                "INSERT OR REPLACE INTO local_character_data (data_hash, created_utc, player_name, home_world_id, data_json, data_blob) " +
                "VALUES ($data_hash, $created_utc, $player_name, $home_world_id, $data_json, $data_blob);";
            command.Parameters.AddWithValue("$data_hash", dataHash);
            command.Parameters.AddWithValue("$created_utc", created);
            command.Parameters.AddWithValue("$player_name", (object?)playerName ?? DBNull.Value);
            command.Parameters.AddWithValue("$home_world_id", homeWorldId == 0 ? DBNull.Value : (object)(long)homeWorldId);
            command.Parameters.AddWithValue("$data_json", (object?)dataJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$data_blob", dataBlob);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to store local character data");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private string GetConnectionString()
        => $"Data Source={_databasePath};Mode=ReadWriteCreate;Cache=Shared";

    /// <summary>
    /// Stores a resource link between a parent file and an SCD file.
    /// </summary>
    public async Task UpsertResourceLinkAsync(string modName, string parentGamePath, string scdGamePath, DateTimeOffset? seenAt = null)
    {
        if (string.IsNullOrWhiteSpace(modName) || string.IsNullOrWhiteSpace(parentGamePath) || string.IsNullOrWhiteSpace(scdGamePath)) return;
        var seenUtc = (seenAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync().ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO resource_links (mod_name, parent_game_path, scd_game_path, last_seen_utc) " +
                "VALUES ($mod_name, $parent_game_path, $scd_game_path, $last_seen_utc) " +
                "ON CONFLICT(mod_name, parent_game_path, scd_game_path) DO UPDATE SET " +
                "last_seen_utc = excluded.last_seen_utc;";
            command.Parameters.AddWithValue("$mod_name", modName);
            command.Parameters.AddWithValue("$parent_game_path", parentGamePath);
            command.Parameters.AddWithValue("$scd_game_path", scdGamePath);
            command.Parameters.AddWithValue("$last_seen_utc", seenUtc);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to upsert resource link");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Retrieves all stored resource links.
    /// </summary>
    public async Task<Dictionary<string, Dictionary<string, HashSet<string>>>> GetResourceLinksAsync()
    {
        var result = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync().ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT mod_name, parent_game_path, scd_game_path FROM resource_links;";
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var modName = reader.GetString(0);
                var parentPath = reader.GetString(1);
                var scdPath = reader.GetString(2);

                if (!result.TryGetValue(modName, out var map))
                {
                    map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                    result[modName] = map;
                }

                if (!map.TryGetValue(parentPath, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    map[parentPath] = set;
                }
                set.Add(scdPath);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load resource links");
        }
        finally
        {
            _writeLock.Release();
        }

        return result;
    }

    private async Task InitializeDatabaseAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(Path.GetDirectoryName(_databasePath)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            }

            using var connection = new SqliteConnection(GetConnectionString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE IF NOT EXISTS local_character_data (" +
                "data_hash TEXT PRIMARY KEY, " +
                "created_utc INTEGER NOT NULL, " +
                "player_name TEXT NULL, " +
                "home_world_id INTEGER NULL, " +
                "data_json TEXT NULL, " +
                "data_blob BLOB NOT NULL" +
                ");" +
                "CREATE TABLE IF NOT EXISTS pair_character_data (" +
                "user_uid TEXT NOT NULL PRIMARY KEY, " +
                "data_hash TEXT NOT NULL, " +
                "received_utc INTEGER NOT NULL, " +
                "player_name TEXT NULL, " +
                "world_id INTEGER NULL, " +
                "race_id INTEGER NULL, " +
                "tribe_id INTEGER NULL, " +
                "gender INTEGER NULL, " +
                "session_id TEXT NULL, " +
                "sequence_number INTEGER NULL, " +
                "data_blob BLOB NOT NULL" +
                ");" +
                "CREATE INDEX IF NOT EXISTS idx_pair_character_lookup ON pair_character_data (player_name, world_id, race_id, tribe_id);" +
                "CREATE TABLE IF NOT EXISTS content_addressable_cache (" +
                "content_hash TEXT PRIMARY KEY, " +
                "content_type TEXT NULL, " +
                "content_blob BLOB NOT NULL, " +
                "content_size INTEGER NOT NULL, " +
                "created_utc INTEGER NOT NULL" +
                ");" +
                "CREATE TABLE IF NOT EXISTS patch_layer_entry (" +
                "entry_id INTEGER PRIMARY KEY AUTOINCREMENT, " +
                "owner_uid TEXT NULL, " +
                "priority INTEGER NOT NULL, " +
                "content_hash TEXT NOT NULL, " +
                "source_mod TEXT NULL, " +
                "option_path TEXT NULL, " +
                "created_utc INTEGER NOT NULL, " +
                "FOREIGN KEY(content_hash) REFERENCES content_addressable_cache(content_hash)" +
                ");" +
                "CREATE TABLE IF NOT EXISTS learned_mods (" +
                "id INTEGER PRIMARY KEY AUTOINCREMENT," +
                "character_key TEXT NOT NULL," +
                "mod_directory_name TEXT NOT NULL," +
                "mod_version TEXT NULL," +
                "settings_hash TEXT NOT NULL," +
                "settings_json TEXT NOT NULL," +
                "fragments_json TEXT NOT NULL," +
                "scd_links_json TEXT NULL," +
                "pap_emotes_json TEXT NULL," +
                "last_updated_utc INTEGER NOT NULL," +
                "UNIQUE(character_key, mod_directory_name, settings_hash)" +
                ");" +
                "CREATE TABLE IF NOT EXISTS resource_links (" +
                "mod_name TEXT NOT NULL," +
                "parent_game_path TEXT NOT NULL," +
                "scd_game_path TEXT NOT NULL," +
                "last_seen_utc INTEGER NOT NULL," +
                "PRIMARY KEY(mod_name, parent_game_path, scd_game_path)" +
                ");" +
                "CREATE INDEX IF NOT EXISTS idx_resource_links_mod ON resource_links (mod_name);";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await EnsurePairCharacterDataPrimaryKeyAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsureLocalCharacterDataColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsureLearnedModColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to initialize character data SQLite store");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task EnsureLocalCharacterDataColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = await GetTableColumnsAsync(connection, "local_character_data", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, columns, "local_character_data", "player_name", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, columns, "local_character_data", "home_world_id", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, columns, "local_character_data", "data_json", "TEXT NULL", cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureLearnedModColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = await GetTableColumnsAsync(connection, "learned_mods", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, columns, "learned_mods", "mod_version", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, columns, "learned_mods", "scd_links_json", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, columns, "learned_mods", "pap_emotes_json", "TEXT NULL", cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsurePairCharacterDataPrimaryKeyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var primaryKeyColumns = await GetPrimaryKeyColumnsAsync(connection, "pair_character_data", cancellationToken).ConfigureAwait(false);
        if (primaryKeyColumns.Count == 1 && primaryKeyColumns.Contains("user_uid"))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS pair_character_data_new (" +
            "user_uid TEXT NOT NULL PRIMARY KEY, " +
            "data_hash TEXT NOT NULL, " +
            "received_utc INTEGER NOT NULL, " +
            "player_name TEXT NULL, " +
            "world_id INTEGER NULL, " +
            "race_id INTEGER NULL, " +
            "tribe_id INTEGER NULL, " +
            "gender INTEGER NULL, " +
            "session_id TEXT NULL, " +
            "sequence_number INTEGER NULL, " +
            "data_blob BLOB NOT NULL" +
            ");" +
            "INSERT OR REPLACE INTO pair_character_data_new " +
            "(user_uid, data_hash, received_utc, player_name, world_id, race_id, tribe_id, gender, session_id, sequence_number, data_blob) " +
            "SELECT p.user_uid, p.data_hash, p.received_utc, p.player_name, p.world_id, p.race_id, p.tribe_id, p.gender, p.session_id, p.sequence_number, p.data_blob " +
            "FROM pair_character_data p " +
            "JOIN (SELECT user_uid, MAX(rowid) AS max_rowid FROM pair_character_data GROUP BY user_uid) latest " +
            "ON p.user_uid = latest.user_uid AND p.rowid = latest.max_rowid;" +
            "DROP TABLE pair_character_data;" +
            "ALTER TABLE pair_character_data_new RENAME TO pair_character_data;" +
            "CREATE INDEX IF NOT EXISTS idx_pair_character_lookup ON pair_character_data (player_name, world_id, race_id, tribe_id);";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HashSet<string>> GetTableColumnsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static async Task<HashSet<string>> GetPrimaryKeyColumnsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(1);
            var pk = reader.GetInt32(5);
            if (pk > 0)
            {
                columns.Add(name);
            }
        }
        return columns;
    }

    private static async Task AddColumnIfMissingAsync(SqliteConnection connection, HashSet<string> columns, string tableName, string columnName, string columnDefinition, CancellationToken cancellationToken)
    {
        if (columns.Contains(columnName)) return;
        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    /// <summary>
    /// Represents persisted pair character data loaded from SQLite.
    /// </summary>
    public sealed record PersistedPairCharacterData(API.Data.CharacterData CharacterData, string DataHash, DateTimeOffset ReceivedAt, string? PlayerName, int? WorldId, byte? RaceId, byte? TribeId, byte? Gender);
}
