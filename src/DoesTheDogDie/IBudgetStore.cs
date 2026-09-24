namespace DoesTheDogDie;

/// <summary>
/// Durable storage for <see cref="ThrottledDtddClient"/> budget state, keyed by an opaque budget id the client
/// derives from its API key and server. <see cref="Cache.SqliteDtddCache"/> implements it.
/// </summary>
public interface IBudgetStore
{
    /// <summary>Returns the state last saved under <paramref name="budgetId"/>, or null if there is none.</summary>
    BudgetState? LoadBudget(string budgetId);

    /// <summary>Replaces the state saved under <paramref name="budgetId"/>.</summary>
    void SaveBudget(string budgetId, BudgetState state);
}
