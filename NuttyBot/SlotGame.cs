using Discord;
using Discord.WebSocket;

namespace NuttyBot;

internal sealed class SlotGame : IDisposable
{
    private const int MachineCount = 5;
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(5);
    private static readonly string[] Symbols = ["🍒", "🍋", "🍊", "🍇", "⭐"];

    private readonly object _syncRoot = new();
    private readonly Dictionary<ulong, List<SlotMachine>> _machinesByGuild = [];
    private readonly Dictionary<(ulong GuildId, ulong UserId), string> _activeLobbies = [];
    private Timer? _cleanupTimer;

    public static SlashCommandBuilder CreateCommand() =>
        new SlashCommandBuilder()
            .WithName("rollslots")
            .WithDescription("Open the slot machine lobby");

    public void Start()
    {
        _cleanupTimer ??= new Timer(
            _ =>
            {
                lock (_syncRoot)
                {
                    ExpireSessions();
                }
            },
            null,
            CleanupInterval,
            CleanupInterval);
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _cleanupTimer = null;
    }

    public async Task HandleSlashCommandAsync(SocketSlashCommand command)
    {
        if (!command.GuildId.HasValue)
        {
            await command.RespondAsync(
                "Slots can only be played inside a server.",
                ephemeral: true);
            return;
        }

        string lobbyId;
        MessageComponent components;
        ulong guildId = command.GuildId.Value;
        ulong userId = command.User.Id;

        lock (_syncRoot)
        {
            ExpireSessions();
            ReleaseUserMachine(guildId, userId);
            lobbyId = CreateLobby(guildId, userId);
            components = BuildLobbyButtons(guildId, userId, lobbyId);
        }

        await command.RespondAsync( BuildLobbyContent(), components: components);
    }

    public async Task HandleButtonAsync(SocketMessageComponent component)
    {
        if (!component.Data.CustomId.StartsWith("slots:", StringComparison.Ordinal) ||
        !component.GuildId.HasValue)
        {
            return;
        }

        string[] parts = component.Data.CustomId.Split(':');

        if (parts.Length < 2)
            return;

        switch (parts[1])
        {
            case "claim":
                await HandleClaimAsync(component, parts);
                break;

            case "roll":
                await HandleRollAsync(component, parts);
                break;

            case "leave":
                await HandleLeaveAsync(component, parts);
                break;

            case "lobby":
                await HandleReturnToLobbyAsync(component, parts);
                break;

            case "refresh":
                await HandleLobbyRefreshAsync(component, parts);
                break;
        }
    }

    private async Task HandleClaimAsync(SocketMessageComponent component, string[] parts)
    {
        if (parts.Length != 5 ||
            !int.TryParse(parts[2], out int machineNumber) ||
            !ulong.TryParse(parts[3], out ulong lobbyUserId))
        {
            return;
        }

        if (component.User.Id != lobbyUserId)
        {
            await component.RespondAsync("This isn't your slot lobby!", ephemeral: true);
            return;
        }

        ulong guildId = component.GuildId!.Value;
        string lobbyId = parts[4];
        string? content = null;
        MessageComponent? components = null;
        bool oldLobby = false;

        lock (_syncRoot)
        {
            ExpireSessions();

            if (!IsCurrentLobby(guildId, component.User.Id, lobbyId))
            {
                oldLobby = true;
            }
            else if (!TryClaimMachine(guildId, component.User.Id, machineNumber, out SlotMachine? machine))
            {
                content = BuildLobbyContent();
                components = BuildLobbyButtons(guildId, component.User.Id, lobbyId);
            }
            else
            {
                CloseLobby(guildId, component.User.Id);
                content = Roll(machine!);
                components = BuildMachineButtons(machine!);
            }
        }

        if (oldLobby)
        {
            await component.RespondAsync(
                "That slot lobby is no longer active. Use `/rollslots` again.",
                ephemeral: true);
            return;
        }

        await UpdateMessageAsync(component, content!, components!);
    }

    private async Task HandleRollAsync(SocketMessageComponent component, string[] parts)
    {
        if (!TryParseMachineSession(parts, out int machineNumber, out string sessionId))
            return;

        string? content = null;
        MessageComponent? components = null;
        bool invalidSession;

        lock (_syncRoot)
        {
            ExpireSessions();
            SlotMachine? machine = FindOwnedMachine(
                component.GuildId!.Value,
                component.User.Id,
                machineNumber,
                sessionId);

            invalidSession = machine is null;

            if (machine is not null)
            {
                content = Roll(machine);
                components = BuildMachineButtons(machine);
            }
        }

        if (invalidSession)
        {
            await RespondSessionExpiredAsync(component);
            return;
        }

        await UpdateMessageAsync(component, content!, components!);
    }

