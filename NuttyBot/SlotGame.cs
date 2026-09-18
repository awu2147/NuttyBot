using Discord;
using Discord.WebSocket;
using System.Globalization;

namespace NuttyBot;

internal sealed class SlotGame : IDisposable
{
    private const int MachinesPerBatch = 5;
    private const int AdditionalLineBonusPercent = 25;
    private const int ResultMessageMinimumLength = 80;
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(5);
    // Replace any 0 below with a custom emoji ID from the Discord server.
    // If that emoji cannot be found in the current server, the fruit is used instead.
    private static readonly SlotSymbol[] SymbolDefinitions =
    [
        new("🍒", 1471654909085483009, 8), // Cherry
        new("🍋", 1549875953914740846, 10), // Lemon
        new("🍊", 1549882503072977027, 12), // Orange
        new("🍇", 698499914593861712, 15), // Grapes
        new("⭐", 1550281140815003708, 20)  // Star
    ];
    private static readonly MachineBatch[] MachineBatches =
    [
        new(0, 0, [1, 10, 50], "$0", "🟦", new Color(52, 152, 219)),
        new(1, 1_000, [10, 100, 500], "$1K", "🟪", new Color(155, 89, 182)),
        new(2, 1_000_000, [10_000, 100_000, 500_000], "$1M", "🟥", new Color(231, 76, 60)),
        new(3, 1_000_000_000, [10_000_000, 100_000_000, 500_000_000], "$1B", "🟨", new Color(241, 196, 15))
    ];

    private readonly object _syncRoot = new();
    private readonly Dictionary<ulong, List<SlotMachine>> _machinesByGuild = [];
    private readonly Dictionary<(ulong GuildId, ulong UserId), LobbySession> _activeLobbies = [];
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
        string displayName = command.User is SocketGuildUser guildUser
            ? guildUser.DisplayName
            : command.User.Username;

        lock (_syncRoot)
        {
            ExpireSessions();
            ReleaseUserMachine(guildId, userId);
            PlayerData player = GetPlayer(guildId, userId);
            lobbyId = CreateLobby(guildId, userId, displayName, player);
            view = BuildLobbyView(guildId, userId, lobbyId, batchIndex: 0);
        }

        await command.RespondAsync(
            components: view,
            flags: MessageFlags.ComponentsV2);

