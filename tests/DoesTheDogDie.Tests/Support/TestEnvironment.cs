namespace DoesTheDogDie.Tests.Support;

/// <summary>
/// Exposes test configuration such as the DtDD API key used by integration
/// tests. Never logs or prints the key's value.
/// </summary>
public static class TestEnvironment
{
    /// <summary>
    /// The DtDD API key, sourced from the <c>DTDD_API_KEY</c> process
    /// environment variable if set to a non-empty value, otherwise from a
    /// <c>.env</c> file found by walking up from the test binary's
    /// directory. <c>null</c> when neither source provides a usable value.
    /// </summary>
    public static string? ApiKey { get; } = ResolveApiKey();

    private static string? ResolveApiKey()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("DTDD_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        var envFilePath = DotEnv.FindFile(AppContext.BaseDirectory);
        if (envFilePath is null)
        {
            return null;
        }

        var values = DotEnv.Parse(File.ReadAllText(envFilePath));
        if (values.TryGetValue("DTDD_API_KEY", out var fromFile) && !string.IsNullOrWhiteSpace(fromFile))
        {
            return fromFile;
        }

        return null;
    }
}
