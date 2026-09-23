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
    private readonly object _syncRoot = new();
    private readonly object _saveRoot = new();

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

    public IReadOnlyList<SlotLeaderboardEntry> GetTopEntries(ulong guildId, int maxEntries)
    {
        if (maxEntries <= 0)
            return Array.Empty<SlotLeaderboardEntry>();

        lock (_syncRoot)
        {
            var top = new SlotLeaderboardEntry?[maxEntries];

            for (int i = 0; i < _entries.Count; i++)
            {
                SlotLeaderboardEntry candidate = _entries[i];

                if (candidate.GuildId != guildId)
                    continue;

                for (int position = 0; position < top.Length; position++)
                {
                    if (top[position] is not null &&
                        CompareEntries(candidate, top[position]!) >= 0)
                    {
                        continue;
                    }

                    for (int shift = top.Length - 1; shift > position; shift--)
                        top[shift] = top[shift - 1];

                    top[position] = candidate;
                    break;
                }
            }

            int count = 0;

            while (count < top.Length && top[count] is not null)
                count++;

            var result = new SlotLeaderboardEntry[count];

            for (int i = 0; i < count; i++)
                result[i] = top[i]!;

            return result;
        }
    }

    public bool RecordPersonalBest(
        ulong guildId,
        ulong userId,
        string displayName,
        TimeSpan duration,
        DateTimeOffset completedUtc,
        int totalSpins,
        int organsSold)
    {
        lock (_syncRoot)
        {
            int existingIndex = -1;

            for (int i = 0; i < _entries.Count; i++)
            {
                SlotLeaderboardEntry entry = _entries[i];

                if (entry.GuildId == guildId && entry.UserId == userId)
                {
                    existingIndex = i;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                SlotLeaderboardEntry existing = _entries[existingIndex];
                bool newRecordIsBetter =
                    totalSpins < existing.TotalSpins ||
                    (totalSpins == existing.TotalSpins && organsSold < existing.OrgansSold);

                if (!newRecordIsBetter)
                    return false;

                _entries.RemoveAt(existingIndex);
            }

            _entries.Add(new SlotLeaderboardEntry(
                guildId,
                userId,
                displayName,
                duration.Ticks,
                completedUtc,
                totalSpins,
                organsSold));
            return true;
        }
    }

    public void Save()
    {
        lock (_saveRoot)
        {
            List<SlotLeaderboardEntry> snapshot;

            lock (_syncRoot)
                snapshot = new List<SlotLeaderboardEntry>(_entries);

            try
            {
                string? directory = Path.GetDirectoryName(_filePath);

                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                string temporaryPath = _filePath + ".tmp";
                string json = JsonSerializer.Serialize(
                    new SlotLeaderboardFile(snapshot),
                    JsonOptions);
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save the slot leaderboard: {ex.Message}");
            }
        }
    }

    private static int CompareEntries(
        SlotLeaderboardEntry left,
        SlotLeaderboardEntry right)
    {
        int comparison = left.TotalSpins.CompareTo(right.TotalSpins);

        if (comparison != 0)
            return comparison;

        comparison = left.OrgansSold.CompareTo(right.OrgansSold);

        if (comparison != 0)
            return comparison;

        return left.CompletedUtc.CompareTo(right.CompletedUtc);
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