    private async Task HandleLeaveAsync(SocketMessageComponent component, string[] parts)
    {
        if (!TryParseMachineSession(parts, out int machineNumber, out string sessionId))
            return;

        string? content = null;
        bool invalidSession;

        lock (_syncRoot)
        {
            ExpireSessions();
            SlotMachine? machine = FindOwnedMachine(
                component.GuildId!.Value,
                component.User.Id,
                machineNumber,
                sessionId);

            invalidSession = machine is null;

            if (machine is not null)
            {
                content =
                    $"🎰 **Slot Machine #{machine.Number}** is now free.\n\n" +
                    $"**Machine Stats:** {machine.TotalRolls} rolls • {machine.TotalWins} wins";
                ReleaseMachine(machine);
            }
        }

        if (invalidSession)
        {
            await RespondSessionExpiredAsync(component);
            return;
        }

        await UpdateMessageAsync(component, content!, new ComponentBuilder().Build());
    }

    private async Task HandleReturnToLobbyAsync(SocketMessageComponent component, string[] parts)
    {
        if (!TryParseMachineSession(parts, out int machineNumber, out string sessionId))
            return;

        ulong guildId = component.GuildId!.Value;
        ulong userId = component.User.Id;
        MessageComponent? components = null;
        bool invalidSession;

        lock (_syncRoot)
        {
            ExpireSessions();
            SlotMachine? machine = FindOwnedMachine(guildId, userId, machineNumber, sessionId);
            invalidSession = machine is null;

            if (machine is not null)
            {
                ReleaseMachine(machine);
                string lobbyId = CreateLobby(guildId, userId);
                components = BuildLobbyButtons(guildId, userId, lobbyId);
            }
        }

        if (invalidSession)
        {
            await RespondSessionExpiredAsync(component);
            return;
        }

        await UpdateMessageAsync(component, BuildLobbyContent(), components!);
    }

    private async Task HandleLobbyRefreshAsync(SocketMessageComponent component, string[] parts)
    {
        if (parts.Length != 4 || !ulong.TryParse(parts[2], out ulong lobbyUserId))
            return;

        if (component.User.Id != lobbyUserId)
        {
            await component.RespondAsync("This isn't your slot lobby!", ephemeral: true);
            return;
        }

        ulong guildId = component.GuildId!.Value;
        string lobbyId = parts[3];
        MessageComponent? components = null;
        bool invalidLobby;

        lock (_syncRoot)
        {
            ExpireSessions();
            invalidLobby = !IsCurrentLobby(guildId, component.User.Id, lobbyId);

            if (!invalidLobby)
                components = BuildLobbyButtons(guildId, component.User.Id, lobbyId);
        }

        if (invalidLobby)
        {
            await component.RespondAsync(
                "That slot lobby is no longer active. Use `/rollslots` again.",
                ephemeral: true);
            return;
        }

        await UpdateMessageAsync(component, BuildLobbyContent(), components!);
    }

    private static bool TryParseMachineSession(
        string[] parts,
        out int machineNumber,
        out string sessionId)
    {
        machineNumber = 0;
        sessionId = string.Empty;

        if (parts.Length != 4 || !int.TryParse(parts[2], out machineNumber))
            return false;

        sessionId = parts[3];
        return true;
    }

    private static Task RespondSessionExpiredAsync(SocketMessageComponent component) =>
        component.RespondAsync("That slot machine session has expired.", ephemeral: true);

    private static Task UpdateMessageAsync(
        SocketMessageComponent component,
        string content,
        MessageComponent components)
    {
        return component.UpdateAsync(message =>
        {
            message.Content = content;
            message.Components = components;
        });
    }

    private string CreateLobby(ulong guildId, ulong userId)
    {
        string lobbyId = Guid.NewGuid().ToString("N");
        _activeLobbies[(guildId, userId)] = lobbyId;
        return lobbyId;
    }

    private bool IsCurrentLobby(ulong guildId, ulong userId, string lobbyId) =>
        _activeLobbies.TryGetValue((guildId, userId), out string? currentLobbyId) &&
        currentLobbyId == lobbyId;

    private void CloseLobby(ulong guildId, ulong userId) =>
        _activeLobbies.Remove((guildId, userId));

    private static string BuildLobbyContent() =>
        "🎰 **Nutty Slots Lobby** 🎰\n\nChoose a slot machine:";

