using System.Text.Json;

namespace NuttyBot;

internal sealed class SlotLeaderboard
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly List<SlotLeaderboardEntry> _entries;

    private SlotLeaderboard(
        string filePath,
        List<SlotLeaderboardEntry> entries)
    {
        _filePath = filePath;
        _entries = entries;
    }

    public static SlotLeaderboard Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return new SlotLeaderboard(filePath, []);

            string json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<SlotLeaderboardFile>(
                json,
                JsonOptions);
            return new SlotLeaderboard(filePath, data?.Entries ?? []);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load the slot leaderboard: {ex.Message}");
            return new SlotLeaderboard(filePath, []);
        }
    }

    public IReadOnlyList<SlotLeaderboardEntry> GetTopThree(ulong guildId) =>
        _entries
            .Where(entry => entry.GuildId == guildId)
            .OrderBy(entry => entry.TotalSpins)
            .ThenBy(entry => entry.OrgansSold)
            .ThenBy(entry => entry.CompletedUtc)
            .Take(3)
            .ToArray();

    public void RecordPersonalBest(
        ulong guildId,
        ulong userId,
        string displayName,
        TimeSpan duration,
        DateTimeOffset completedUtc,
        int totalSpins,
        int organsSold)
    {
        SlotLeaderboardEntry? existing = _entries.FirstOrDefault(entry =>
            entry.GuildId == guildId && entry.UserId == userId);

        if (existing is not null)
        {
            bool newRecordIsBetter =
                totalSpins < existing.TotalSpins ||
                (totalSpins == existing.TotalSpins && organsSold < existing.OrgansSold);

            if (!newRecordIsBetter)
                return;

            _entries.Remove(existing);
        }

        _entries.Add(new SlotLeaderboardEntry(
            guildId,
            userId,
            displayName,
            duration.Ticks,
            completedUtc,
            totalSpins,
            organsSold));
        Save();
    }

    private void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_filePath);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temporaryPath = _filePath + ".tmp";
            string json = JsonSerializer.Serialize(
                new SlotLeaderboardFile(_entries),
                JsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save the slot leaderboard: {ex.Message}");
        }
    }

    private sealed record SlotLeaderboardFile(
        List<SlotLeaderboardEntry> Entries);
}

internal sealed record SlotLeaderboardEntry(
    ulong GuildId,
    ulong UserId,
    string DisplayName,
    long DurationTicks,
    DateTimeOffset CompletedUtc,
    int TotalSpins,
    int OrgansSold)
{
    public TimeSpan Duration => TimeSpan.FromTicks(DurationTicks);
}
