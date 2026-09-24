using System.Collections.Concurrent;

namespace DoesTheDogDie.Tests.Support;

/// <summary>
/// An in-memory <see cref="IBudgetStore"/> that survives the <see cref="ThrottledDtddClient"/> instances using
/// it, standing in for a database across a simulated restart. Can be told to throw, to prove store failures
/// never fail a request.
/// </summary>
internal sealed class MemoryBudgetStore : IBudgetStore
{
    private readonly ConcurrentDictionary<string, BudgetState> _states = new();

    public bool ThrowOnAccess { get; set; }

    public int Accesses => _accesses;

    private int _accesses;

    public BudgetState? Peek(string budgetId) => _states.GetValueOrDefault(budgetId);

    public BudgetState? LoadBudget(string budgetId)
    {
        Interlocked.Increment(ref _accesses);
        ThrowIfAsked();
        return _states.GetValueOrDefault(budgetId);
    }

    public void SaveBudget(string budgetId, BudgetState state)
    {
        Interlocked.Increment(ref _accesses);
        ThrowIfAsked();
        _states[budgetId] = state;
    }

    private void ThrowIfAsked()
    {
        if (ThrowOnAccess)
        {
            throw new InvalidOperationException("budget store unavailable");
        }
    }
}
