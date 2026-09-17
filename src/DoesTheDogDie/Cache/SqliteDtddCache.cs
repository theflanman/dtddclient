using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DoesTheDogDie.Api;
using Microsoft.Data.Sqlite;

namespace DoesTheDogDie.Cache;

/// <summary>
/// A persistent, file-backed <see cref="IDtddCache"/> implementation using SQLite via
/// <see cref="Microsoft.Data.Sqlite"/>. Each operation opens and closes its own connection, so instances are
/// safe to use concurrently from multiple threads. The connection string is stored, not a shared connection.
/// </summary>
public sealed class SqliteDtddCache : IDtddCache
{
    private const int AllTopicsSentinel = -1;

    private readonly string _connectionString;
    private readonly CachePolicy _policy;
    private readonly TimeProvider _time;

    /// <summary>
    /// Opens (or creates) the SQLite database at <paramref name="connectionString"/>, ensuring the schema
    /// exists and matches the supported version. Throws <see cref="InvalidOperationException"/> if an
    /// existing database has a different schema version, since no migrations are available yet.
    /// </summary>
    public SqliteDtddCache(string connectionString, CachePolicy? policy = null, TimeProvider? timeProvider = null)
    {
        _connectionString = connectionString;
        _policy = policy ?? CachePolicy.Default;
        _time = timeProvider ?? TimeProvider.System;

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        using (var create = connection.CreateCommand())
        {
            create.CommandText = string.Join(
                '\n',
                SqliteSchema.CreateMeta,
                SqliteSchema.CreateItems,
                SqliteSchema.CreateItemsImdbIndex,
                SqliteSchema.CreateItemsTmdbIndex,
                SqliteSchema.CreateRatings,
                SqliteSchema.CreateTaxonomy,
                SqliteSchema.CreateLookups);
            create.ExecuteNonQuery();
        }

        using (var versionCheck = connection.CreateCommand())
        {
            versionCheck.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
            var existing = versionCheck.ExecuteScalar() as string;

            if (existing is null)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO meta(key, value) VALUES ('schema_version', $version);";
                insert.Parameters.AddWithValue("$version", SqliteSchema.Version);
                insert.ExecuteNonQuery();
            }
            else if (existing != SqliteSchema.Version)
            {
                throw new InvalidOperationException(
                    $"Cache database has schema version '{existing}' but this library supports version '{SqliteSchema.Version}'. No migrations are available.");
            }
        }
    }

    public async Task<CacheEntry<ItemDetail>?> GetItemAsync(int itemId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT fetched_at, json FROM items WHERE id = $id;";
        command.Parameters.AddWithValue("$id", itemId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var fetchedAt = ParseFetchedAt(reader.GetString(0));
        var item = JsonSerializer.Deserialize(reader.GetString(1), DtddJsonContext.Default.ItemDetail)!;
        return ToEntry(item, fetchedAt, _policy.ItemMaxAge);
    }

    public async Task PutItemAsync(ItemDetail item, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO items(id, imdb_id, tmdb_id, item_type_id, release_year, fetched_at, json)
            VALUES ($id, $imdbId, $tmdbId, $itemTypeId, $releaseYear, $fetchedAt, $json)
            ON CONFLICT(id) DO UPDATE SET
                imdb_id = excluded.imdb_id,
                tmdb_id = excluded.tmdb_id,
                item_type_id = excluded.item_type_id,
                release_year = excluded.release_year,
                fetched_at = excluded.fetched_at,
                json = excluded.json;
            """;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$imdbId", (object?)item.ImdbId ?? DBNull.Value);
        command.Parameters.AddWithValue("$tmdbId", (object?)item.TmdbId ?? DBNull.Value);
        command.Parameters.AddWithValue("$itemTypeId", item.ItemTypeId);
        command.Parameters.AddWithValue("$releaseYear", (object?)item.ReleaseYear ?? DBNull.Value);
        command.Parameters.AddWithValue("$fetchedAt", FormatFetchedAt(fetchedAt));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(item, DtddJsonContext.Default.ItemDetail));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<CacheEntry<IReadOnlyList<Rating>>?> GetRatingsAsync(int itemId, int? topicId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT fetched_at, json FROM ratings WHERE item_id = $itemId AND topic_id = $topicId;";
        command.Parameters.AddWithValue("$itemId", itemId);
        command.Parameters.AddWithValue("$topicId", topicId ?? AllTopicsSentinel);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var fetchedAt = ParseFetchedAt(reader.GetString(0));
        var ratings = JsonSerializer.Deserialize(reader.GetString(1), DtddJsonContext.Default.ListRating)!;
        return ToEntry<IReadOnlyList<Rating>>(ratings, fetchedAt, _policy.RatingsMaxAge);
    }

    public async Task PutRatingsAsync(int itemId, int? topicId, IReadOnlyList<Rating> ratings, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ratings(item_id, topic_id, fetched_at, json)
            VALUES ($itemId, $topicId, $fetchedAt, $json)
            ON CONFLICT(item_id, topic_id) DO UPDATE SET
                fetched_at = excluded.fetched_at,
                json = excluded.json;
            """;
        command.Parameters.AddWithValue("$itemId", itemId);
        command.Parameters.AddWithValue("$topicId", topicId ?? AllTopicsSentinel);
        command.Parameters.AddWithValue("$fetchedAt", FormatFetchedAt(fetchedAt));
        command.Parameters.AddWithValue(
            "$json",
            JsonSerializer.Serialize(AsList(ratings), DtddJsonContext.Default.ListRating));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public Task<CacheEntry<IReadOnlyList<Topic>>?> GetTopicsAsync(CancellationToken ct = default) =>
        GetTaxonomyAsync("topics", DtddJsonContext.Default.ListTopic, _policy.TaxonomyMaxAge, ct);

    public Task PutTopicsAsync(IReadOnlyList<Topic> topics, DateTimeOffset fetchedAt, CancellationToken ct = default) =>
        PutTaxonomyAsync("topics", topics, fetchedAt, DtddJsonContext.Default.ListTopic, ct);

    public Task<CacheEntry<IReadOnlyList<ItemType>>?> GetItemTypesAsync(CancellationToken ct = default) =>
        GetTaxonomyAsync("itemtypes", DtddJsonContext.Default.ListItemType, _policy.TaxonomyMaxAge, ct);

    public Task PutItemTypesAsync(IReadOnlyList<ItemType> itemTypes, DateTimeOffset fetchedAt, CancellationToken ct = default) =>
        PutTaxonomyAsync("itemtypes", itemTypes, fetchedAt, DtddJsonContext.Default.ListItemType, ct);

    public Task<CacheEntry<IReadOnlyList<TopicCategory>>?> GetTopicCategoriesAsync(CancellationToken ct = default) =>
        GetTaxonomyAsync("topiccategories", DtddJsonContext.Default.ListTopicCategory, _policy.TaxonomyMaxAge, ct);

    public Task PutTopicCategoriesAsync(IReadOnlyList<TopicCategory> topicCategories, DateTimeOffset fetchedAt, CancellationToken ct = default) =>
        PutTaxonomyAsync("topiccategories", topicCategories, fetchedAt, DtddJsonContext.Default.ListTopicCategory, ct);

    public Task<CacheEntry<IReadOnlyList<TopicSuperCategory>>?> GetTopicSuperCategoriesAsync(CancellationToken ct = default) =>
        GetTaxonomyAsync("topicsupercategories", DtddJsonContext.Default.ListTopicSuperCategory, _policy.TaxonomyMaxAge, ct);

    public Task PutTopicSuperCategoriesAsync(IReadOnlyList<TopicSuperCategory> topicSuperCategories, DateTimeOffset fetchedAt, CancellationToken ct = default) =>
        PutTaxonomyAsync("topicsupercategories", topicSuperCategories, fetchedAt, DtddJsonContext.Default.ListTopicSuperCategory, ct);

    public async Task<CacheEntry<int?>?> GetLookupAsync(LookupKey key, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_id, fetched_at FROM lookups WHERE kind = $kind AND key = $key;";
        command.Parameters.AddWithValue("$kind", key.Kind.ToString());
        command.Parameters.AddWithValue("$key", key.Key);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        int? itemId = reader.IsDBNull(0) ? null : reader.GetInt32(0);
        var fetchedAt = ParseFetchedAt(reader.GetString(1));
        var maxAge = itemId is null ? _policy.NegativeLookupMaxAge : _policy.ItemMaxAge;
        var isStale = _time.GetUtcNow() - fetchedAt > maxAge;
        return new CacheEntry<int?>(itemId, fetchedAt, isStale);
    }

    public async Task PutLookupAsync(LookupKey key, int? itemId, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO lookups(kind, key, item_id, fetched_at)
            VALUES ($kind, $key, $itemId, $fetchedAt)
            ON CONFLICT(kind, key) DO UPDATE SET
                item_id = excluded.item_id,
                fetched_at = excluded.fetched_at;
            """;
        command.Parameters.AddWithValue("$kind", key.Kind.ToString());
        command.Parameters.AddWithValue("$key", key.Key);
        command.Parameters.AddWithValue("$itemId", (object?)itemId ?? DBNull.Value);
        command.Parameters.AddWithValue("$fetchedAt", FormatFetchedAt(fetchedAt));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<CacheEntry<IReadOnlyList<T>>?> GetTaxonomyAsync<T>(
        string kind, JsonTypeInfo<List<T>> typeInfo, TimeSpan maxAge, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT fetched_at, json FROM taxonomy WHERE kind = $kind;";
        command.Parameters.AddWithValue("$kind", kind);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var fetchedAt = ParseFetchedAt(reader.GetString(0));
        var list = JsonSerializer.Deserialize(reader.GetString(1), typeInfo)!;
        return ToEntry<IReadOnlyList<T>>(list, fetchedAt, maxAge);
    }

    private async Task PutTaxonomyAsync<T>(
        string kind, IReadOnlyList<T> values, DateTimeOffset fetchedAt, JsonTypeInfo<List<T>> typeInfo, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO taxonomy(kind, fetched_at, json)
            VALUES ($kind, $fetchedAt, $json)
            ON CONFLICT(kind) DO UPDATE SET
                fetched_at = excluded.fetched_at,
                json = excluded.json;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$fetchedAt", FormatFetchedAt(fetchedAt));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(AsList(values), typeInfo));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private CacheEntry<T> ToEntry<T>(T value, DateTimeOffset fetchedAt, TimeSpan maxAge)
    {
        var isStale = _time.GetUtcNow() - fetchedAt > maxAge;
        return new CacheEntry<T>(value, fetchedAt, isStale);
    }

    private static List<T> AsList<T>(IReadOnlyList<T> values) => values as List<T> ?? [.. values];

    private static string FormatFetchedAt(DateTimeOffset fetchedAt) =>
        fetchedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseFetchedAt(string value) =>
        DateTimeOffset.ParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
