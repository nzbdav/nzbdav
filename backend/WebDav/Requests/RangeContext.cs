namespace NzbWebDAV.WebDav.Requests;

/// <summary>
/// Carries an optional byte budget for the current ranged read on the async
/// call context so stream constructors can cap segment prefetch.
/// </summary>
public static class RangeContext
{
    private static readonly AsyncLocal<long?> CurrentReadBudget = new();

    public static void SetReadBudget(long? readBudgetBytes) =>
        CurrentReadBudget.Value = readBudgetBytes;

    public static long? GetReadBudget() => CurrentReadBudget.Value;
}
