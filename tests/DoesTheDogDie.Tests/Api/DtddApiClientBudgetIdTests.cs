using DoesTheDogDie.Api;

namespace DoesTheDogDie.Tests.Api;

public class DtddApiClientBudgetIdTests
{
    private static string IdOf(string apiKey, string baseAddress = "https://www.doesthedogdie.com/api/v3/") =>
        ((IBudgetIdentity)new DtddApiClient(new HttpClient(), new DtddApiOptions { ApiKey = apiKey, BaseAddress = new Uri(baseAddress) })).BudgetId;

    [Fact]
    public void BudgetId_SameKeyAndServer_IsStable()
    {
        // Break: an id that varies between client instances (e.g. random, or including the instance), so no
        // restart could ever find its own state. The trailing slash is normalized by the constructor.
        Assert.Equal(IdOf("ddd_one"), IdOf("ddd_one", "https://www.doesthedogdie.com/api/v3"));
    }

    [Fact]
    public void BudgetId_DifferentKey_Differs()
    {
        // Break: an id ignoring the key, so every key shares one budget's state.
        Assert.NotEqual(IdOf("ddd_one"), IdOf("ddd_two"));
    }

    [Fact]
    public void BudgetId_DifferentServer_Differs()
    {
        // Break: an id ignoring the server, so a mock API's exhaustion (DTDD_API_BASE_URL in end-to-end tests)
        // would block the real one under the same key.
        Assert.NotEqual(IdOf("ddd_one"), IdOf("ddd_one", "http://localhost:5055/api/v3/"));
    }

    [Fact]
    public void BudgetId_DoesNotRevealKey()
    {
        // Break: the raw key written into the id, and so onto disk in the budget store.
        Assert.DoesNotContain("ddd_supersecret", IdOf("ddd_supersecret"), StringComparison.Ordinal);
    }
}
