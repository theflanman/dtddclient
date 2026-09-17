namespace DoesTheDogDie;

/// <summary>
/// A value returned by <see cref="IDtddClient"/>, annotated with where it came from and when it was fetched.
/// </summary>
/// <typeparam name="T">The payload type.</typeparam>
/// <param name="Value">The payload.</param>
/// <param name="Source">Where the value came from; see <see cref="ResultSource"/>.</param>
/// <param name="FetchedAt">When the underlying data was last fetched from the DtDD API.</param>
public sealed record DtddResult<T>(T Value, ResultSource Source, DateTimeOffset FetchedAt);
