namespace NuttyBot;

internal sealed class PlayerData
{
    public const int StartingBalance = 100;

    public PlayerData(ulong userId)
    {
        UserId = userId;
        Balance = StartingBalance;
    }

    public ulong UserId { get; }
    public int Balance { get; private set; }

    public bool CanAfford(int amount) => amount > 0 && Balance >= amount;

    public bool TrySpend(int amount)
    {
        if (!CanAfford(amount))
            return false;

        Balance -= amount;
        return true;
    }

    public void Credit(int amount)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount));

        Balance += amount;
    }
}
