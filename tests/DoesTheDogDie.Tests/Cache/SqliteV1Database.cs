using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DoesTheDogDie.Api;
using DoesTheDogDie.Cache;
using Microsoft.Data.Sqlite;

namespace DoesTheDogDie.Tests.Cache;

/// <summary>
/// Writes a database in schema v1 exactly as <c>SqliteDtddCache</c> shipped it in v0.1.0: whole
/// <see cref="ItemDetail"/>s, rating sets and taxonomy lists as JSON blobs. The DDL and encodings below are
/// frozen copies, deliberately <b>not</b> taken from the production <c>SqliteSchema</c> - this is a historical
/// on-disk format that migration must keep reading long after the production schema has moved on.
/// </summary>
internal sealed class SqliteV1Database : IDisposable
{
    private const int AllTopicsSentinel = -1;

    private readonly SqliteConnection _connection;

    public SqliteV1Database(string path)
    {
        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("""
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE items(
                id INTEGER PRIMARY KEY,
                imdb_id TEXT,
                tmdb_id INTEGER,
                item_type_id INTEGER NOT NULL,
                release_year INTEGER,
                fetched_at TEXT NOT NULL,
                json TEXT NOT NULL
            );
            CREATE INDEX items_imdb ON items(imdb_id);
            CREATE INDEX items_tmdb ON items(tmdb_id);
            CREATE TABLE ratings(
                item_id INTEGER NOT NULL,
                topic_id INTEGER NOT NULL,
                fetched_at TEXT NOT NULL,
                json TEXT NOT NULL,
                PRIMARY KEY(item_id, topic_id)
            );
            CREATE TABLE taxonomy(kind TEXT PRIMARY KEY, fetched_at TEXT NOT NULL, json TEXT NOT NULL);
            CREATE TABLE lookups(
                kind TEXT NOT NULL,
                key TEXT NOT NULL,
                item_id INTEGER,
                fetched_at TEXT NOT NULL,
                PRIMARY KEY(kind, key)
            );
            INSERT INTO meta(key, value) VALUES ('schema_version', '1');
            """);
    }

    public void PutItem(ItemDetail item, DateTimeOffset fetchedAt) => Execute(
        "INSERT INTO items(id, imdb_id, tmdb_id, item_type_id, release_year, fetched_at, json) " +
        "VALUES ($id, $imdb, $tmdb, $type, $year, $at, $json);",
        ("$id", item.Id),
        ("$imdb", item.ImdbId),
        ("$tmdb", item.TmdbId),
        ("$type", item.ItemTypeId),
        ("$year", item.ReleaseYear),
        ("$at", Format(fetchedAt)),
        ("$json", JsonSerializer.Serialize(item, DtddJsonContext.Default.ItemDetail)));

    public void PutRatings(int itemId, int? topicId, IReadOnlyList<Rating> ratings, DateTimeOffset fetchedAt) => Execute(
        "INSERT INTO ratings(item_id, topic_id, fetched_at, json) VALUES ($item, $topic, $at, $json);",
        ("$item", itemId),
        ("$topic", topicId ?? AllTopicsSentinel),
        ("$at", Format(fetchedAt)),
        ("$json", JsonSerializer.Serialize(ratings.ToList(), DtddJsonContext.Default.ListRating)));

    public void PutTaxonomy<T>(string kind, IReadOnlyList<T> values, JsonTypeInfo<List<T>> typeInfo, DateTimeOffset fetchedAt) => Execute(
        "INSERT INTO taxonomy(kind, fetched_at, json) VALUES ($kind, $at, $json);",
        ("$kind", kind),
        ("$at", Format(fetchedAt)),
        ("$json", JsonSerializer.Serialize(values.ToList(), typeInfo)));

    public void PutLookup(LookupKey key, int? itemId, DateTimeOffset fetchedAt) => Execute(
        "INSERT INTO lookups(kind, key, item_id, fetched_at) VALUES ($kind, $key, $item, $at);",
        ("$kind", key.Kind.ToString()),
        ("$key", key.Key),
        ("$item", itemId),
        ("$at", Format(fetchedAt)));

    public void Dispose() => _connection.Dispose();

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }
}
