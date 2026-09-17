using Discord;
using Discord.WebSocket;

namespace NuttyBot;

internal sealed class SlotGame : IDisposable
{
    private const int MachineCount = 5;
    private const int SpinCost = 1;
    private const int WinPayout = 100;
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(5);
    private static readonly string[] Symbols = ["🍒", "🍋", "🍊", "🍇", "⭐"];

    private readonly object _syncRoot = new();
    private readonly Dictionary<ulong, List<SlotMachine>> _machinesByGuild = [];
    private readonly Dictionary<(ulong GuildId, ulong UserId), string> _activeLobbies = [];
    private readonly Dictionary<(ulong GuildId, ulong UserId), PlayerData> _players = [];
    private Timer? _cleanupTimer;

    public static SlashCommandBuilder CreateCommand() => new SlashCommandBuilder().WithName("slots").WithDescription("Open the slot machine lobby");

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
        MessageComponent view;
        ulong guildId = command.GuildId.Value;
        ulong userId = command.User.Id;

        lock (_syncRoot)
        {
            ExpireSessions();
            ReleaseUserMachine(guildId, userId);
            lobbyId = CreateLobby(guildId, userId);
            view = BuildLobbyView(guildId, userId, lobbyId);
        }

        await command.RespondAsync(
            components: view,
            flags: MessageFlags.ComponentsV2);
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
        MessageComponent? view = null;
        bool oldLobby = false;
        bool insufficientFunds = false;
        int currentBalance = 0;
        string displayName = component.User is SocketGuildUser guildUser ? guildUser.DisplayName : component.User.Username;

        lock (_syncRoot)
        {
            ExpireSessions();

            if (!IsCurrentLobby(guildId, component.User.Id, lobbyId))
            {
                oldLobby = true;
            }
            else if (!GetPlayer(guildId, component.User.Id).CanAfford(SpinCost))
            {
                PlayerData player = GetPlayer(guildId, component.User.Id);
                insufficientFunds = true;
                currentBalance = player.Balance;
            }
            else if (!TryClaimMachine(guildId, component.User.Id, displayName, machineNumber, out SlotMachine? machine))
            {
                view = BuildLobbyView(guildId, component.User.Id, lobbyId);
            }
            else
            {
                CloseLobby(guildId, component.User.Id);
                SlotMachine claimedMachine = machine!;
                PlayerData player = GetPlayer(guildId, component.User.Id);
                claimedMachine.Message = component.Message;
                claimedMachine.Player = player;
                view = BuildMachineView(
                    claimedMachine,
                    player,
                    machineDisplay: BuildIdleMachineDisplay(),
                    result: "Press **Spin** when you're ready.",
                    spinButtonLabel: "Spin");
            }
        }

        if (oldLobby)
        {
            await component.RespondAsync(
                "That slot lobby is no longer active. Use `/slots` again.",
                ephemeral: true);
            return;
        }

        if (insufficientFunds)
        {
            await RespondInsufficientFundsAsync(component, currentBalance);
            return;
        }

