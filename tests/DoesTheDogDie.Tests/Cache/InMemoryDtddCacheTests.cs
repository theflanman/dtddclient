using DoesTheDogDie.Cache;

namespace DoesTheDogDie.Tests.Cache;

public class InMemoryDtddCacheTests : DtddCacheContractTests
{
    protected override IDtddCache CreateCache(CachePolicy policy, TimeProvider time) => new InMemoryDtddCache(policy, time);
}
