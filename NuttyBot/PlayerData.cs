namespace NuttyBot;

internal sealed class PlayerData
{
    public const long StartingBalance = 100;
    public const long OrganSaleValue = 20;
    public const long VictoryBalance = 1_000_000_000_000;

    public PlayerData(ulong userId)
    {
        UserId = userId;
        Balance = StartingBalance;
    }

    public ulong UserId { get; }
    public long Balance { get; private set; }
    public int OrgansSold { get; private set; }
    public int TotalSpins { get; private set; }
    public DateTimeOffset? FirstGambleUtc { get; private set; }
    public DateTimeOffset? CompletedUtc { get; private set; }
    public bool HasCompletedRun => CompletedUtc.HasValue;

    public TimeSpan RunDuration =>
        FirstGambleUtc.HasValue && CompletedUtc.HasValue
            ? CompletedUtc.Value - FirstGambleUtc.Value
            : TimeSpan.Zero;

    public bool CanAfford(long amount) => amount > 0 && Balance >= amount;

    public bool TrySpend(long amount)
    {
        if (!CanAfford(amount))
            return false;

        Balance -= amount;
        return true;
    }

    public void Credit(long amount)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount));

        Balance += amount;
    }

    public void SellOrgan()
    {
        checked
        {
            OrgansSold++;
            Balance += OrganSaleValue;
        }
    }

    public void RecordSpin(DateTimeOffset timestamp)
    {
        FirstGambleUtc ??= timestamp;
        TotalSpins++;
    }

    public bool TryCompleteRun(DateTimeOffset timestamp)
    {
        if (HasCompletedRun || Balance < VictoryBalance)
            return HasCompletedRun;

        CompletedUtc = timestamp;
        return true;
    }
}
