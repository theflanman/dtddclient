namespace DoesTheDogDie.Cache;

/// <summary>DDL and version constant for the <see cref="SqliteDtddCache"/> schema.</summary>
internal static class SqliteSchema
{
    /// <summary>The current schema version stored in the <c>meta</c> table under key <c>schema_version</c>.</summary>
    public const string Version = "1";

    public const string CreateMeta = """
        CREATE TABLE IF NOT EXISTS meta(
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    public const string CreateItems = """
        CREATE TABLE IF NOT EXISTS items(
            id INTEGER PRIMARY KEY,
            imdb_id TEXT,
            tmdb_id INTEGER,
            item_type_id INTEGER NOT NULL,
            release_year INTEGER,
            fetched_at TEXT NOT NULL,
            json TEXT NOT NULL
        );
        """;

    public const string CreateItemsImdbIndex = "CREATE INDEX IF NOT EXISTS items_imdb ON items(imdb_id);";

    public const string CreateItemsTmdbIndex = "CREATE INDEX IF NOT EXISTS items_tmdb ON items(tmdb_id);";

    public const string CreateRatings = """
        CREATE TABLE IF NOT EXISTS ratings(
            item_id INTEGER NOT NULL,
            topic_id INTEGER NOT NULL,
            fetched_at TEXT NOT NULL,
            json TEXT NOT NULL,
            PRIMARY KEY(item_id, topic_id)
        );
        """;

    public const string CreateTaxonomy = """
        CREATE TABLE IF NOT EXISTS taxonomy(
            kind TEXT PRIMARY KEY,
            fetched_at TEXT NOT NULL,
            json TEXT NOT NULL
        );
        """;

    public const string CreateLookups = """
        CREATE TABLE IF NOT EXISTS lookups(
            kind TEXT NOT NULL,
            key TEXT NOT NULL,
            item_id INTEGER,
            fetched_at TEXT NOT NULL,
            PRIMARY KEY(kind, key)
        );
        """;
}
