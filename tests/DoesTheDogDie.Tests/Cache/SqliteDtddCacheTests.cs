using System.Globalization;
using System.Text.Json;
using DoesTheDogDie.Api;
using DoesTheDogDie.Cache;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace DoesTheDogDie.Tests.Cache;

public sealed class SqliteDtddCacheTests : DtddCacheContractTests, IDisposable
{
    private static readonly string[] V2Tables =
    [
        "budget_state", "item_genres", "item_types", "items", "lookups", "meta", "rating_sets", "ratings",
        "taxonomy", "topic_categories", "topic_item_stats", "topic_super_categories", "topics",
    ];

    private readonly string _path = Path.Combine(Path.GetTempPath(), "dtdd-cache-" + Guid.NewGuid().ToString("N") + ".db");

    protected override IDtddCache CreateCache(CachePolicy policy, TimeProvider time) =>
        new SqliteDtddCache($"Data Source={_path}", policy, time);

    [Fact]
    public void Open_CreatesSchemaAndVersion()
    {
        _ = CreateCache(CachePolicy.Default, TimeProvider.System);

        Assert.Equal(V2Tables, TableNames());
        Assert.Equal("2", SchemaVersion(_path));
    }

    [Fact]
    public void Schema_HasNoJsonBlobColumns()
    {
        // Break: any DtDD model still stored as an opaque blob instead of real, queryable columns.
        _ = CreateCache(CachePolicy.Default, TimeProvider.System);

        foreach (var table in TableNames())
        {
            Assert.DoesNotContain("json", Query<string>($"SELECT name FROM pragma_table_info('{table}');"));
        }
    }

    [Fact]
    public async Task Item_StoredInRelationalColumns()
    {
        // Break: stats or genres not written to their own rows, so nothing can rank on them in SQL.
        var cache = CreateCache(CachePolicy.Default, TimeProvider.System);

        await cache.PutItemAsync(MakeFullItem(), DateTimeOffset.UnixEpoch);

        Assert.Equal(["A boy and his dog on the Texas frontier."], Query<string>("SELECT overview FROM items WHERE id = 10752;"));
        Assert.Equal([120L, 2L, 9L], Query<long>("SELECT yes_sum FROM topic_item_stats WHERE owner_item_id = 10752 ORDER BY ordinal;"));
        Assert.Equal(["Western", "Drama", "Family"], Query<string>("SELECT genre FROM item_genres WHERE owner_item_id = 10752 ORDER BY ordinal;"));
    }

    [Fact]
    public async Task Ratings_StoredOneRowPerRating()
    {
        // Break: a rating set still stored as one row per fetch rather than one row per rating.
        var cache = CreateCache(CachePolicy.Default, TimeProvider.System);

        await cache.PutRatingsAsync(10752, null, [MakeFullRating(900), MakeSparseRating(100)], DateTimeOffset.UnixEpoch);

        Assert.Equal([900L, 100L], Query<long>("SELECT id FROM ratings WHERE set_item_id = 10752 AND set_topic_id = -1 ORDER BY ordinal;"));
    }

    [Fact]
    public async Task Taxonomy_StoredInRelationalColumns()
    {
        // Break: a taxonomy list still stored as a blob rather than one row per entry.
        var cache = CreateCache(CachePolicy.Default, TimeProvider.System);

        await cache.PutTopicsAsync(AllTopics, DateTimeOffset.UnixEpoch);

        Assert.Equal(["a dog dies", "a cat dies"], Query<string>("SELECT name FROM topics ORDER BY ordinal;"));
    }

    [Fact]
    public async Task Reopen_KeepsData()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var item = MakeFullItem();

        var first = new SqliteDtddCache($"Data Source={_path}", CachePolicy.Default, time);
        await first.PutItemAsync(item, time.GetUtcNow());

        var second = new SqliteDtddCache($"Data Source={_path}", CachePolicy.Default, time);
        var entry = await second.GetItemAsync(item.Id);

