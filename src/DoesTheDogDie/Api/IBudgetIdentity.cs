namespace DoesTheDogDie.Api;

/// <summary>
/// Implemented by an <see cref="IDtddApiClient"/> that can name the budget it spends - one per API key per server
/// - so <see cref="ThrottledDtddClient"/> can persist budget state under it. A separate interface rather than a
/// member of <see cref="IDtddApiClient"/>, so implementations outside this library are unaffected; one without it
/// simply does not persist, which is right for a client with no key to own a budget.
/// </summary>
internal interface IBudgetIdentity
{
    /// <summary>An opaque, stable id for the budget. Must never reveal the API key: it is written to disk.</summary>
    string BudgetId { get; }
}