        await UpdateMessageAsync(component, view!);
    }

    private async Task HandleRollAsync(SocketMessageComponent component, string[] parts)
    {
        if (!TryParseMachineSession(parts, out int machineNumber, out string sessionId))
            return;

        MessageComponent? view = null;
        bool invalidSession;
        bool insufficientFunds = false;
        int currentBalance = 0;

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
                PlayerData player = GetPlayer(component.GuildId.Value, component.User.Id);

                if (!player.CanAfford(SpinCost))
                {
                    insufficientFunds = true;
                    currentBalance = player.Balance;
                }
                else
                {
                    view = Roll(machine, player);
                }
            }
        }

        if (invalidSession)
        {
            await RespondSessionExpiredAsync(component);
            return;
        }

        if (insufficientFunds)
        {
            await RespondInsufficientFundsAsync(component, currentBalance);
            return;
        }

        await UpdateMessageAsync(component, view!);
    }

    private async Task HandleLeaveAsync(SocketMessageComponent component, string[] parts)
    {
        if (!TryParseMachineSession(parts, out int machineNumber, out string sessionId))
            return;

        MessageComponent? view = null;
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
                view = BuildSessionEndedView(
                    machine.OwnerDisplayName ?? component.User.Username,
                    machine.Player?.Balance ?? GetPlayer(
                        component.GuildId.Value,
                        component.User.Id).Balance);
                ReleaseMachine(machine);
            }
        }

        if (invalidSession)
        {
            await RespondSessionExpiredAsync(component);
            return;
        }

        await UpdateMessageAsync(component, view!);
    }

    private async Task HandleReturnToLobbyAsync(SocketMessageComponent component, string[] parts)
    {
        if (!TryParseMachineSession(parts, out int machineNumber, out string sessionId))
            return;

        ulong guildId = component.GuildId!.Value;
        ulong userId = component.User.Id;
        MessageComponent? view = null;
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
                view = BuildLobbyView(guildId, userId, lobbyId);
            }
        }

        if (invalidSession)
        {
            await RespondSessionExpiredAsync(component);
            return;
        }

        await UpdateMessageAsync(component, view!);
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
        MessageComponent? view = null;
        bool invalidLobby;

        lock (_syncRoot)
        {
            ExpireSessions();
            invalidLobby = !IsCurrentLobby(guildId, component.User.Id, lobbyId);

            if (!invalidLobby)
                view = BuildLobbyView(guildId, component.User.Id, lobbyId);
        }

        if (invalidLobby)
        {
            await component.RespondAsync(
                "That slot lobby is no longer active. Use `/slots` again.",
                ephemeral: true);
            return;
        }

        await UpdateMessageAsync(component, view!);
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

    private static Task RespondInsufficientFundsAsync(
        SocketMessageComponent component,
        int currentBalance) =>
        component.RespondAsync(
            $"You need ${SpinCost} to spin. Your balance is ${currentBalance}.",
            ephemeral: true);

    private static Task UpdateMessageAsync(
        SocketMessageComponent component,
        MessageComponent view)
    {
        return component.UpdateAsync(message =>
        {
            message.Components = view;
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

    private MessageComponent BuildLobbyView(
     ulong guildId,
     ulong userId,
     string lobbyId)
    {
        PlayerData player = GetPlayer(guildId, userId);

        var container = new ContainerBuilder()
            .WithAccentColor(new Color(0, 200, 220))
            .WithTextDisplay(
                "## 🎰 Nutty Slots 🎰\n\n" +
                $"**Balance:** ${player.Balance}\n\n" +
                "Choose an available machine:");

        foreach (SlotMachine machine in GetMachines(guildId))
        {
            string occupant = machine.IsClaimed
                ? machine.OwnerDisplayName ?? "Unknown user"
                : "Available";

            var machineButton = new ButtonBuilder()
                .WithCustomId(
                    $"slots:claim:{machine.Number}:{userId}:{lobbyId}")
                .WithStyle(
                    machine.IsClaimed || !player.CanAfford(SpinCost)
                        ? ButtonStyle.Secondary
                        : ButtonStyle.Primary)
                .WithEmote(new Emoji("🎰"))
                .WithDisabled(machine.IsClaimed || !player.CanAfford(SpinCost));

            var occupantDisplay = new ButtonBuilder()
                .WithLabel(occupant)
                .WithCustomId($"slots:owner:{machine.Number}")
                .WithStyle(ButtonStyle.Secondary)
                .WithDisabled(true);

            container.WithActionRow(
                new ActionRowBuilder()
                    .WithButton(machineButton)
                    .WithButton(occupantDisplay));
        }

        container.WithActionRow(
            new ActionRowBuilder()
                .WithButton(
                    "Refresh Lobby",
                    $"slots:refresh:{userId}:{lobbyId}",
                    ButtonStyle.Secondary,
                    emote: new Emoji("🔄")));

        return new ComponentBuilderV2()
            .WithContainer(container)
            .Build();
    }

    private static MessageComponent BuildMachineView(
        SlotMachine machine,
        PlayerData player,
        string? machineDisplay,
        string? result,
        string spinButtonLabel)
    {
        var navigationButtons = new ActionRowBuilder()
            .WithButton(
                "Choose Machine",
                $"slots:lobby:{machine.Number}:{machine.SessionId}",
                ButtonStyle.Secondary,
                emote: new Emoji("↩️"))
            .WithButton(
                "Leave Casino",
                $"slots:leave:{machine.Number}:{machine.SessionId}",
                ButtonStyle.Secondary,
                emote: new Emoji("🚪"));

        var rollButton = new ActionRowBuilder()
            .WithButton(
                spinButtonLabel,
                $"slots:roll:{machine.Number}:{machine.SessionId}",
                ButtonStyle.Primary,
                emote: new Emoji("🎰"),
                disabled: !player.CanAfford(SpinCost));

        string display =
            $"## 🎰 Slot Machine #{machine.Number} 🎰\n\n";

        if (machineDisplay is not null)
            display += $"```text\n{machineDisplay}\n```\n";

        if (result is not null)
            display += $"{result}\n\n";

        display +=
            $"**Balance:** ${player.Balance}\n" +
            $"**Spin Cost:** ${SpinCost} • **Win Payout:** ${WinPayout}\n" +
            $"**Machine Stats:** {machine.TotalRolls} rolls • {machine.TotalWins} wins";

        return new ComponentBuilderV2()
            .WithContainer(container => container
                .WithAccentColor(new Color(0, 200, 220))
                .WithActionRow(navigationButtons)
                .WithTextDisplay(display)
                .WithActionRow(rollButton))
            .Build();
    }

    private static string BuildIdleMachineDisplay() =>
        "┌───────────┐\n" +
        "│ 🍒  🍋  🍊 │\n" +
        "│ ⭐  🍇  🍒 │ ⬅️\n" +
        "│ 🍊  ⭐  🍋 │\n" +
        "└───────────┘";

    private static MessageComponent BuildSessionEndedView(
        string displayName,
        int balance) =>
        new ComponentBuilderV2()
            .WithContainer(container => container
                .WithAccentColor(new Color(100, 100, 100))
                .WithTextDisplay(
                    $"{displayName} has left the casino with a balance of ${balance}."))
            .Build();

    private PlayerData GetPlayer(ulong guildId, ulong userId)
    {
        var key = (guildId, userId);

        if (_players.TryGetValue(key, out PlayerData? player))
            return player;

        player = new PlayerData(userId);
        _players[key] = player;
        return player;
    }

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
        string userDisplayName,
        int machineNumber,
        out SlotMachine? machine)
    {
        machine = GetMachines(guildId)
            .FirstOrDefault(x => x.Number == machineNumber);

        if (machine is null || machine.IsClaimed)
            return false;

        machine.OwnerUserId = userId;
        machine.OwnerDisplayName = userDisplayName;
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
        machine.OwnerDisplayName = null;
        machine.SessionId = null;
        machine.LastInteractionUtc = default;
        machine.Message = null;
        machine.Player = null;
    }

    private static MessageComponent Roll(SlotMachine machine, PlayerData player)
    {
        if (!player.TrySpend(SpinCost))
            throw new InvalidOperationException("The player cannot afford this spin.");

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

        if (winner)
            player.Credit(WinPayout);

        string machineDisplay =
            $"┌───────────┐\n" +
            $"│ {slots[0, 0]}  {slots[0, 1]}  {slots[0, 2]} │\n" +
            $"│ {slots[1, 0]}  {slots[1, 1]}  {slots[1, 2]} │ ⬅️\n" +
            $"│ {slots[2, 0]}  {slots[2, 1]}  {slots[2, 2]} │\n" +
            "└───────────┘";

        string result = winner
            ? $"🎉 **JACKPOT! You won ${WinPayout}!** 🎉"
            : "Better luck next time!";

        return BuildMachineView(
            machine,
            player,
            machineDisplay,
            result,
            spinButtonLabel: "Spin Again");
    }

    private void ExpireSessions()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (SlotMachine machine in _machinesByGuild.Values.SelectMany(x => x))
        {
            if (machine.IsClaimed && now - machine.LastInteractionUtc >= SessionTimeout)
            {
                IUserMessage? message = machine.Message;
                string displayName = machine.OwnerDisplayName ?? "The player";
                int balance = machine.Player?.Balance ?? 0;
                ReleaseMachine(machine);

                if (message is not null)
                    _ = UpdateExpiredMessageAsync(message, displayName, balance);
            }
        }
    }

    private static async Task UpdateExpiredMessageAsync(
        IUserMessage message,
        string displayName,
        int balance)
    {
        try
        {
            await message.ModifyAsync(properties =>
            {
                properties.Components = BuildSessionEndedView(displayName, balance);
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to update an expired slot session: {ex.Message}");
        }
    }

    private sealed class SlotMachine(int number)
    {
        public int Number { get; } = number;
        public ulong? OwnerUserId { get; set; }
        public string? OwnerDisplayName { get; set; }
        public string? SessionId { get; set; }
        public DateTimeOffset LastInteractionUtc { get; set; }
        public IUserMessage? Message { get; set; }
        public PlayerData? Player { get; set; }
        public int TotalRolls { get; set; }
        public int TotalWins { get; set; }
        public bool IsClaimed => OwnerUserId.HasValue;
    }
}