        Assert.NotNull(entry);
        AssertItemEquivalent(item, entry!.Value);
    }

    [Fact]
    public void Open_WrongSchemaVersion_Throws()
    {
        using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL); " +
                "INSERT INTO meta(key, value) VALUES ('schema_version', '999');";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidOperationException>(() => new SqliteDtddCache($"Data Source={_path}"));
    }

    [Fact]
    public async Task Open_V1Database_MigratesEveryEntity()
    {
        // Break: any field, row, empty-set marker or fetched_at lost while reprojecting the v1 blobs. Each
        // entity gets its own fetched_at, so a migration that re-stamps "now" is caught too.
        //
        // Read-backs are asserted before the schema version on purpose: against v1 code they pass, which proves
        // the frozen SqliteV1Database fixture really is the v1 format, and only the version assertion fails.
        var at = new[]
        {
            DateTimeOffset.Parse("2026-03-01T10:00:00Z"), DateTimeOffset.Parse("2026-03-02T11:00:00Z"),
            DateTimeOffset.Parse("2026-03-03T12:00:00Z"), DateTimeOffset.Parse("2026-03-04T13:00:00Z"),
            DateTimeOffset.Parse("2026-03-05T14:00:00Z"), DateTimeOffset.Parse("2026-03-06T15:00:00Z"),
            DateTimeOffset.Parse("2026-03-07T16:00:00Z"), DateTimeOffset.Parse("2026-03-08T17:00:00Z"),
            DateTimeOffset.Parse("2026-03-09T18:00:00Z"), DateTimeOffset.Parse("2026-03-10T19:00:00Z"),
        };
        Rating[] allTopicRatings = [MakeFullRating(900), MakeSparseRating(100)];
        using (var v1 = new SqliteV1Database(_path))
        {
            v1.PutItem(MakeFullItem(), at[0]);
            v1.PutItem(MakeSparseItem(), at[1]);
            v1.PutRatings(10752, null, allTopicRatings, at[2]);
            v1.PutRatings(10752, 153, [], at[3]);
            v1.PutTaxonomy("topics", AllTopics, DtddJsonContext.Default.ListTopic, at[4]);
            v1.PutTaxonomy("itemtypes", AllItemTypes, DtddJsonContext.Default.ListItemType, at[5]);
            v1.PutTaxonomy("topiccategories", [], DtddJsonContext.Default.ListTopicCategory, at[6]);
            v1.PutTaxonomy("topicsupercategories", AllTopicSuperCategories, DtddJsonContext.Default.ListTopicSuperCategory, at[7]);
            v1.PutLookup(LookupKey.Imdb("tt0050798"), 10752, at[8]);
            v1.PutLookup(LookupKey.Imdb("tt9999999"), null, at[9]);
        }

        var cache = new SqliteDtddCache($"Data Source={_path}", CachePolicy.Default, new FakeTimeProvider(at[9]));

        var full = await cache.GetItemAsync(10752);
        var sparse = await cache.GetItemAsync(20001);
        var ratings = await cache.GetRatingsAsync(10752, null);
        var emptyRatings = await cache.GetRatingsAsync(10752, 153);
        var topics = await cache.GetTopicsAsync();
        var itemTypes = await cache.GetItemTypesAsync();
        var categories = await cache.GetTopicCategoriesAsync();
        var superCategories = await cache.GetTopicSuperCategoriesAsync();
        var hit = await cache.GetLookupAsync(LookupKey.Imdb("tt0050798"));
        var miss = await cache.GetLookupAsync(LookupKey.Imdb("tt9999999"));

        AssertItemEquivalent(MakeFullItem(), full!.Value);
        AssertItemEquivalent(MakeSparseItem(), sparse!.Value);
        Assert.Equal(allTopicRatings, ratings!.Value);
        Assert.Empty(emptyRatings!.Value);
        Assert.Equal(AllTopics, topics!.Value);
        Assert.Equal(AllItemTypes, itemTypes!.Value);
        Assert.Empty(categories!.Value);
        Assert.Equal(AllTopicSuperCategories, superCategories!.Value);
        Assert.Equal(10752, hit!.Value);
        Assert.Null(miss!.Value);
        Assert.Equal(
            at,
            new[]
            {
                full.FetchedAt, sparse.FetchedAt, ratings.FetchedAt, emptyRatings.FetchedAt, topics.FetchedAt,
                itemTypes.FetchedAt, categories.FetchedAt, superCategories.FetchedAt, hit.FetchedAt, miss.FetchedAt,
            });

        Assert.Equal("2", SchemaVersion(_path));
        Assert.Equal(V2Tables, TableNames());

        // Break: renaming v1 `items` carries its indexes along under their old names, so a v2
        // CREATE INDEX IF NOT EXISTS silently skips them and dropping the v1 table leaves v2 unindexed.
        Assert.Equal(
            ["items_imdb", "items_tmdb"],
            Query<string>("SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'items' AND name NOT LIKE 'sqlite_%' ORDER BY name;"));
    }

    [Fact]
    public void Open_V1Database_WritesBackupFirst()
    {
        // Break: migrating without a restorable snapshot of the v1 data, or snapshotting something other than
        // the complete v1 database (e.g. an empty file, or the database after the rewrite began).
        var now = DateTimeOffset.Parse("2026-09-23T20:49:10.123Z");
        using (var v1 = new SqliteV1Database(_path))
        {
            v1.PutItem(MakeFullItem(), now);
        }

        _ = new SqliteDtddCache($"Data Source={_path}", CachePolicy.Default, new FakeTimeProvider(now));

        var backup = Assert.Single(BackupFiles());
        Assert.Equal("1", SchemaVersion(backup));
        var json = Query<string>("SELECT json FROM items WHERE id = 10752;", backup);
        AssertItemEquivalent(MakeFullItem(), JsonSerializer.Deserialize(Assert.Single(json), DtddJsonContext.Default.ItemDetail)!);
    }

    [Fact]
    public void Open_V1Database_BackupAlreadyExists_ThrowsAndLeavesV1Untouched()
    {
        // Breaks: overwriting a file that was already there; or treating a failed snapshot as optional and
        // migrating anyway, destroying the only copy of the v1 data. The snapshot path is derived from the
        // clock, so colliding with it is how this test makes the snapshot fail deterministically.
        var now = DateTimeOffset.Parse("2026-09-23T20:49:10.123Z");
        using (var v1 = new SqliteV1Database(_path))
        {
            v1.PutItem(MakeFullItem(), now);
        }

        var backupPath = _path + ".v1-backup-" + now.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        byte[] sentinel = [0xDE, 0xAD, 0xBE, 0xEF];
        File.WriteAllBytes(backupPath, sentinel);

        Assert.Throws<InvalidOperationException>(
            () => new SqliteDtddCache($"Data Source={_path}", CachePolicy.Default, new FakeTimeProvider(now)));

        Assert.Equal(sentinel, File.ReadAllBytes(backupPath));
        Assert.Equal("1", SchemaVersion(_path));
        Assert.Single(Query<string>("SELECT json FROM items WHERE id = 10752;"));
    }

    [Fact]
    public void Open_V1Database_MigrationFails_RollsBackToV1()
    {
        // Break: rewriting outside one transaction, so a failure part-way leaves a half-migrated database - v1
        // tables renamed or dropped, v2 tables half filled - that neither version can open. The unknown kind is
        // unreachable from v1 code; taxonomy is migrated last, so this fails after items are already rewritten.
        var now = DateTimeOffset.Parse("2026-09-23T20:49:10.123Z");
        using (var v1 = new SqliteV1Database(_path))
        {
            v1.PutItem(MakeFullItem(), now);
            v1.PutTaxonomy("not-a-kind", AllTopics, DtddJsonContext.Default.ListTopic, now);
        }

        Assert.Throws<InvalidOperationException>(
            () => new SqliteDtddCache($"Data Source={_path}", CachePolicy.Default, new FakeTimeProvider(now)));

        Assert.Equal("1", SchemaVersion(_path));
        Assert.Equal(["items", "lookups", "meta", "ratings", "taxonomy"], TableNames());
        Assert.Single(Query<string>("SELECT json FROM items WHERE id = 10752;"));
    }

    [Fact]
    public void BudgetStore_RoundTrip_IsExact()
    {
        // Breaks: a budget column dropped or mis-mapped (full); NULL read back as 0 (headerless); a budget that
        // was never observed confused with one observed with every header missing (unobserved vs headerless).
        var cache = (SqliteDtddCache)CreateCache(CachePolicy.Default, TimeProvider.System);
        var full = new BudgetState(
            new RateLimitStatus(30, 12, 5000, 4321, DateTimeOffset.Parse("2026-09-16T12:00:01.5Z")),
            MonthEndsAt: DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
            ExhaustedUntil: DateTimeOffset.Parse("2026-10-02T00:00:00Z"),
            BackgroundHeldUntil: DateTimeOffset.Parse("2026-10-03T00:00:00Z"));
        var headerless = new BudgetState(new RateLimitStatus(null, null, null, null, DateTimeOffset.Parse("2026-09-17T08:30:00Z")), null, null, null);
        var unobserved = new BudgetState(null, null, null, null);

        cache.SaveBudget("full", full);
        cache.SaveBudget("headerless", headerless);
        cache.SaveBudget("unobserved", unobserved);

        Assert.Equal(full, cache.LoadBudget("full"));
        Assert.Equal(headerless, cache.LoadBudget("headerless"));
        Assert.Equal(unobserved, cache.LoadBudget("unobserved"));
    }

    [Fact]
    public void BudgetStore_ReplacesAndIsKeyedById()
    {
        // Breaks: a save appending instead of replacing; ids sharing a row; a missing id reading as a default
        // state rather than null.
        var cache = (SqliteDtddCache)CreateCache(CachePolicy.Default, TimeProvider.System);
        var first = new BudgetState(null, null, DateTimeOffset.Parse("2026-10-01T00:00:00Z"), null);
        var second = new BudgetState(null, null, null, DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
        var other = new BudgetState(null, DateTimeOffset.Parse("2026-11-01T00:00:00Z"), null, null);

        cache.SaveBudget("a", first);
        cache.SaveBudget("b", other);
        cache.SaveBudget("a", second);

        Assert.Equal(second, cache.LoadBudget("a"));
        Assert.Equal(other, cache.LoadBudget("b"));
        Assert.Null(cache.LoadBudget("never-saved"));
    }

    [Fact]
    public void Open_V2DatabaseWithoutBudgetTable_AddsIt()
    {
        // Break: a database already at schema v2 from before budget_state existed - exactly what the previous
        // release leaves on disk - never gains the table, so every save fails and persistence silently never works.
        _ = CreateCache(CachePolicy.Default, TimeProvider.System);
        using (var connection = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE budget_state;";
            command.ExecuteNonQuery();
        }

        var reopened = new SqliteDtddCache($"Data Source={_path}");
        var state = new BudgetState(null, null, DateTimeOffset.Parse("2026-10-01T00:00:00Z"), null);
        reopened.SaveBudget("a", state);

        Assert.Equal(state, reopened.LoadBudget("a"));
        Assert.Equal("2", SchemaVersion(_path));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var file in BackupFiles().Append(_path).Append(_path + "-wal").Append(_path + "-shm"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string? SchemaVersion(string path) =>
        Query<string>("SELECT value FROM meta WHERE key = 'schema_version';", path).SingleOrDefault();

    private string[] BackupFiles() =>
        Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".v1-backup-*");

    private string[] TableNames() =>
        [.. Query<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;")];

    private List<T> Query<T>(string sql) => Query<T>(sql, _path);

    private static List<T> Query<T>(string sql, string path)
    {
        // Pooling=False: a pooled handle would keep the file open, and these reads are one-off inspections.
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<T>();
        while (reader.Read())
        {
            values.Add(reader.GetFieldValue<T>(0));
        }

        return values;
    }
}
