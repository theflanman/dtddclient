using DoesTheDogDie.Cache;
using Microsoft.Data.Sqlite;

namespace DoesTheDogDie.Tests.Cache;

public sealed class SqliteDtddCacheTests : DtddCacheContractTests, IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "dtdd-cache-" + Guid.NewGuid().ToString("N") + ".db");

    protected override IDtddCache CreateCache(CachePolicy policy, TimeProvider time) =>
        new SqliteDtddCache($"Data Source={_path}", policy, time);

    [Fact]
    public void Open_CreatesSchemaAndVersion()
    {
        _ = CreateCache(CachePolicy.Default, TimeProvider.System);

        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();

        var expectedTables = new[] { "meta", "items", "ratings", "taxonomy", "lookups" };
        foreach (var table in expectedTables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", table);
            var result = command.ExecuteScalar();
            Assert.NotNull(result);
            Assert.Equal(table, result);
        }

        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        var version = versionCommand.ExecuteScalar();
        Assert.Equal("1", version);
    }

    [Fact]
    public async Task Reopen_KeepsData()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var item = new DoesTheDogDie.Api.ItemDetail
        {
            Id = 42,
            Name = "Halloween",
            ItemTypeId = 15,
            ItemTypeName = "Movie",
        };

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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_path + suffix);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
