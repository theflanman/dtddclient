namespace DoesTheDogDie.Api;

/// <summary>
/// A successful DtDD API response, paired with the rate-limit snapshot observed on it.
/// </summary>
/// <typeparam name="T">The deserialized response payload type.</typeparam>
/// <param name="Value">The deserialized response payload.</param>
/// <param name="RateLimit">The rate-limit snapshot observed on the response headers.</param>
public sealed record ApiResponse<T>(T Value, RateLimitStatus RateLimit);