    private MessageComponent BuildLobbyButtons(ulong guildId, ulong userId, string lobbyId)
    {
        var builder = new ComponentBuilder();

        foreach (SlotMachine machine in GetMachines(guildId))
        {
            string statusEmoji = machine.IsClaimed ? "🔒" : "🟢";
            builder.WithButton(
                new ButtonBuilder()
                    .WithLabel($"🎰 #{machine.Number} {statusEmoji}")
                    .WithCustomId($"slots:claim:{machine.Number}:{userId}:{lobbyId}")
                    .WithStyle(machine.IsClaimed ? ButtonStyle.Secondary : ButtonStyle.Primary)
                    .WithDisabled(machine.IsClaimed));
        }

        builder.WithButton(
            "Refresh Lobby",
            $"slots:refresh:{userId}:{lobbyId}",
            ButtonStyle.Secondary,
            emote: new Emoji("🔄"));

        return builder.Build();
    }

    private static MessageComponent BuildMachineButtons(SlotMachine machine) =>
        new ComponentBuilder()
            .WithButton(
                "Roll Again",
                $"slots:roll:{machine.Number}:{machine.SessionId}",
                ButtonStyle.Primary,
                emote: new Emoji("🎰"))
            .WithButton(
                "Leave Machine",
                $"slots:leave:{machine.Number}:{machine.SessionId}",
                ButtonStyle.Secondary,
                emote: new Emoji("🚪"))
            .WithButton(
                "Back to Lobby",
                $"slots:lobby:{machine.Number}:{machine.SessionId}",
                ButtonStyle.Secondary,
                emote: new Emoji("↩️"))
            .Build();

    private List<SlotMachine> GetMachines(ulong guildId)
    {
        if (_machinesByGuild.TryGetValue(guildId, out List<SlotMachine>? machines))
            return machines;

        machines = Enumerable.Range(1, MachineCount)
            .Select(number => new SlotMachine(number))
            .ToList();
        _machinesByGuild[guildId] = machines;
        return machines;
    }

    private bool TryClaimMachine(
        ulong guildId,
        ulong userId,
        int machineNumber,
        out SlotMachine? machine)
    {
        machine = GetMachines(guildId).FirstOrDefault(x => x.Number == machineNumber);

        if (machine is null || machine.IsClaimed)
            return false;

        machine.OwnerUserId = userId;
        machine.SessionId = Guid.NewGuid().ToString("N");
        machine.LastInteractionUtc = DateTimeOffset.UtcNow;
        return true;
    }

    private SlotMachine? FindOwnedMachine(
        ulong guildId,
        ulong userId,
        int machineNumber,
        string sessionId) =>
        GetMachines(guildId).FirstOrDefault(machine =>
            machine.Number == machineNumber &&
            machine.OwnerUserId == userId &&
            machine.SessionId == sessionId);

    private void ReleaseUserMachine(ulong guildId, ulong userId)
    {
        SlotMachine? machine = GetMachines(guildId)
            .FirstOrDefault(x => x.OwnerUserId == userId);

        if (machine is not null)
            ReleaseMachine(machine);
    }

    private static void ReleaseMachine(SlotMachine machine)
    {
        machine.OwnerUserId = null;
        machine.SessionId = null;
        machine.LastInteractionUtc = default;
    }

    private static string Roll(SlotMachine machine)
    {
        string[,] slots = new string[3, 3];

        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
                slots[row, column] = Symbols[Random.Shared.Next(Symbols.Length)];
        }

        bool winner =
            slots[1, 0] == slots[1, 1] &&
            slots[1, 1] == slots[1, 2];

        machine.TotalRolls++;
        machine.TotalWins += winner ? 1 : 0;
        machine.LastInteractionUtc = DateTimeOffset.UtcNow;

        string machineDisplay =
            $"┌───────────┐\n" +
            $"│ {slots[0, 0]}  {slots[0, 1]}  {slots[0, 2]} │\n" +
            $"│ {slots[1, 0]}  {slots[1, 1]}  {slots[1, 2]} │ ⬅️\n" +
            $"│ {slots[2, 0]}  {slots[2, 1]}  {slots[2, 2]} │\n" +
            "└───────────┘";

        string result = winner ? "🎉 **JACKPOT!** 🎉" : "Better luck next time!";

        return
            $"🎰 **Slot Machine #{machine.Number}** 🎰\n\n" +
            $"{machineDisplay}\n\n{result}\n\n" +
            $"**Machine Stats:** {machine.TotalRolls} rolls • {machine.TotalWins} wins";
    }

    private void ExpireSessions()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (SlotMachine machine in _machinesByGuild.Values.SelectMany(x => x))
        {
            if (machine.IsClaimed && now - machine.LastInteractionUtc >= SessionTimeout)
                ReleaseMachine(machine);
        }
    }

    private sealed class SlotMachine(int number)
    {
        public int Number { get; } = number;
        public ulong? OwnerUserId { get; set; }
        public string? SessionId { get; set; }
        public DateTimeOffset LastInteractionUtc { get; set; }
        public int TotalRolls { get; set; }
        public int TotalWins { get; set; }
        public bool IsClaimed => OwnerUserId.HasValue;
    }
}
