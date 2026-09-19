using Discord;
using Discord.WebSocket;

namespace NuttyBot;

internal sealed class DailySlotsGame
{
    private const int MaxSelectedBuffs = 5;

    // Costs/effects are intentionally easy to tune while we prototype the lobby.
    private static readonly DailyBuffDefinition[] BuffDefinitions =
    [
        new(
            "row-left",
            "Shift Row Left",
            "⬅️",
            100,
            2,
            "Shift the selected row left by one. The leftmost symbol wraps to the right."),
        new(
            "row-right",
            "Shift Row Right",
            "➡️",
            100,
            2,
            "Shift the selected row right by one. The rightmost symbol wraps to the left."),
        new(
            "column-up",
            "Shift Column Up",
            "⬆️",
            100,
            2,
            "Shift the selected column up by one. The top symbol wraps to the bottom."),
        new(
            "column-down",
            "Shift Column Down",
            "⬇️",
            100,
            2,
            "Shift the selected column down by one. The bottom symbol wraps to the top."),
        new(
            "reroll",
            "Reroll Cell",
            "🎲",
            150,
            3,
            "Reroll one selected cell on the current board."),
        new(
            "lucky-spin",
            "Lucky Spin",
            "🍀",
            300,
            2,
            "Your next spin is guaranteed to contain at least one new solution hit."),
        new(
            "wild",
            "Wild Cell",
            "🃏",
            500,
            1,
            "Turn one selected cell into a Wild for solution matching and 5-in-a-row payouts."),
        new(
            "double-payout",
            "Double Payout",
            "💵",
            650,
            1,
            "Double the total money earned from your next spin."),
        new(
            "jackpot-boost",
            "Jackpot Boost",
            "💰",
            1_000,
            1,
            "Greatly increase the payout of the next 5-in-a-row you create."),
        new(
            "perfect-spin",
            "Perfect Spin",
            "🌟",
            10_000,
            1,
            "Your next spin exactly matches the daily solution board.")
    ];

    private readonly object _syncRoot = new();
    private readonly Dictionary<(ulong GuildId, ulong UserId), BuffSelectionSession> _sessions = [];

    public static SlashCommandBuilder CreateCommand() => new SlashCommandBuilder()
        .WithName("slotsdaily")
        .WithDescription("Play today's Daily Slots board");

    public async Task HandleSlashCommandAsync(SocketSlashCommand command)
    {
        if (!command.GuildId.HasValue)
        {
            await command.RespondAsync(
                "Daily Slots can only be played inside a server.",
                ephemeral: true);
            return;
        }

        BuffSelectionSession session;

        lock (_syncRoot)
        {
            session = new BuffSelectionSession(
                command.GuildId.Value,
                command.User.Id,
                Guid.NewGuid().ToString("N"));

            _sessions[(session.GuildId, session.UserId)] = session;
        }

        await command.RespondAsync(
            components: BuildBuffSelectionView(session),
            flags: MessageFlags.ComponentsV2);
    }

    public async Task HandleButtonAsync(SocketMessageComponent component)
    {
        if (!component.GuildId.HasValue ||
            !component.Data.CustomId.StartsWith("slotsdaily:", StringComparison.Ordinal))
        {
            return;
        }

        string[] parts = component.Data.CustomId.Split(':');

        if (parts.Length < 4 ||
            !ulong.TryParse(parts[2], out ulong ownerUserId))
        {
            await component.DeferAsync();
            return;
        }

        if (component.User.Id != ownerUserId)
        {
            await component.RespondAsync(
                "This Daily Slots loadout belongs to another player.",
                ephemeral: true);
            return;
        }

        ulong guildId = component.GuildId.Value;
        string sessionId = parts[3];

        if (parts[1] == "buff" && parts.Length == 5)
        {
            await HandleBuffSelectionAsync(
                component,
                guildId,
                ownerUserId,
                sessionId,
                parts[4]);
            return;
        }

        if (parts[1] == "start")
        {
            // Phase 2: this will transition into the actual 5x5 Daily Slots board.
            // For now we simply acknowledge the interaction so Discord does not
            // show "This interaction failed" when the enabled button is pressed.
            await component.DeferAsync();
            return;
        }

        await component.DeferAsync();
    }

