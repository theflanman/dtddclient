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
/// <remarks>
/// Every DtDD model is stored relationally (see <see cref="SqliteSchema"/>). A put spanning several tables runs
/// in one write transaction and a get in one read transaction, so a reader never observes an item whose child
/// rows are half replaced.
/// <para>
/// Row reads and writes are synchronous helpers shared by the public methods and the constructor-time
/// migration, so migrated data goes through exactly the code every round-trip test exercises. That costs the
/// async methods nothing: SQLite has no asynchronous I/O, so Microsoft.Data.Sqlite's async ADO.NET methods
/// complete synchronously anyway.
/// </para>
/// </remarks>
public sealed class SqliteDtddCache : IDtddCache, IBudgetStore
{
    private const int AllTopicsSentinel = -1;

    private const string BackupTimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    // Kind strings are the ones schema v1 used, which migration relies on to route each v1 taxonomy blob.
    private static readonly TaxonomyTable<Topic> Topics = new(
        "topics",
        "topics",
        ["id", "name", "not_name", "keywords", "description", "does_name", "list_name", "minimal_name", "topic_category_id", "alt_topic_category_id"],
        t => [t.Id, t.Name, t.NotName, t.Keywords, t.Description, t.DoesName, t.ListName, t.MinimalName, t.TopicCategoryId, t.AltTopicCategoryId],
        r => new Topic
        {
            Id = r.GetInt32(0),
            Name = r.GetString(1),
            NotName = GetNullableString(r, 2),
            Keywords = GetNullableString(r, 3),
            Description = GetNullableString(r, 4),
            DoesName = GetNullableString(r, 5),
            ListName = GetNullableString(r, 6),
            MinimalName = GetNullableString(r, 7),
            TopicCategoryId = r.GetInt32(8),
            AltTopicCategoryId = GetNullableInt32(r, 9),
        });

    private static readonly TaxonomyTable<ItemType> ItemTypes = new(
        "itemtypes",
        "item_types",
        ["id", "name", "slug", "verb", "past_tense_verb", "index1_label", "index2_label", "position1_label", "position2_label", "position3_label"],
        t => [t.Id, t.Name, t.Slug, t.Verb, t.PastTenseVerb, t.Index1Label, t.Index2Label, t.Position1Label, t.Position2Label, t.Position3Label],
        r => new ItemType
        {
            Id = r.GetInt32(0),
            Name = r.GetString(1),
            Slug = r.GetString(2),
            Verb = r.GetString(3),
            PastTenseVerb = r.GetString(4),
            Index1Label = GetNullableString(r, 5),
            Index2Label = GetNullableString(r, 6),
            Position1Label = GetNullableString(r, 7),
            Position2Label = GetNullableString(r, 8),
            Position3Label = GetNullableString(r, 9),
        });

    private static readonly TaxonomyTable<TopicCategory> TopicCategories = new(
        "topiccategories",
        "topic_categories",
        ["id", "name", "topic_super_category_id"],
        t => [t.Id, t.Name, t.TopicSuperCategoryId],
        r => new TopicCategory { Id = r.GetInt32(0), Name = r.GetString(1), TopicSuperCategoryId = r.GetInt32(2) });

    private static readonly TaxonomyTable<TopicSuperCategory> TopicSuperCategories = new(
        "topicsupercategories",
        "topic_super_categories",
        ["id", "name", "short_name"],
        t => [t.Id, t.Name, t.ShortName],
        r => new TopicSuperCategory { Id = r.GetInt32(0), Name = r.GetString(1), ShortName = r.GetString(2) });

    private readonly string _connectionString;
    private readonly CachePolicy _policy;
    private readonly TimeProvider _time;

    /// <summary>
    /// Opens (or creates) the SQLite database at <paramref name="connectionString"/>, ensuring the current schema
    /// exists. A schema-v1 database is migrated in place, after first snapshotting it to a timestamped
    /// <c>.v1-backup-*</c> file beside it. Throws <see cref="InvalidOperationException"/> for a schema version this
    /// library cannot migrate, or if the snapshot cannot be written - in which case the database is left unchanged.
    /// </summary>
    public SqliteDtddCache(string connectionString, CachePolicy? policy = null, TimeProvider? timeProvider = null)
    {
        _connectionString = connectionString;
        _policy = policy ?? CachePolicy.Default;
        _time = timeProvider ?? TimeProvider.System;

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, null, "PRAGMA journal_mode=WAL;");

