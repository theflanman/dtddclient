namespace DoesTheDogDie.Api.Models;

/// <summary>
/// The error body returned by the DtDD API on non-2xx responses.
/// </summary>
public sealed record ApiError
{
    public required string Error { get; init; }

    public required string Message { get; init; }
}