        try
        {
            IUserMessage message = await command.GetOriginalResponseAsync();

            lock (_syncRoot)
            {
                if (TryGetCurrentLobby(guildId, userId, lobbyId, out LobbySession? lobby))
                    lobby.Message = message;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to track a slot lobby message: {ex.Message}");
        }
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

            case "lobbyleave":
                await HandleLobbyLeaveAsync(component, parts);
                break;

            case "lobby":
                await HandleReturnToLobbyAsync(component, parts);
                break;

            case "refresh":
                await HandleLobbyRefreshAsync(component, parts);
                break;

            case "batch":
                await HandleLobbyBatchAsync(component, parts);
                break;

            case "sell":
                await HandleSellOrganAsync(component, parts);
                break;

            case "lobbysell":
                await HandleLobbySellOrganAsync(component, parts);
                break;

            case "reel":
            case "lever":
                await component.DeferAsync();
                break;
        }
    }

    private async Task HandleClaimAsync(SocketMessageComponent component, string[] parts)
    {
        if (parts.Length != 5 ||
            !int.TryParse(parts[2], out int machineId) ||
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
        string displayName = component.User is SocketGuildUser guildUser ? guildUser.DisplayName : component.User.Username;

        lock (_syncRoot)
        {
            ExpireSessions();
            PlayerData player = GetPlayer(guildId, component.User.Id);
            TouchLobby(
                guildId,
                component.User.Id,
                lobbyId,
                component.Message);

            if (!IsCurrentLobby(guildId, component.User.Id, lobbyId))
            {
                oldLobby = true;
            }
            else if (!TryClaimMachine(
                         guildId,
                         component.User.Id,
                         displayName,
                         player,
                         machineId,
                         out SlotMachine? machine))
            {
                view = BuildLobbyView(
                    guildId,
                    component.User.Id,
                    lobbyId,
                    machine?.Batch.Index ?? 0);
            }
            else
            {
                CloseLobby(guildId, component.User.Id);
                SlotMachine claimedMachine = machine!;
                claimedMachine.Message = component.Message;
                claimedMachine.Player = player;
                IReadOnlyList<string> symbols = ResolveSymbols(component);
                string[,] idleReels = BuildIdleReels(symbols);
                var idleWinningCells = new bool[3, 3];
                claimedMachine.LastReels = idleReels;
                claimedMachine.LastWinningCells = idleWinningCells;
                claimedMachine.LastResult = "Choose a bet when you're ready.";
                view = BuildMachineView(
                    claimedMachine,
                    player,
                    symbols,
                    idleReels,
                    idleWinningCells,
                    claimedMachine.LastResult);
            }
        }

        if (oldLobby)
        {
            await component.RespondAsync(
                "That slot lobby is no longer active. Use `/slots` again.",
                ephemeral: true);
            return;
        }

        await UpdateMessageAsync(component, view!);
    }

    private async Task HandleSellOrganAsync(
        SocketMessageComponent component,
        string[] parts)
    {
        if (!TryParseMachineSession(parts, out int machineId, out string sessionId))
            return;

        MessageComponent? view = null;
        bool invalidSession;

        lock (_syncRoot)
        {
            ExpireSessions();
            SlotMachine? machine = FindOwnedMachine(
                component.GuildId!.Value,
                component.User.Id,
                machineId,
                sessionId);

            invalidSession = machine is null;

            if (machine is not null)
            {
                PlayerData player = GetPlayer(
                    component.GuildId.Value,
                    component.User.Id);
                player.SellOrgan();
                machine.LastInteractionUtc = DateTimeOffset.UtcNow;
                machine.Message = component.Message;
                machine.LastResult =
                    $"🫀 Sold an organ for {FormatMoney(PlayerData.OrganSaleValue)}.";

                IReadOnlyList<string> symbols = ResolveSymbols(component);
                view = BuildMachineView(
                    machine,
                    player,
                    symbols,
                    machine.LastReels ?? BuildIdleReels(symbols),
                    machine.LastWinningCells ?? new bool[3, 3],
                    machine.LastResult);
            }
        }

        if (invalidSession)
        {
            await RespondSessionExpiredAsync(component);
            return;
        }

        await UpdateMessageAsync(component, view!);
    }

    private async Task HandleLobbySellOrganAsync(
        SocketMessageComponent component,
        string[] parts)
    {
        if (parts.Length != 5 ||
            !ulong.TryParse(parts[2], out ulong lobbyUserId) ||
            !int.TryParse(parts[4], out int batchIndex))
        {
            return;
        }

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
            {
                TouchLobby(
                    guildId,
                    component.User.Id,
                    lobbyId,
                    component.Message);
                GetPlayer(guildId, component.User.Id).SellOrgan();
                view = BuildLobbyView(
                    guildId,
                    component.User.Id,
                    lobbyId,
                    batchIndex);
            }
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

    private async Task HandleRollAsync(SocketMessageComponent component, string[] parts)
    {
        if (!TryParseBetSession(
                parts,
                out int machineId,
                out string sessionId,
                out string betToken))
            return;

        MessageComponent? view = null;
        bool invalidSession;
        bool insufficientFunds = false;
        long requiredBalance = 0;
        long currentBalance = 0;

        lock (_syncRoot)
        {
            ExpireSessions();
            SlotMachine? machine = FindOwnedMachine(
                component.GuildId!.Value,
                component.User.Id,
                machineId,
                sessionId);

            invalidSession = machine is null;

            if (machine is not null)
            {
                PlayerData player = GetPlayer(component.GuildId.Value, component.User.Id);

                if (betToken == "all" && player.Balance == 0)
                {
                    insufficientFunds = true;
                    requiredBalance = 1;
                    currentBalance = 0;
                }
                else if (!TryResolveBet(
                             betToken,
                             machine,
                             player,
                             out long betAmount))
                {
                    invalidSession = true;
                }
                else if (!player.CanAfford(betAmount))
                {
                    insufficientFunds = true;
                    requiredBalance = betAmount;
                    currentBalance = player.Balance;
                }
                else
                {
                    machine.Message = component.Message;
                    IReadOnlyList<string> symbols = ResolveSymbols(component);
                    SpinResult spin = Spin(
                        machine,
                        player,
                        betAmount,
                        symbols);
                    view = BuildMachineView(
                        machine,
                        player,
                        symbols,
                        spin.Reels,
                        spin.WinningCells,
                        spin.Result);
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
            await RespondInsufficientFundsAsync(
                component,
                requiredBalance,
                currentBalance);
            return;
        }

        await UpdateMessageAsync(component, view!);
    }

    private async Task HandleLeaveAsync(SocketMessageComponent component, string[] parts)
    {
        if (!TryParseMachineSession(parts, out int machineId, out string sessionId))
            return;

        MessageComponent? view = null;
        bool invalidSession;

        lock (_syncRoot)
        {
            ExpireSessions();
            SlotMachine? machine = FindOwnedMachine(
                component.GuildId!.Value,
                component.User.Id,
                machineId,
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
        if (!TryParseMachineSession(parts, out int machineId, out string sessionId))
            return;

        ulong guildId = component.GuildId!.Value;
        ulong userId = component.User.Id;
        MessageComponent? view = null;
        bool invalidSession;

        lock (_syncRoot)
        {
            ExpireSessions();
            SlotMachine? machine = FindOwnedMachine(guildId, userId, machineId, sessionId);
            invalidSession = machine is null;

            if (machine is not null)
            {
                int batchIndex = machine.Batch.Index;
                string displayName = machine.OwnerDisplayName ?? component.User.Username;
                PlayerData player = GetPlayer(guildId, userId);
                ReleaseMachine(machine);
                string lobbyId = CreateLobby(
                    guildId,
                    userId,
                    displayName,
                    player,
                    component.Message);
                view = BuildLobbyView(guildId, userId, lobbyId, batchIndex);
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
        if (parts.Length != 5 ||
            !ulong.TryParse(parts[2], out ulong lobbyUserId) ||
            !int.TryParse(parts[4], out int batchIndex))
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
            {
                TouchLobby(
                    guildId,
                    component.User.Id,
                    lobbyId,
                    component.Message);
                view = BuildLobbyView(
                    guildId,
                    component.User.Id,
                    lobbyId,
                    batchIndex);
            }
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

    private async Task HandleLobbyBatchAsync(SocketMessageComponent component, string[] parts)
    {
        if (parts.Length != 5 ||
            !ulong.TryParse(parts[2], out ulong lobbyUserId) ||
            !int.TryParse(parts[4], out int batchIndex))
        {
            return;
        }

        if (component.User.Id != lobbyUserId)
        {
            await component.RespondAsync("This isn't your slot lobby!", ephemeral: true);
            return;
        }

        ulong guildId = component.GuildId!.Value;
        string lobbyId = parts[3];
        MessageComponent? view = null;
        bool invalidLobby;
        bool lockedBatch = false;
        long requiredBalance = 0;
        long currentBalance = 0;

        lock (_syncRoot)
        {
            ExpireSessions();
            invalidLobby = !IsCurrentLobby(guildId, component.User.Id, lobbyId);

            if (!invalidLobby)
            {
                TouchLobby(
                    guildId,
                    component.User.Id,
                    lobbyId,
                    component.Message);
                PlayerData player = GetPlayer(guildId, component.User.Id);
                MachineBatch? batch = GetBatch(batchIndex);

                if (batch is null || player.Balance < batch.RequiredBalance)
                {
                    lockedBatch = true;
                    requiredBalance = batch?.RequiredBalance ?? 0;
                    currentBalance = player.Balance;
                }
                else
                {
                    view = BuildLobbyView(
                        guildId,
                        component.User.Id,
                        lobbyId,
                        batchIndex);
                }
            }
        }

        if (invalidLobby)
        {
            await component.RespondAsync(
                "That slot lobby is no longer active. Use `/slots` again.",
                ephemeral: true);
            return;
        }

        if (lockedBatch)
        {
            await component.RespondAsync(
                $"You need a balance of {FormatMoney(requiredBalance)} to access that machine batch. " +
                $"Your balance is {FormatMoney(currentBalance)}.",
                ephemeral: true);
            return;
        }

        await UpdateMessageAsync(component, view!);
    }

    private async Task HandleLobbyLeaveAsync(SocketMessageComponent component, string[] parts)
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
            {
                CloseLobby(guildId, component.User.Id);
                PlayerData player = GetPlayer(guildId, component.User.Id);
                string displayName = component.User is SocketGuildUser guildUser
                    ? guildUser.DisplayName
                    : component.User.Username;
                view = BuildSessionEndedView(displayName, player.Balance);
            }
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
        long requiredBalance,
        long currentBalance) =>
        component.RespondAsync(
            $"You need {FormatMoney(requiredBalance)} for that bet. " +
            $"Your balance is {FormatMoney(currentBalance)}.",
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

    private string CreateLobby(
        ulong guildId,
        ulong userId,
        string displayName,
        PlayerData player,
        IUserMessage? message = null)
    {
        string lobbyId = Guid.NewGuid().ToString("N");
        _activeLobbies[(guildId, userId)] = new LobbySession(
            lobbyId,
            displayName,
            player,
            message);
        return lobbyId;
    }

    private bool TryGetCurrentLobby(
        ulong guildId,
        ulong userId,
        string lobbyId,
        out LobbySession? lobby)
    {
        return _activeLobbies.TryGetValue((guildId, userId), out lobby) &&
            lobby.Id == lobbyId;
    }

    private bool IsCurrentLobby(ulong guildId, ulong userId, string lobbyId) =>
        TryGetCurrentLobby(guildId, userId, lobbyId, out _);

    private void TouchLobby(
        ulong guildId,
        ulong userId,
        string lobbyId,
        IUserMessage message)
    {
        if (!TryGetCurrentLobby(guildId, userId, lobbyId, out LobbySession? lobby))
            return;

        lobby.LastInteractionUtc = DateTimeOffset.UtcNow;
        lobby.Message = message;
    }

    private void CloseLobby(ulong guildId, ulong userId) =>
        _activeLobbies.Remove((guildId, userId));

    private MessageComponent BuildLobbyView(
        ulong guildId,
        ulong userId,
        string lobbyId,
        int batchIndex)
    {
        PlayerData player = GetPlayer(guildId, userId);
        MachineBatch batch = GetAccessibleBatch(player, batchIndex);
        string displayName = TryGetCurrentLobby(
            guildId,
            userId,
            lobbyId,
            out LobbySession? lobby)
                ? lobby.DisplayName
                : "Unknown player";

        var navigationButtons = new ActionRowBuilder()
            .WithButton(
                "Refresh Lobby",
                $"slots:refresh:{userId}:{lobbyId}:{batch.Index}",
                ButtonStyle.Secondary,
                emote: new Emoji("🔄"))
            .WithButton(
                "Sell Organs",
                $"slots:lobbysell:{userId}:{lobbyId}:{batch.Index}",
                ButtonStyle.Danger,
                emote: new Emoji("🫀"))
            .WithButton(
                "Leave Casino",
                $"slots:lobbyleave:{userId}:{lobbyId}",
                ButtonStyle.Secondary,
                emote: new Emoji("🚪"));

        var container = new ContainerBuilder()
            .WithAccentColor(batch.AccentColor)
            .WithTextDisplay(
                $"**Player:** {displayName} • " +
                $"**Organs Sold:** {player.OrgansSold}\n" +
                $"**Balance:** {FormatMoney(player.Balance)}")
            .WithActionRow(navigationButtons)
            .WithTextDisplay("Choose an available slot machine:");

        foreach (SlotMachine machine in GetMachines(guildId)
                     .Where(machine => machine.Batch.Index == batch.Index))
        {
            string occupant = machine.IsClaimed
                ? machine.OwnerDisplayName ?? "Unknown user"
                : "Available";

            var machineButton = new ButtonBuilder()
                .WithLabel("🎰")
                .WithCustomId(
                    $"slots:claim:{machine.Id}:{userId}:{lobbyId}")
                .WithStyle(
                    machine.IsClaimed
                        ? ButtonStyle.Secondary
                        : ButtonStyle.Primary)
                .WithEmote(new Emoji(machine.Batch.ColorEmoji))
                .WithDisabled(machine.IsClaimed);

            var occupantDisplay = new ButtonBuilder()
                .WithLabel(occupant)
                .WithCustomId($"slots:owner:{machine.Id}")
                .WithStyle(ButtonStyle.Secondary)
                .WithDisabled(true);

            container.WithActionRow(
                new ActionRowBuilder()
                    .WithButton(machineButton)
                    .WithButton(occupantDisplay));
        }

        var batchButtons = new ActionRowBuilder();

        foreach (MachineBatch availableBatch in MachineBatches)
        {
            bool canAccess = player.Balance >= availableBatch.RequiredBalance;

            batchButtons.WithButton(
                availableBatch.ButtonLabel,
                $"slots:batch:{userId}:{lobbyId}:{availableBatch.Index}",
                availableBatch.Index == batch.Index
                    ? ButtonStyle.Primary
                    : ButtonStyle.Secondary,
                emote: new Emoji(availableBatch.ColorEmoji),
                disabled: !canAccess);
        }

        var statusName = string.Empty;
        if (batch.RequiredBalance == 0)
        {
            statusName = "Chud";
        }
        else if (batch.RequiredBalance == 1000)
        { 
            statusName = "High Roller"; 
        }
        else if (batch.RequiredBalance == 1000000)
        {
            statusName = "Millionaire";
        }
        else if (batch.RequiredBalance == 1000000000)
        {
            statusName = "Billionaire";
        }

        container
            .WithTextDisplay($"**{statusName} Room [{FormatMoney(batch.RequiredBalance)}+]**")
            .WithActionRow(batchButtons);

        return new ComponentBuilderV2()
            .WithContainer(container)
            .Build();
    }

    private static MessageComponent BuildMachineView(
        SlotMachine machine,
        PlayerData player,
        IReadOnlyList<string> symbols,
        string[,] reels,
        bool[,] winningCells,
        string? result)
    {
        var navigationButtons = new ActionRowBuilder()
            .WithButton(
                "Choose Machine",
                $"slots:lobby:{machine.Id}:{machine.SessionId}",
                ButtonStyle.Secondary,
                emote: new Emoji("↩️"))
            .WithButton(
                "Sell Organs",
                $"slots:sell:{machine.Id}:{machine.SessionId}",
                ButtonStyle.Danger,
                emote: new Emoji("🫀"))
            .WithButton(
                "Leave Casino",
                $"slots:leave:{machine.Id}:{machine.SessionId}",
                ButtonStyle.Secondary,
                emote: new Emoji("🚪"));

        var betButtons = new ActionRowBuilder();

        foreach (long betAmount in machine.Batch.BetAmounts)
        {
            betButtons.WithButton(
                FormatCompactAmount(betAmount),
                $"slots:roll:{machine.Id}:{machine.SessionId}:{betAmount}",
                ButtonStyle.Primary,
                disabled: !player.CanAfford(betAmount));
        }

        betButtons.WithButton(
                "All In",
                $"slots:roll:{machine.Id}:{machine.SessionId}:all",
                ButtonStyle.Danger,
                disabled: player.Balance <= 0);

        string linePayouts = string.Join(
            " • ",
            SymbolDefinitions.Select((symbol, index) =>
                $"{symbols[index]} ×{symbol.LineMultiplier}"));

        string machineInformation =
            $"**Line Payouts:** {linePayouts}\n" +
            $"**Multi-Line Bonus:** +{AdditionalLineBonusPercent}% per extra line\n" +
            $"**Machine Stats:** {machine.TotalRolls} rolls • {machine.TotalWins} wins";

        var container = new ContainerBuilder()
            .WithAccentColor(machine.Batch.AccentColor)
            .WithTextDisplay(
                $"**Player:** {machine.OwnerDisplayName ?? "Unknown player"} • " +
                $"**Organs Sold:** {player.OrgansSold}\n" +
                $"**Balance:** {FormatMoney(player.Balance)}")
            .WithActionRow(navigationButtons)
            .WithTextDisplay($"## 🎰 Slot Machine {machine.Number} 🎰\n\n")
            .WithTextDisplay(machineInformation)
            .WithActionRow(BuildReelRow(machine, reels, winningCells, row: 0))
            .WithActionRow(BuildReelRow(
                machine,
                reels,
                winningCells,
                row: 1,
                includeLever: true))
            .WithActionRow(BuildReelRow(machine, reels, winningCells, row: 2));

        if (result is not null)
            container.WithTextDisplay(PadResultMessage(result));

        container
            .WithTextDisplay(
                $"**Balance:** {FormatMoney(player.Balance)} • **Next Spin Bet:**")
            .WithActionRow(betButtons);

        return new ComponentBuilderV2()
            .WithContainer(container)
            .Build();
    }

    private static ActionRowBuilder BuildReelRow(
        SlotMachine machine,
        string[,] reels,
        bool[,] winningCells,
        int row,
        bool includeLever = false)
    {
        var reelRow = new ActionRowBuilder();

        for (int column = 0; column < 3; column++)
        {
            reelRow.WithButton(
                new ButtonBuilder()
                    .WithCustomId(
                        $"slots:reel:{machine.Id}:{machine.SessionId}:{row}:{column}")
                    .WithStyle(
                        winningCells[row, column]
                            ? ButtonStyle.Success
                            : ButtonStyle.Secondary)
                    .WithEmote(ParseReelEmote(reels[row, column])));
        }

        if (includeLever)
        {
            reelRow.WithButton(
                new ButtonBuilder()
                    .WithCustomId(
                        $"slots:lever:{machine.Id}:{machine.SessionId}")
                    .WithStyle(ButtonStyle.Secondary)
                    .WithEmote(new Emoji("🕹️")));
        }

        return reelRow;
    }

    private static IEmote ParseReelEmote(string symbol) =>
        symbol.StartsWith('<')
            ? Emote.Parse(symbol)
            : new Emoji(symbol);

    private static string PadResultMessage(string message)
    {
        int paddingLength = ResultMessageMinimumLength - message.Length;

        if (paddingLength <= 0)
            return message;

        return message + string.Concat(
            Enumerable.Repeat("\u200B\u2800", paddingLength));
    }

    private static string[,] BuildIdleReels(IReadOnlyList<string> symbols) =>
        new[,]
        {
            { symbols[0], symbols[1], symbols[2] },
            { symbols[4], symbols[3], symbols[0] },
            { symbols[2], symbols[4], symbols[1] }
        };

    private static IReadOnlyList<string> ResolveSymbols(
        SocketMessageComponent component)
    {
        SocketGuild? guild = (component.Channel as SocketGuildChannel)?.Guild;

        return SymbolDefinitions
            .Select(symbol =>
            {
                if (symbol.CustomEmojiId == 0 || guild is null)
                    return symbol.FallbackEmoji;

                var customEmoji = guild.Emotes
                    .FirstOrDefault(emote => emote.Id == symbol.CustomEmojiId);

                return customEmoji is { IsAvailable: true }
                    ? customEmoji.ToString()
                    : symbol.FallbackEmoji;
            })
            .ToArray();
    }

    private static string FormatCompactAmount(long amount)
    {
        if (amount >= 1_000_000_000_000_000)
            return $"${(decimal)amount / 1_000_000_000_000_000:0.##}Q";

        if (amount >= 1_000_000_000_000)
            return $"${(decimal)amount / 1_000_000_000_000:0.##}T";

        if (amount >= 1_000_000_000)
            return $"${(decimal)amount / 1_000_000_000:0.##}B";

        if (amount >= 1_000_000)
            return $"${(decimal)amount / 1_000_000:0.##}M";

        if (amount >= 1_000)
            return $"${(decimal)amount / 1_000:0.##}K";

        return $"${amount}";
    }

    private static string FormatMoney(long amount) =>
        "$" + amount.ToString("N0", CultureInfo.InvariantCulture);

    private static MessageComponent BuildSessionEndedView(
        string displayName,
        long balance) =>
        new ComponentBuilderV2()
            .WithContainer(container => container
                .WithAccentColor(new Color(100, 100, 100))
                .WithTextDisplay(
                    $"{displayName} has left the casino with a balance of {FormatMoney(balance)}."))
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

    private static MachineBatch? GetBatch(int batchIndex) =>
        MachineBatches.FirstOrDefault(batch => batch.Index == batchIndex);

    private static MachineBatch GetAccessibleBatch(
        PlayerData player,
        int preferredBatchIndex)
    {
        MachineBatch? preferredBatch = GetBatch(preferredBatchIndex);

        if (preferredBatch is not null && player.Balance >= preferredBatch.RequiredBalance)
            return preferredBatch;

        return MachineBatches[0];
    }

    private List<SlotMachine> GetMachines(ulong guildId)
    {
        if (_machinesByGuild.TryGetValue(guildId, out List<SlotMachine>? machines))
            return machines;

        machines = MachineBatches
            .SelectMany(batch => Enumerable.Range(1, MachinesPerBatch)
                .Select(number => new SlotMachine(
                    batch.Index * MachinesPerBatch + number,
                    number,
                    batch)))
            .ToList();
        _machinesByGuild[guildId] = machines;
        return machines;
    }

    private bool TryClaimMachine(
        ulong guildId,
        ulong userId,
        string userDisplayName,
        PlayerData player,
        int machineId,
        out SlotMachine? machine)
    {
        machine = GetMachines(guildId)
            .FirstOrDefault(x => x.Id == machineId);

        if (machine is null ||
            machine.IsClaimed ||
            player.Balance < machine.Batch.RequiredBalance)
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
        int machineId,
        string sessionId) =>
        GetMachines(guildId).FirstOrDefault(machine =>
            machine.Id == machineId &&
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
        machine.LastReels = null;
        machine.LastWinningCells = null;
        machine.LastResult = null;
    }

    private static bool TryParseBetSession(
        string[] parts,
        out int machineId,
        out string sessionId,
        out string betToken)
    {
        machineId = 0;
        sessionId = string.Empty;
        betToken = string.Empty;

        if (parts.Length != 5 || !int.TryParse(parts[2], out machineId))
            return false;

        sessionId = parts[3];
        betToken = parts[4];
        return true;
    }

    private static bool TryResolveBet(
        string betToken,
        SlotMachine machine,
        PlayerData player,
        out long betAmount)
    {
        if (betToken == "all")
        {
            betAmount = player.Balance;
            return betAmount > 0;
        }

        return long.TryParse(betToken, out betAmount) &&
            machine.Batch.BetAmounts.Contains(betAmount);
    }

    private static SpinResult Spin(
        SlotMachine machine,
        PlayerData player,
        long betAmount,
        IReadOnlyList<string> symbols)
    {
        if (!player.TrySpend(betAmount))
            throw new InvalidOperationException("The player cannot afford this spin.");

        string[,] slots = new string[3, 3];

        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
                slots[row, column] = symbols[Random.Shared.Next(symbols.Count)];
        }

        var winningLines = new List<WinningLine>();
        var winningCells = new bool[3, 3];

        void CheckLine(
            int row1, int column1,
            int row2, int column2,
            int row3, int column3)
        {
            string symbol = slots[row1, column1];

            if (symbol != slots[row2, column2] ||
                symbol != slots[row3, column3])
            {
                return;
            }

            int symbolIndex = -1;

            for (int index = 0; index < symbols.Count; index++)
            {
                if (symbols[index] == symbol)
                {
                    symbolIndex = index;
                    break;
                }
            }

            if (symbolIndex >= 0)
            {
                winningLines.Add(new WinningLine(
                    symbol,
                    SymbolDefinitions[symbolIndex].LineMultiplier));
                winningCells[row1, column1] = true;
                winningCells[row2, column2] = true;
                winningCells[row3, column3] = true;
            }
        }

        for (int row = 0; row < 3; row++)
            CheckLine(row, 0, row, 1, row, 2);

        for (int column = 0; column < 3; column++)
            CheckLine(0, column, 1, column, 2, column);

        CheckLine(0, 0, 1, 1, 2, 2);
        CheckLine(0, 2, 1, 1, 2, 0);

        bool winner = winningLines.Count > 0;

        machine.TotalRolls++;
        machine.TotalWins += winner ? 1 : 0;
        machine.LastInteractionUtc = DateTimeOffset.UtcNow;

        long payout = 0;
        decimal effectiveMultiplier = 0;

        if (winner)
        {
            int baseMultiplier = winningLines.Sum(line => line.Multiplier);
            int bonusPercent = 100 +
                ((winningLines.Count - 1) * AdditionalLineBonusPercent);

            effectiveMultiplier = baseMultiplier * bonusPercent / 100m;
            payout = checked((long)(betAmount * effectiveMultiplier));
            player.Credit(payout);
        }

        string result = winner
            ? $"🎉 **{winningLines.Count} winning " +
              $"{(winningLines.Count == 1 ? "line" : "lines")}! " +
              $"×{effectiveMultiplier.ToString("0.##", CultureInfo.InvariantCulture)} — " +
              $"You won {FormatMoney(payout)}!** 🎉"
            : "Better luck next time!";

        machine.LastReels = slots;
        machine.LastWinningCells = winningCells;
        machine.LastResult = result;

        return new SpinResult(slots, winningCells, result);
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
                long balance = machine.Player?.Balance ?? 0;
                ReleaseMachine(machine);

                if (message is not null)
                    _ = UpdateExpiredMessageAsync(message, displayName, balance);
            }
        }

        var expiredLobbies = _activeLobbies
            .Where(entry => now - entry.Value.LastInteractionUtc >= SessionTimeout)
            .ToList();

        foreach (var entry in expiredLobbies)
        {
            _activeLobbies.Remove(entry.Key);
            LobbySession lobby = entry.Value;

            if (lobby.Message is not null)
            {
                _ = UpdateExpiredMessageAsync(
                    lobby.Message,
                    lobby.DisplayName,
                    lobby.Player.Balance);
            }
        }
    }

    private static async Task UpdateExpiredMessageAsync(
        IUserMessage message,
        string displayName,
        long balance)
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

    private sealed record SpinResult(
        string[,] Reels,
        bool[,] WinningCells,
        string Result);

    private sealed record WinningLine(string Symbol, int Multiplier);

    private sealed record SlotSymbol(
        string FallbackEmoji,
        ulong CustomEmojiId,
        int LineMultiplier);

    private sealed class LobbySession(
        string id,
        string displayName,
        PlayerData player,
        IUserMessage? message)
    {
        public string Id { get; } = id;
        public string DisplayName { get; } = displayName;
        public PlayerData Player { get; } = player;
        public IUserMessage? Message { get; set; } = message;
        public DateTimeOffset LastInteractionUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed record MachineBatch(
        int Index,
        long RequiredBalance,
        long[] BetAmounts,
        string ButtonLabel,
        string ColorEmoji,
        Color AccentColor);

    private sealed class SlotMachine(
        int id,
        int number,
        MachineBatch batch)
    {
        public int Id { get; } = id;
        public int Number { get; } = number;
        public MachineBatch Batch { get; } = batch;
        public ulong? OwnerUserId { get; set; }
        public string? OwnerDisplayName { get; set; }
        public string? SessionId { get; set; }
        public DateTimeOffset LastInteractionUtc { get; set; }
        public IUserMessage? Message { get; set; }
        public PlayerData? Player { get; set; }
        public string[,]? LastReels { get; set; }
        public bool[,]? LastWinningCells { get; set; }
        public string? LastResult { get; set; }
        public int TotalRolls { get; set; }
        public int TotalWins { get; set; }
        public bool IsClaimed => OwnerUserId.HasValue;
    }
}