    private async Task HandleBuffSelectionAsync(
        SocketMessageComponent component,
        ulong guildId,
        ulong userId,
        string sessionId,
        string buffId)
    {
        MessageComponent? view = null;
        bool selectionLimitReached = false;
        bool invalidSession = false;

        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue((guildId, userId), out BuffSelectionSession? session) ||
                session.SessionId != sessionId)
            {
                invalidSession = true;
            }
            else
            {
                DailyBuffDefinition? buff = BuffDefinitions
                    .FirstOrDefault(x => x.Id == buffId);

                if (buff is null)
                    return;

                if (session.SelectedBuffIds.Contains(buffId))
                {
                    session.SelectedBuffIds.Remove(buffId);
                    view = BuildBuffSelectionView(session);
                }
                else if (session.SelectedBuffIds.Count < MaxSelectedBuffs)
                {
                    session.SelectedBuffIds.Add(buffId);
                    view = BuildBuffSelectionView(session);
                }
                else
                {
                    selectionLimitReached = true;
                }
            }
        }

        if (invalidSession)
        {
            await component.RespondAsync(
                "This Daily Slots selection is no longer active. Run /slotsdaily to open a new one.",
                ephemeral: true);
            return;
        }

        // At 5/5, clicking a sixth grey buff intentionally changes nothing.
        if (selectionLimitReached || view is null)
        {
            await component.DeferAsync();
            return;
        }

        await component.UpdateAsync(message =>
        {
            message.Components = view;
        });
    }

    private static MessageComponent BuildBuffSelectionView(BuffSelectionSession session)
    {
        int selectedCount = session.SelectedBuffIds.Count;

        var container = new ContainerBuilder()
            .WithAccentColor(new Color(241, 196, 15))
            .WithTextDisplay("## 🎰 Daily Slots")
            .WithTextDisplay($"**Select buffs to begin ({selectedCount}/{MaxSelectedBuffs})**");

        for (int row = 0; row < 2; row++)
        {
            var actionRow = new ActionRowBuilder();

            for (int column = 0; column < 5; column++)
            {
                DailyBuffDefinition buff = BuffDefinitions[(row * 5) + column];
                bool selected = session.SelectedBuffIds.Contains(buff.Id);

                var button = new ButtonBuilder()
                    .WithCustomId(
                        $"slotsdaily:buff:{session.UserId}:{session.SessionId}:{buff.Id}")
                    .WithStyle(selected ? ButtonStyle.Success : ButtonStyle.Secondary)
                    .WithEmote(new Emoji(buff.Emoji));

                actionRow.WithButton(button);
            }

            container.WithActionRow(actionRow);
        }

        DailyBuffDefinition[] selectedBuffs = BuffDefinitions
            .Where(buff => session.SelectedBuffIds.Contains(buff.Id))
            .OrderBy(buff => buff.Cost)
            .ThenBy(buff => Array.IndexOf(BuffDefinitions, buff))
            .ToArray();

        if (selectedBuffs.Length > 0)
        {
            string descriptions = string.Join(
                "\n",
                selectedBuffs.Select(buff =>
                    $"{buff.Emoji} **{buff.Name}** — " +
                    $"Cost: ${buff.Cost:N0} • " +
                    $"Use Count: {buff.UseCount} • " +
                    $"Effect: {buff.Effect}"));

            container.WithTextDisplay(descriptions);
        }

        bool canStart = selectedCount == MaxSelectedBuffs;

        container.WithActionRow(
            new ActionRowBuilder()
                .WithButton(
                    "Start Game",
                    $"slotsdaily:start:{session.UserId}:{session.SessionId}",
                    canStart ? ButtonStyle.Success : ButtonStyle.Secondary,
                    emote: new Emoji("🎰"),
                    disabled: !canStart));

        return new ComponentBuilderV2()
            .WithContainer(container)
            .Build();
    }

    private sealed class BuffSelectionSession(
        ulong guildId,
        ulong userId,
        string sessionId)
    {
        public ulong GuildId { get; } = guildId;
        public ulong UserId { get; } = userId;
        public string SessionId { get; } = sessionId;
        public HashSet<string> SelectedBuffIds { get; } = [];
    }

    private sealed record DailyBuffDefinition(
        string Id,
        string Name,
        string Emoji,
        long Cost,
        int UseCount,
        string Effect);
}
