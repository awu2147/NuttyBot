namespace NuttyBot;

internal sealed class PlayerData
{
    public const long StartingBalance = 100;

    public PlayerData(ulong userId)
    {
        UserId = userId;
        Balance = StartingBalance;
    }

    public ulong UserId { get; }
    public long Balance { get; private set; }

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
}
