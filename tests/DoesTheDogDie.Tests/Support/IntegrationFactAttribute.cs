namespace DoesTheDogDie.Tests.Support;

/// <summary>
/// Marks a test as an integration test that talks to the live DtDD API.
/// The test is skipped automatically when no <c>DTDD_API_KEY</c> is
/// available via <see cref="TestEnvironment.ApiKey"/>.
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (TestEnvironment.ApiKey is null)
        {
            Skip = "DTDD_API_KEY not set";
        }
    }
}
