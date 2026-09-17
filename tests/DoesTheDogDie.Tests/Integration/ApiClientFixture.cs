using DoesTheDogDie.Api;
using DoesTheDogDie.Tests.Support;

namespace DoesTheDogDie.Tests.Integration;

/// <summary>
/// Owns a single <see cref="HttpClient"/> and <see cref="DtddApiClient"/> shared across the
/// integration tests in this class, so a full test run only opens one connection.
/// Construction must not throw when <see cref="TestEnvironment.ApiKey"/> is <c>null</c>, because
/// xUnit constructs class fixtures even when every test that uses them is skipped.
/// </summary>
public sealed class ApiClientFixture : IDisposable
{
    private readonly HttpClient? _httpClient;

    public ApiClientFixture()
    {
        var apiKey = TestEnvironment.ApiKey;
        if (apiKey is null)
        {
            Client = null;
            return;
        }

        _httpClient = new HttpClient();
        Client = new DtddApiClient(_httpClient, new DtddApiOptions { ApiKey = apiKey });
    }

    /// <summary>
    /// The shared client, or <c>null</c> when no API key was available. Only ever dereferenced
    /// inside <c>[IntegrationFact]</c> tests, which are skipped in that case.
    /// </summary>
    public DtddApiClient? Client { get; }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}