        // M2: check the schema version BEFORE creating any table, so a database this library cannot open is
        // rejected without first being mutated.
        switch (ReadSchemaVersion(connection))
        {
            case null:
                Execute(connection, null, SqliteSchema.CreateAll);
                Execute(connection, null, "INSERT INTO meta(key, value) VALUES ('schema_version', $version);", ("$version", SqliteSchema.Version));
                break;
            case SqliteSchema.Version:
                Execute(connection, null, SqliteSchema.CreateAll);
                break;
            case "1":
                MigrateFromV1(connection);
                break;
            case var other:
                throw new InvalidOperationException(
                    $"Cache database has schema version '{other}' but this library supports version '{SqliteSchema.Version}' and has no migration from it.");
        }
    }

    /// <inheritdoc />
    public BudgetState? LoadBudget(string budgetId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        var rows = ReadRows(
            connection,
            null,
            """
            SELECT minute_limit, minute_remaining, month_limit, month_remaining, observed_at,
                   month_ends_at, exhausted_until, background_held_until
            FROM budget_state WHERE budget_id = $id;
            """,
            r => new BudgetState(
                r.IsDBNull(4)
                    ? null
                    : new RateLimitStatus(GetNullableInt32(r, 0), GetNullableInt32(r, 1), GetNullableInt32(r, 2), GetNullableInt32(r, 3), ParseFetchedAt(r.GetString(4))),
                GetNullableInstant(r, 5),
                GetNullableInstant(r, 6),
                GetNullableInstant(r, 7)),
            ("$id", budgetId));
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public void SaveBudget(string budgetId, BudgetState state)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(
            connection,
            null,
            """
            INSERT OR REPLACE INTO budget_state(budget_id, minute_limit, minute_remaining, month_limit, month_remaining,
                                                observed_at, month_ends_at, exhausted_until, background_held_until)
            VALUES ($id, $minuteLimit, $minuteRemaining, $monthLimit, $monthRemaining,
                    $observedAt, $monthEndsAt, $exhaustedUntil, $backgroundHeldUntil);
            """,
            ("$id", budgetId),
            ("$minuteLimit", state.Budget?.MinuteLimit),
            ("$minuteRemaining", state.Budget?.MinuteRemaining),
            ("$monthLimit", state.Budget?.MonthLimit),
            ("$monthRemaining", state.Budget?.MonthRemaining),
            ("$observedAt", state.Budget is { } budget ? FormatFetchedAt(budget.ObservedAt) : null),
            ("$monthEndsAt", FormatNullableInstant(state.MonthEndsAt)),
            ("$exhaustedUntil", FormatNullableInstant(state.ExhaustedUntil)),
            ("$backgroundHeldUntil", FormatNullableInstant(state.BackgroundHeldUntil)));
    }

    public async Task<CacheEntry<ItemDetail>?> GetItemAsync(int itemId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction(deferred: true);
        var read = ReadItem(connection, tx, itemId);
        tx.Commit();
        return read is { } r ? ToEntry(r.Item, r.FetchedAt, _policy.ItemMaxAge) : null;
    }

    public async Task PutItemAsync(ItemDetail item, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction();
        WriteItem(connection, tx, item, fetchedAt);
        tx.Commit();
    }

    public async Task<CacheEntry<IReadOnlyList<Rating>>?> GetRatingsAsync(int itemId, int? topicId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction(deferred: true);
        var read = ReadRatings(connection, tx, itemId, topicId ?? AllTopicsSentinel);
        tx.Commit();
        return read is { } r ? ToEntry<IReadOnlyList<Rating>>(r.Ratings, r.FetchedAt, _policy.RatingsMaxAge) : null;
    }

    public async Task PutRatingsAsync(int itemId, int? topicId, IReadOnlyList<Rating> ratings, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction();
        WriteRatings(connection, tx, itemId, topicId ?? AllTopicsSentinel, ratings, fetchedAt);
        tx.Commit();
    }

    public Task<CacheEntry<IReadOnlyList<Topic>>?> GetTopicsAsync(CancellationToken ct = default) =>
        GetTaxonomyAsync(Topics, ct);

    public Task PutTopicsAsync(IReadOnlyList<Topic> topics, DateTimeOffset fetchedAt, CancellationToken ct = default) =>
        PutTaxonomyAsync(Topics, topics, fetchedAt, ct);

    public Task<CacheEntry<IReadOnlyList<ItemType>>?> GetItemTypesAsync(CancellationToken ct = default) =>
        GetTaxonomyAsync(ItemTypes, ct);

    public Task PutItemTypesAsync(IReadOnlyList<ItemType> itemTypes, DateTimeOffset fetchedAt, CancellationToken ct = default) =>
        PutTaxonomyAsync(ItemTypes, itemTypes, fetchedAt, ct);

    public Task<CacheEntry<IReadOnlyList<TopicCategory>>?> GetTopicCategoriesAsync(CancellationToken ct = default) =>
        GetTaxonomyAsync(TopicCategories, ct);

    public Task PutTopicCategoriesAsync(IReadOnlyList<TopicCategory> topicCategories, DateTimeOffset fetchedAt, CancellationToken ct = default) =>
        PutTaxonomyAsync(TopicCategories, topicCategories, fetchedAt, ct);

    public Task<CacheEntry<IReadOnlyList<TopicSuperCategory>>?> GetTopicSuperCategoriesAsync(CancellationToken ct = default) =>
        GetTaxonomyAsync(TopicSuperCategories, ct);

    public Task PutTopicSuperCategoriesAsync(IReadOnlyList<TopicSuperCategory> topicSuperCategories, DateTimeOffset fetchedAt, CancellationToken ct = default) =>
        PutTaxonomyAsync(TopicSuperCategories, topicSuperCategories, fetchedAt, ct);

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

    private async Task<CacheEntry<IReadOnlyList<T>>?> GetTaxonomyAsync<T>(TaxonomyTable<T> table, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction(deferred: true);
        var read = ReadTaxonomy(connection, tx, table);
        tx.Commit();
        return read is { } r ? ToEntry<IReadOnlyList<T>>(r.Values, r.FetchedAt, _policy.TaxonomyMaxAge) : null;
    }

    private async Task PutTaxonomyAsync<T>(TaxonomyTable<T> table, IReadOnlyList<T> values, DateTimeOffset fetchedAt, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction();
        WriteTaxonomy(connection, tx, table, values, fetchedAt);
        tx.Commit();
    }

    /// <summary>
    /// Rewrites a schema-v1 database, which stored whole models as JSON blobs, into the relational schema. The
    /// database is snapshotted first; the rewrite then runs in one transaction, so any failure - including an
    /// exception thrown here - rolls back and leaves the database exactly as v1 left it.
    /// </summary>
    private void MigrateFromV1(SqliteConnection connection)
    {
        SnapshotBeforeMigrating(connection);

        using var tx = connection.BeginTransaction();

        // Renaming a table carries its indexes along under their old names. Drop them, or CreateAll's
        // CREATE INDEX IF NOT EXISTS would see the names taken and silently leave the new table unindexed.
        Execute(connection, tx, """
            ALTER TABLE items RENAME TO v1_items;
            ALTER TABLE ratings RENAME TO v1_ratings;
            ALTER TABLE taxonomy RENAME TO v1_taxonomy;
            DROP INDEX items_imdb;
            DROP INDEX items_tmdb;
            """);
        Execute(connection, tx, SqliteSchema.CreateAll);

        foreach (var (fetchedAt, json) in ReadRows(connection, tx, "SELECT fetched_at, json FROM v1_items;", r => (r.GetString(0), r.GetString(1))))
        {
            WriteItem(connection, tx, Deserialize(json, DtddJsonContext.Default.ItemDetail), ParseFetchedAt(fetchedAt));
        }

        var ratingSets = ReadRows(
            connection, tx, "SELECT item_id, topic_id, fetched_at, json FROM v1_ratings;", r => (r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3)));
        foreach (var (itemId, setTopicId, fetchedAt, json) in ratingSets)
        {
            WriteRatings(connection, tx, itemId, setTopicId, Deserialize(json, DtddJsonContext.Default.ListRating), ParseFetchedAt(fetchedAt));
        }

        foreach (var (kind, fetchedAt, json) in ReadRows(connection, tx, "SELECT kind, fetched_at, json FROM v1_taxonomy;", r => (r.GetString(0), r.GetString(1), r.GetString(2))))
        {
            var at = ParseFetchedAt(fetchedAt);
            if (kind == Topics.Kind)
            {
                WriteTaxonomy(connection, tx, Topics, Deserialize(json, DtddJsonContext.Default.ListTopic), at);
            }
            else if (kind == ItemTypes.Kind)
            {
                WriteTaxonomy(connection, tx, ItemTypes, Deserialize(json, DtddJsonContext.Default.ListItemType), at);
            }
            else if (kind == TopicCategories.Kind)
            {
                WriteTaxonomy(connection, tx, TopicCategories, Deserialize(json, DtddJsonContext.Default.ListTopicCategory), at);
            }
            else if (kind == TopicSuperCategories.Kind)
            {
                WriteTaxonomy(connection, tx, TopicSuperCategories, Deserialize(json, DtddJsonContext.Default.ListTopicSuperCategory), at);
            }
            else
            {
                throw new InvalidOperationException($"Schema v1 taxonomy kind '{kind}' is not recognized; the migration was rolled back.");
            }
        }

        Execute(connection, tx, "DROP TABLE v1_items; DROP TABLE v1_ratings; DROP TABLE v1_taxonomy;");
        Execute(connection, tx, "UPDATE meta SET value = $version WHERE key = 'schema_version';", ("$version", SqliteSchema.Version));
        tx.Commit();
    }

    /// <summary>
    /// Copies the whole database to a timestamped file beside it before migration destroys the v1 layout. The
    /// snapshot is a precondition, not a best effort: if it cannot be written, this throws before anything has
    /// been changed.
    /// </summary>
    private void SnapshotBeforeMigrating(SqliteConnection connection)
    {
        var file = ExecuteScalar(connection, null, "SELECT file FROM pragma_database_list WHERE name = 'main';") as string;

        // An in-memory or temporary database has no file to snapshot, and its contents do not outlive the
        // process anyway.
        if (string.IsNullOrEmpty(file))
        {
            return;
        }

        var backupPath = file + ".v1-backup-" + _time.GetUtcNow().UtcDateTime.ToString(BackupTimestampFormat, CultureInfo.InvariantCulture);

        // Never overwrite: whatever is already at that path is somebody's data. VACUUM INTO guarantees this by
        // itself - it refuses any non-empty target, atomically, where a separate existence check would leave a
        // race window. (An existing *empty* file it fills, which loses nothing.) The BackupAlreadyExists test
        // pins that SQLite behavior, so a change to it would surface as a failing test rather than a lost file.
        try
        {
            Execute(connection, null, "VACUUM INTO $path;", ("$path", backupPath));
        }
        catch (SqliteException ex)
        {
            throw SnapshotFailed(backupPath, ex.Message, ex);
        }
    }

    private static InvalidOperationException SnapshotFailed(string backupPath, string reason, Exception? inner) =>
        new($"Could not snapshot the schema v1 cache database to '{backupPath}' ({reason}), so it was not migrated " +
            $"to schema version {SqliteSchema.Version} and is unchanged.", inner);

    private static (ItemDetail Item, DateTimeOffset FetchedAt)? ReadItem(SqliteConnection connection, SqliteTransaction tx, int itemId)
    {
        var rows = ReadRows(
            connection,
            tx,
            """
            SELECT name, release_year, item_type_id, item_type_name, tmdb_id, imdb_id,
                   background_image, poster_image, overview, fetched_at
            FROM items WHERE id = $id;
            """,
            r => (Item: new ItemDetail
            {
                Id = itemId,
                Name = r.GetString(0),
                ReleaseYear = GetNullableInt32(r, 1),
                ItemTypeId = r.GetInt32(2),
                ItemTypeName = r.GetString(3),
                TmdbId = GetNullableInt32(r, 4),
                ImdbId = GetNullableString(r, 5),
                BackgroundImage = GetNullableString(r, 6),
                PosterImage = GetNullableString(r, 7),
                Overview = GetNullableString(r, 8),
            }, FetchedAt: ParseFetchedAt(r.GetString(9))),
            ("$id", itemId));

        if (rows.Count == 0)
        {
            return null;
        }

        var stats = ReadRows(
            connection,
            tx,
            """
            SELECT topic_item_id, yes_sum, no_sum, num_comments, topic_id, topic_name, item_id
            FROM topic_item_stats WHERE owner_item_id = $id ORDER BY ordinal;
            """,
            r => new TopicItemStat
            {
                TopicItemId = r.GetInt32(0),
                YesSum = r.GetInt32(1),
                NoSum = r.GetInt32(2),
                NumComments = r.GetInt32(3),
                TopicId = r.GetInt32(4),
                TopicName = r.GetString(5),
                ItemId = r.GetInt32(6),
            },
            ("$id", itemId));
        var genres = ReadRows(
            connection, tx, "SELECT genre FROM item_genres WHERE owner_item_id = $id ORDER BY ordinal;", r => r.GetString(0), ("$id", itemId));

        var (item, fetchedAt) = rows[0];
        return (item with { Genres = genres, TopicItemStats = stats }, fetchedAt);
    }

    private static void WriteItem(SqliteConnection connection, SqliteTransaction tx, ItemDetail item, DateTimeOffset fetchedAt)
    {
        // INSERT OR REPLACE rather than an upsert: listing eleven columns again in an UPDATE SET clause invites
        // one being left out, which would silently keep that member's stale value.
        Execute(
            connection,
            tx,
            """
            INSERT OR REPLACE INTO items(id, name, release_year, item_type_id, item_type_name, tmdb_id, imdb_id,
                                         background_image, poster_image, overview, fetched_at)
            VALUES ($id, $name, $releaseYear, $itemTypeId, $itemTypeName, $tmdbId, $imdbId,
                    $backgroundImage, $posterImage, $overview, $fetchedAt);
            DELETE FROM topic_item_stats WHERE owner_item_id = $id;
            DELETE FROM item_genres WHERE owner_item_id = $id;
            """,
            ("$id", item.Id),
            ("$name", item.Name),
            ("$releaseYear", item.ReleaseYear),
            ("$itemTypeId", item.ItemTypeId),
            ("$itemTypeName", item.ItemTypeName),
            ("$tmdbId", item.TmdbId),
            ("$imdbId", item.ImdbId),
            ("$backgroundImage", item.BackgroundImage),
            ("$posterImage", item.PosterImage),
            ("$overview", item.Overview),
            ("$fetchedAt", FormatFetchedAt(fetchedAt)));

        InsertRows(
            connection,
            tx,
            """
            INSERT INTO topic_item_stats(owner_item_id, ordinal, topic_item_id, yes_sum, no_sum, num_comments, topic_id, topic_name, item_id)
            VALUES ($ownerItemId, $ordinal, $topicItemId, $yesSum, $noSum, $numComments, $topicId, $topicName, $itemId);
            """,
            item.TopicItemStats.Select((s, i) => new (string, object?)[]
            {
                ("$ownerItemId", item.Id), ("$ordinal", i), ("$topicItemId", s.TopicItemId), ("$yesSum", s.YesSum),
                ("$noSum", s.NoSum), ("$numComments", s.NumComments), ("$topicId", s.TopicId),
                ("$topicName", s.TopicName), ("$itemId", s.ItemId),
            }));

        InsertRows(
            connection,
            tx,
            "INSERT INTO item_genres(owner_item_id, ordinal, genre) VALUES ($ownerItemId, $ordinal, $genre);",
            item.Genres.Select((g, i) => new (string, object?)[] { ("$ownerItemId", item.Id), ("$ordinal", i), ("$genre", g) }));
    }

    private static (List<Rating> Ratings, DateTimeOffset FetchedAt)? ReadRatings(
        SqliteConnection connection, SqliteTransaction tx, int itemId, int setTopicId)
    {
        if (ExecuteScalar(
                connection,
                tx,
                "SELECT fetched_at FROM rating_sets WHERE item_id = $itemId AND topic_id = $topicId;",
                ("$itemId", itemId),
                ("$topicId", setTopicId)) is not string fetchedAt)
        {
            return null;
        }

        var ratings = ReadRows(
            connection,
            tx,
            """
            SELECT id, yes, no, vote_sum, trigger_description, is_rampant, index1, index2,
                   position1, position2, position3, safe_position1, safe_position2, safe_position3,
                   cue_description, item_id, topic_id, is_scene_alert
            FROM ratings WHERE set_item_id = $itemId AND set_topic_id = $topicId ORDER BY ordinal;
            """,
            r => new Rating
            {
                Id = r.GetInt32(0),
                Yes = r.GetInt32(1),
                No = r.GetInt32(2),
                VoteSum = r.GetInt32(3),
                TriggerDescription = GetNullableString(r, 4),
                IsRampant = r.GetBoolean(5),
                Index1 = r.GetInt32(6),
                Index2 = r.GetInt32(7),
                Position1 = GetNullableInt32(r, 8),
                Position2 = GetNullableInt32(r, 9),
                Position3 = GetNullableInt32(r, 10),
                SafePosition1 = GetNullableInt32(r, 11),
                SafePosition2 = GetNullableInt32(r, 12),
                SafePosition3 = GetNullableInt32(r, 13),
                CueDescription = GetNullableString(r, 14),
                ItemId = r.GetInt32(15),
                TopicId = r.GetInt32(16),
                IsSceneAlert = r.GetBoolean(17),
            },
            ("$itemId", itemId),
            ("$topicId", setTopicId));

        return (ratings, ParseFetchedAt(fetchedAt));
    }

    private static void WriteRatings(
        SqliteConnection connection, SqliteTransaction tx, int itemId, int setTopicId, IReadOnlyList<Rating> ratings, DateTimeOffset fetchedAt)
    {
        Execute(
            connection,
            tx,
            """
            INSERT OR REPLACE INTO rating_sets(item_id, topic_id, fetched_at) VALUES ($itemId, $topicId, $fetchedAt);
            DELETE FROM ratings WHERE set_item_id = $itemId AND set_topic_id = $topicId;
            """,
            ("$itemId", itemId),
            ("$topicId", setTopicId),
            ("$fetchedAt", FormatFetchedAt(fetchedAt)));

        InsertRows(
            connection,
            tx,
            """
            INSERT INTO ratings(set_item_id, set_topic_id, ordinal, id, yes, no, vote_sum, trigger_description, is_rampant,
                                index1, index2, position1, position2, position3, safe_position1, safe_position2, safe_position3,
                                cue_description, item_id, topic_id, is_scene_alert)
            VALUES ($setItemId, $setTopicId, $ordinal, $id, $yes, $no, $voteSum, $triggerDescription, $isRampant,
                    $index1, $index2, $position1, $position2, $position3, $safePosition1, $safePosition2, $safePosition3,
                    $cueDescription, $itemId, $topicId, $isSceneAlert);
            """,
            ratings.Select((r, i) => new (string, object?)[]
            {
                ("$setItemId", itemId), ("$setTopicId", setTopicId), ("$ordinal", i), ("$id", r.Id), ("$yes", r.Yes),
                ("$no", r.No), ("$voteSum", r.VoteSum), ("$triggerDescription", r.TriggerDescription),
                ("$isRampant", r.IsRampant), ("$index1", r.Index1), ("$index2", r.Index2), ("$position1", r.Position1),
                ("$position2", r.Position2), ("$position3", r.Position3), ("$safePosition1", r.SafePosition1),
                ("$safePosition2", r.SafePosition2), ("$safePosition3", r.SafePosition3),
                ("$cueDescription", r.CueDescription), ("$itemId", r.ItemId), ("$topicId", r.TopicId),
                ("$isSceneAlert", r.IsSceneAlert),
            }));
    }

    private static (List<T> Values, DateTimeOffset FetchedAt)? ReadTaxonomy<T>(SqliteConnection connection, SqliteTransaction tx, TaxonomyTable<T> table)
    {
        if (ExecuteScalar(connection, tx, "SELECT fetched_at FROM taxonomy WHERE kind = $kind;", ("$kind", table.Kind)) is not string fetchedAt)
        {
            return null;
        }

        var values = ReadRows(connection, tx, $"SELECT {string.Join(", ", table.Columns)} FROM {table.Table} ORDER BY ordinal;", table.FromRow);
        return (values, ParseFetchedAt(fetchedAt));
    }

    private static void WriteTaxonomy<T>(
        SqliteConnection connection, SqliteTransaction tx, TaxonomyTable<T> table, IReadOnlyList<T> values, DateTimeOffset fetchedAt)
    {
        Execute(
            connection,
            tx,
            $"INSERT OR REPLACE INTO taxonomy(kind, fetched_at) VALUES ($kind, $fetchedAt); DELETE FROM {table.Table};",
            ("$kind", table.Kind),
            ("$fetchedAt", FormatFetchedAt(fetchedAt)));

        InsertRows(
            connection,
            tx,
            $"INSERT INTO {table.Table}(ordinal, {string.Join(", ", table.Columns)}) VALUES ($ordinal, {string.Join(", ", table.Columns.Select(c => "$" + c))});",
            values.Select((v, i) => table.ToRow(v).Select((value, c) => ("$" + table.Columns[c], value)).Prepend(("$ordinal", (object?)i)).ToArray()));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private static string? ReadSchemaVersion(SqliteConnection connection) =>
        ExecuteScalar(connection, null, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'meta';") is null
            ? null
            : ExecuteScalar(connection, null, "SELECT value FROM meta WHERE key = 'schema_version';") as string;

    private static SqliteCommand CreateCommand(
        SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, tx, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static object? ExecuteScalar(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, tx, sql, parameters);
        return command.ExecuteScalar();
    }

    private static List<T> ReadRows<T>(
        SqliteConnection connection, SqliteTransaction? tx, string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, tx, sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private static void InsertRows(SqliteConnection connection, SqliteTransaction tx, string sql, IEnumerable<(string Name, object? Value)[]> rows)
    {
        using var command = CreateCommand(connection, tx, sql);
        foreach (var row in rows)
        {
            command.Parameters.Clear();
            foreach (var (name, value) in row)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            command.ExecuteNonQuery();
        }
    }

    private static T Deserialize<T>(string json, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(json, typeInfo)
        ?? throw new InvalidOperationException($"Schema v1 row decoded to null {typeof(T).Name}; the migration was rolled back.");

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? GetNullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private CacheEntry<T> ToEntry<T>(T value, DateTimeOffset fetchedAt, TimeSpan maxAge)
    {
        var isStale = _time.GetUtcNow() - fetchedAt > maxAge;
        return new CacheEntry<T>(value, fetchedAt, isStale);
    }

    private static DateTimeOffset? GetNullableInstant(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseFetchedAt(reader.GetString(ordinal));

    private static string? FormatNullableInstant(DateTimeOffset? value) =>
        value is { } instant ? FormatFetchedAt(instant) : null;

    private static string FormatFetchedAt(DateTimeOffset fetchedAt) =>
        fetchedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseFetchedAt(string value) =>
        DateTimeOffset.ParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>
    /// How one taxonomy model maps onto its table. <see cref="Kind"/> keys its row in the <c>taxonomy</c> fetch
    /// marker; <see cref="Columns"/>, <see cref="ToRow"/> and <see cref="FromRow"/> must list members in the same
    /// order, which is also the reader's column order.
    /// </summary>
    private sealed record TaxonomyTable<T>(
        string Kind, string Table, string[] Columns, Func<T, object?[]> ToRow, Func<SqliteDataReader, T> FromRow);
}
