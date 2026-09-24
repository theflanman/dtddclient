namespace DoesTheDogDie.Cache;

/// <summary>DDL and version constant for the <see cref="SqliteDtddCache"/> schema.</summary>
/// <remarks>
/// Every DtDD model is stored relationally, one column per member. A column holding a model member takes that
/// member's snake_case name; a structural column (owner key, fetch key, ordinal) is named for its role, which is
/// why <c>topic_item_stats</c> has both <c>owner_item_id</c> and the model's own <c>item_id</c>.
/// <para>
/// Child rows are keyed by (owning fetch, ordinal) rather than by any model id, so the cache never rejects a
/// payload the API actually returned - and paid budget for. Fetch-marker tables (<c>rating_sets</c>,
/// <c>taxonomy</c>) record that a fetch happened, which is what tells a cached empty list apart from one never
/// fetched. There are no foreign-key clauses: SQLite enforces them only under a per-connection pragma, and an
/// unenforced constraint would mislead. Puts delete child rows explicitly inside their transaction instead.
/// </para>
/// </remarks>
internal static class SqliteSchema
{
    /// <summary>The current schema version stored in the <c>meta</c> table under key <c>schema_version</c>.</summary>
    public const string Version = "2";

    public const string CreateMeta = """
        CREATE TABLE IF NOT EXISTS meta(
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    public const string CreateItems = """
        CREATE TABLE IF NOT EXISTS items(
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            release_year INTEGER,
            item_type_id INTEGER NOT NULL,
            item_type_name TEXT NOT NULL,
            tmdb_id INTEGER,
            imdb_id TEXT,
            background_image TEXT,
            poster_image TEXT,
            overview TEXT,
            fetched_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS items_imdb ON items(imdb_id);
        CREATE INDEX IF NOT EXISTS items_tmdb ON items(tmdb_id);
        """;

    public const string CreateItemGenres = """
        CREATE TABLE IF NOT EXISTS item_genres(
            owner_item_id INTEGER NOT NULL,
            ordinal INTEGER NOT NULL,
            genre TEXT NOT NULL,
            PRIMARY KEY(owner_item_id, ordinal)
        );
        """;

    public const string CreateTopicItemStats = """
        CREATE TABLE IF NOT EXISTS topic_item_stats(
            owner_item_id INTEGER NOT NULL,
            ordinal INTEGER NOT NULL,
            topic_item_id INTEGER NOT NULL,
            yes_sum INTEGER NOT NULL,
            no_sum INTEGER NOT NULL,
            num_comments INTEGER NOT NULL,
            topic_id INTEGER NOT NULL,
            topic_name TEXT NOT NULL,
            item_id INTEGER NOT NULL,
            PRIMARY KEY(owner_item_id, ordinal)
        );
        """;

    /// <summary>One row per ratings fetch. <c>topic_id</c> is -1 for the all-topics fetch.</summary>
    public const string CreateRatingSets = """
        CREATE TABLE IF NOT EXISTS rating_sets(
            item_id INTEGER NOT NULL,
            topic_id INTEGER NOT NULL,
            fetched_at TEXT NOT NULL,
            PRIMARY KEY(item_id, topic_id)
        );
        """;

    /// <summary>
    /// One row per rating, per set. The all-topics fetch and a per-topic fetch can return the same rating, so
    /// the set it arrived in is part of the key.
    /// </summary>
    public const string CreateRatings = """
        CREATE TABLE IF NOT EXISTS ratings(
            set_item_id INTEGER NOT NULL,
            set_topic_id INTEGER NOT NULL,
            ordinal INTEGER NOT NULL,
            id INTEGER NOT NULL,
            yes INTEGER NOT NULL,
            no INTEGER NOT NULL,
            vote_sum INTEGER NOT NULL,
            trigger_description TEXT,
            is_rampant INTEGER NOT NULL,
            index1 INTEGER NOT NULL,
            index2 INTEGER NOT NULL,
            position1 INTEGER,
            position2 INTEGER,
            position3 INTEGER,
            safe_position1 INTEGER,
            safe_position2 INTEGER,
            safe_position3 INTEGER,
            cue_description TEXT,
            item_id INTEGER NOT NULL,
            topic_id INTEGER NOT NULL,
            is_scene_alert INTEGER NOT NULL,
            PRIMARY KEY(set_item_id, set_topic_id, ordinal)
        );
        """;

    /// <summary>One row per taxonomy fetch, keyed by kind (<c>topics</c>, <c>itemtypes</c>, ...).</summary>
    public const string CreateTaxonomy = """
        CREATE TABLE IF NOT EXISTS taxonomy(
            kind TEXT PRIMARY KEY,
            fetched_at TEXT NOT NULL
        );
        """;

    public const string CreateTopics = """
        CREATE TABLE IF NOT EXISTS topics(
            ordinal INTEGER PRIMARY KEY,
            id INTEGER NOT NULL,
            name TEXT NOT NULL,
            not_name TEXT,
            keywords TEXT,
            description TEXT,
            does_name TEXT,
            list_name TEXT,
            minimal_name TEXT,
            topic_category_id INTEGER NOT NULL,
            alt_topic_category_id INTEGER
        );
        """;

    public const string CreateItemTypes = """
        CREATE TABLE IF NOT EXISTS item_types(
            ordinal INTEGER PRIMARY KEY,
            id INTEGER NOT NULL,
            name TEXT NOT NULL,
            slug TEXT NOT NULL,
            verb TEXT NOT NULL,
            past_tense_verb TEXT NOT NULL,
            index1_label TEXT,
            index2_label TEXT,
            position1_label TEXT,
            position2_label TEXT,
            position3_label TEXT
        );
        """;

    public const string CreateTopicCategories = """
        CREATE TABLE IF NOT EXISTS topic_categories(
            ordinal INTEGER PRIMARY KEY,
            id INTEGER NOT NULL,
            name TEXT NOT NULL,
            topic_super_category_id INTEGER NOT NULL
        );
        """;

    public const string CreateTopicSuperCategories = """
        CREATE TABLE IF NOT EXISTS topic_super_categories(
            ordinal INTEGER PRIMARY KEY,
            id INTEGER NOT NULL,
            name TEXT NOT NULL,
            short_name TEXT NOT NULL
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

    /// <summary>
    /// <see cref="ThrottledDtddClient"/> budget state, one row per budget id (see <see cref="IBudgetStore"/>).
    /// <c>observed_at</c> is null exactly when no budget was ever observed. Added to schema v2 without a version bump:
    /// it is purely additive, so older v2 readers ignore it, and <c>CREATE TABLE IF NOT EXISTS</c> on open adds it to
    /// a v2 database that predates it.
    /// </summary>
    public const string CreateBudgetState = """
        CREATE TABLE IF NOT EXISTS budget_state(
            budget_id TEXT PRIMARY KEY,
            minute_limit INTEGER,
            minute_remaining INTEGER,
            month_limit INTEGER,
            month_remaining INTEGER,
            observed_at TEXT,
            month_ends_at TEXT,
            exhausted_until TEXT,
            background_held_until TEXT
        );
        """;

    /// <summary>Every statement needed to create the current schema. Each is idempotent.</summary>
    public static readonly string CreateAll = string.Join(
        '\n',
        CreateMeta,
        CreateItems,
        CreateItemGenres,
        CreateTopicItemStats,
        CreateRatingSets,
        CreateRatings,
        CreateTaxonomy,
        CreateTopics,
        CreateItemTypes,
        CreateTopicCategories,
        CreateTopicSuperCategories,
        CreateLookups,
        CreateBudgetState);
}
