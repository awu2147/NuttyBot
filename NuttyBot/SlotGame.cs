using Discord;
using Discord.WebSocket;
using System.Globalization;
using System.Text;
using static System.Net.Mime.MediaTypeNames;

namespace NuttyBot;

internal sealed class SlotGame : IDisposable
{
    private const int MachinesPerBatch = 5;
    private const int AdditionalLineBonusPercent = 25;
    private const int ResultMessageMinimumLength = 80;
    // Optional per-guild custom emoji overrides. The 14 built-in emojis below
    // are always the defaults; only add entries for symbols a guild overrides.
    //
    // Custom1  = 🍒   Custom6  = 🔔   Custom11 = 💰
    // Custom2  = 🍋   Custom7  = 💎   Custom12 = 🏆
    // Custom3  = 🍊   Custom8  = 👑   Custom13 = 🔥
    // Custom4  = 🍇   Custom9  = 🍀   Custom14 = 🌟
    // Custom5  = 🍉   Custom10 = 🪙
    private static readonly Dictionary<ulong, Dictionary<SlotSymbolKey, ulong>>
        GuildCustomEmojiIds = new()
        {
            // Nutty
            [472949270857777152] = new()
            {
                // Custom1-Custom5 are intentionally omitted, so the Chud room
                // currently uses the five default fruit emojis. Add IDs here
                // whenever you want to replace any of them.
                [SlotSymbolKey.Custom6] = 1471654909085483009,
                [SlotSymbolKey.Custom7] = 1549875953914740846,
                [SlotSymbolKey.Custom8] = 1549882503072977027,
                [SlotSymbolKey.Custom9] = 698499914593861712,
                [SlotSymbolKey.Custom10] = 1550281140815003708,
                [SlotSymbolKey.Custom11] = 647573467570372639,
                [SlotSymbolKey.Custom12] = 784426912948289546,
                [SlotSymbolKey.Custom13] = 1549839919378333817,
                [SlotSymbolKey.Custom14] = 1387382724242833419
            },
            // Rago Demons
            [992184140881461350] = new()
            {
                // Custom1-Custom5 are intentionally omitted, so the Chud room
                // currently uses the five default fruit emojis. Add IDs here
                // whenever you want to replace any of them.
                [SlotSymbolKey.Custom7] = 1404576307764920320,
                [SlotSymbolKey.Custom8] = 1403585589663502487,
                [SlotSymbolKey.Custom9] = 1403585535343198228,
                [SlotSymbolKey.Custom10] = 1403585374420467782,
                [SlotSymbolKey.Custom11] = 1403585496999002223,
                [SlotSymbolKey.Custom12] = 1401901345438437537,
                [SlotSymbolKey.Custom13] = 1405797511498563658,
                [SlotSymbolKey.Custom14] = 1401048413532852314
            }

            // Add another guild like this:
            // [GUILD_ID] = new()
            // {
            //     [SlotSymbolKey.Custom1] = EMOJI_ID, // replaces 🍒
            //     [SlotSymbolKey.Custom5] = EMOJI_ID  // replaces 🍉
            // }
        };

    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(5);
    // These are the 14 default visuals and payout values. Every position can
    // optionally be replaced by a guild-specific custom emoji above.
    private static readonly SlotSymbol[] SymbolDefinitions =
    [
        new(SlotSymbolKey.Custom1, "🍒", 2),
        new(SlotSymbolKey.Custom2, "🍋", 3),
        new(SlotSymbolKey.Custom3, "🍊", 5),
        new(SlotSymbolKey.Custom4, "🍇", 8),
        new(SlotSymbolKey.Custom5, "🍉", 13),
        new(SlotSymbolKey.Custom6, "🔔", 20),
        new(SlotSymbolKey.Custom7, "💎", 30),
        new(SlotSymbolKey.Custom8, "👑", 50),
        new(SlotSymbolKey.Custom9, "🍀", 80),
        new(SlotSymbolKey.Custom10, "🪙", 130),
        new(SlotSymbolKey.Custom11, "💰", 200),
        new(SlotSymbolKey.Custom12, "🏆", 300),
        new(SlotSymbolKey.Custom13, "🔥", 500),
        new(SlotSymbolKey.Custom14, "🌟", 800)
    ];
    private static readonly int[][] RoomSymbolIndexes =
    [
        [0, 1, 2, 3, 4],     // $0: five fruits
        [3, 4, 5, 6, 7],     // $1K: top two below + three new
        [6, 7, 8, 9, 10],    // $1M: top two below + three new
        [9, 10, 11, 12, 13]  // $1B: top two below + three new
    ];
    private static readonly MachineBatch[] MachineBatches =
    [
        new(0, 0, [1, 10, 50], "$0", "🟦", new Color(52, 152, 219)),
        new(1, 1_000, [100, 1000, 5000], "$1K", "🟪", new Color(155, 89, 182)),
        new(2, 1_000_000, [100_000, 500_000, 2_000_000], "$1M", "🟥", new Color(231, 76, 60)),
        new(3, 1_000_000_000, [100_000_000, 500_000_000, 2_000_000_000], "$1B", "🟨", new Color(241, 196, 15))
    ];

    private readonly object _syncRoot = new();
    private readonly Dictionary<ulong, List<SlotMachine>> _machinesByGuild = [];
    private readonly Dictionary<(ulong GuildId, ulong UserId), LobbySession> _activeLobbies = [];
    private readonly Dictionary<(ulong GuildId, ulong UserId), PlayerData> _players = [];
    private readonly Dictionary<(ulong GuildId, ulong UserId), PlayerData> _speedPlayers = [];
    private readonly Dictionary<(ulong GuildId, ulong UserId), SlotMachine> _speedMachines = [];
    private readonly SlotLeaderboard _leaderboard = SlotLeaderboard.Load(
        Path.Combine(AppContext.BaseDirectory, "slot-leaderboard.json"));
    private Timer? _cleanupTimer;

    public static SlashCommandBuilder CreateCommand() => new SlashCommandBuilder().WithName("slots").WithDescription("Open the slot machine lobby");

    public static SlashCommandBuilder CreateSpeedCommand() => new SlashCommandBuilder()
        .WithName("slotsspeed")
        .WithDescription("Start a lobby-free slots speed run");

    public static SlashCommandBuilder CreateLeaderboardCommand() => new SlashCommandBuilder()
        .WithName("slotsleaderboard")
        .WithDescription("Show the server slots leaderboard");

    public static SlashCommandBuilder CreatePayoutsCommand() => new SlashCommandBuilder()
        .WithName("slotspayouts")
        .WithDescription("Show all slot symbol payout multipliers");

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

        string lobbyId = string.Empty;
        MessageComponent view;
        ulong guildId = command.GuildId.Value;
        ulong userId = command.User.Id;
        string displayName = command.User is SocketGuildUser guildUser
            ? guildUser.DisplayName
            : command.User.Username;
        bool saveLeaderboard = false;

        lock (_syncRoot)
        {
            ExpireSessions();
            ReleaseUserMachine(guildId, userId);
            PlayerData player = GetPlayer(guildId, userId);

            if (TryCompleteRun(guildId, displayName, player, out saveLeaderboard))
            {
                view = BuildVictoryView(guildId, displayName, player);
            }
            else
            {
                lobbyId = CreateLobby(guildId, userId, displayName, player);
                view = BuildLobbyView(guildId, userId, lobbyId, batchIndex: 0);
            }
        }

        try
        {
            await command.RespondAsync(
                components: view,
                flags: MessageFlags.ComponentsV2);
        }
        finally
        {
            if (saveLeaderboard)
                _leaderboard.Save();
        }

        if (lobbyId.Length == 0)
            return;

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


    public async Task HandleSpeedSlashCommandAsync(SocketSlashCommand command)
    {
        if (!command.GuildId.HasValue)
        {
            await command.RespondAsync(
                "Slots can only be played inside a server.",
                ephemeral: true);
            return;
        }

        ulong guildId = command.GuildId.Value;
        ulong userId = command.User.Id;
        string displayName = command.User is SocketGuildUser guildUser
            ? guildUser.DisplayName
            : command.User.Username;
        SlotMachine speedMachine;

        lock (_syncRoot)
        {
            ExpireSessions();
            ReleaseSpeedMachine(guildId, userId);

            PlayerData player = GetSpeedPlayer(guildId, userId);
            player.ResetRun();
            speedMachine = CreateSpeedMachine(
                guildId,
                userId,
                displayName,
                player,
                MachineBatches[0]);
        }

        MessageComponent speedView = BuildInitialMachineView(command, speedMachine);

        await command.RespondAsync(
            components: speedView,
            flags: MessageFlags.ComponentsV2);

        try
        {
            IUserMessage responseMessage = await command.GetOriginalResponseAsync();

            lock (_syncRoot)
            {
                if (_speedMachines.TryGetValue((guildId, userId), out SlotMachine? current) &&
                    ReferenceEquals(current, speedMachine) &&
                    current.SessionId == speedMachine.SessionId)
                {
                    current.Message = responseMessage;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to track a speed slot message: {ex.Message}");
        }
    }

    public async Task HandleLeaderboardSlashCommandAsync(SocketSlashCommand command)
    {
        if (!command.GuildId.HasValue)
        {
            await command.RespondAsync(
                "The slots leaderboard can only be viewed inside a server.",
                ephemeral: true);
            return;
        }

        MessageComponent view;

        lock (_syncRoot)
        {
            view = BuildLeaderboardView(command.GuildId.Value);
        }

        await command.RespondAsync(
            components: view,
            flags: MessageFlags.ComponentsV2);
    }

    public async Task HandlePayoutsSlashCommandAsync(SocketSlashCommand command)
    {
        if (!command.GuildId.HasValue)
        {
            await command.RespondAsync(
                "Slot payouts can only be viewed inside a server.",
                ephemeral: true);
            return;
        }

        await command.RespondAsync(
            components: BuildPayoutsView(command),
            flags: MessageFlags.ComponentsV2);
    }

    private static MessageComponent BuildInitialMachineView(
        SocketSlashCommand command,
        SlotMachine machine)
    {
        PlayerData player = machine.Player!;
        ResolvedSlotSymbol[] symbols = GetResolvedSymbols(command, machine);
        ResolvedSlotSymbol[,] idleReels = BuildIdleReels(symbols);
        var idleWinningCells = new bool[3, 3];

        machine.LastReels = idleReels;
        machine.LastWinningCells = idleWinningCells;
        machine.LastResult = "Choose a bet when you're ready.";

        return BuildMachineView(
            machine,
            player,
            symbols,
            idleReels,
            idleWinningCells,
            machine.LastResult);
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

            case "reset":
                await HandleResetRunAsync(component, parts);
                break;

            case "nextroom":
                await HandleNextRoomAsync(component, parts);
                break;

            case "playagain":
                await HandlePlayAgainAsync(component, parts);
                break;

            case "share":
                await HandleShareResultAsync(component, parts);
                break;

            case "reel":
            case "lever":
                await component.DeferAsync();
                break;
        }
    }

    private async Task HandleNextRoomAsync(
        SocketMessageComponent component,
        string[] parts)
    {
        if (!TryParseMachineSession(parts, out int machineId, out string sessionId))
            return;

        ulong guildId = component.GuildId!.Value;
        ulong userId = component.User.Id;
        MessageComponent? view = null;
        bool invalidSession;
        bool nextRoomLocked = false;
        long requiredBalance = 0;
        long currentBalance = 0;

        lock (_syncRoot)
        {
            SlotMachine? machine = FindOwnedMachine(
                guildId,
                userId,
                machineId,
                sessionId);

            invalidSession = machine is null;

            if (machine is not null)
            {
                PlayerData player = machine.Player ??
                    (machine.IsSpeedMode
                        ? GetSpeedPlayer(guildId, userId)
                        : GetPlayer(guildId, userId));
                MachineBatch? nextBatch = GetBatch(machine.Batch.Index + 1);

                if (nextBatch is null || player.Balance < nextBatch.RequiredBalance)
                {
                    nextRoomLocked = true;
                    requiredBalance = nextBatch?.RequiredBalance ?? 0;
                    currentBalance = player.Balance;
                    machine.LastInteractionUtc = DateTimeOffset.UtcNow;
                    machine.Message = component.Message;
                }
                else
                {
                    string displayName =
                        machine.OwnerDisplayName ?? component.User.Username;

                    if (machine.IsSpeedMode)
                    {
                        ReleaseSpeedMachine(guildId, userId);
                        SlotMachine nextMachine = CreateSpeedMachine(
                            guildId,
                            userId,
                            displayName,
                            player,
                            nextBatch,
                            component.Message);

                        ResolvedSlotSymbol[] symbols = GetResolvedSymbols(
                            component,
                            nextMachine);
                        ResolvedSlotSymbol[,] idleReels = BuildIdleReels(symbols);
                        var idleWinningCells = new bool[3, 3];

                        nextMachine.LastReels = idleReels;
                        nextMachine.LastWinningCells = idleWinningCells;
                        nextMachine.LastResult = "Choose a bet when you're ready.";
                        view = BuildMachineView(
                            nextMachine,
                            player,
                            symbols,
                            idleReels,
                            idleWinningCells,
                            nextMachine.LastResult);
                    }
                    else
                    {
                        List<SlotMachine> machines = GetMachines(guildId);
                        int startIndex = nextBatch.Index * MachinesPerBatch;
                        int endIndex = startIndex + MachinesPerBatch;
                        SlotMachine? availableMachine = null;

                        for (int i = startIndex; i < endIndex; i++)
                        {
                            SlotMachine candidate = machines[i];

                            if (candidate.IsClaimed)
                                continue;

                            availableMachine = candidate;
                            break;
                        }

                        ReleaseMachine(machine);

                        if (availableMachine is not null &&
                            TryClaimMachine(
                                guildId,
                                userId,
                                displayName,
                                player,
                                availableMachine.Id,
                                out SlotMachine? claimedMachine))
                        {
                            SlotMachine nextMachine = claimedMachine!;
                            nextMachine.Message = component.Message;
                            nextMachine.Player = player;

                            ResolvedSlotSymbol[] symbols = GetResolvedSymbols(
                                component,
                                nextMachine);
                            ResolvedSlotSymbol[,] idleReels = BuildIdleReels(symbols);
                            var idleWinningCells = new bool[3, 3];

                            nextMachine.LastReels = idleReels;
                            nextMachine.LastWinningCells = idleWinningCells;
                            nextMachine.LastResult = "Choose a bet when you're ready.";
                            view = BuildMachineView(
                                nextMachine,
                                player,
                                symbols,
                                idleReels,
                                idleWinningCells,
                                nextMachine.LastResult);
                        }
                        else
                        {
                            string lobbyId = CreateLobby(
                                guildId,
                                userId,
                                displayName,
                                player,
                                component.Message);
                            view = BuildLobbyView(
                                guildId,
                                userId,
                                lobbyId,
                                nextBatch.Index);
                        }
                    }
                }
            }
        }

        if (invalidSession)
        {
            await RespondSessionExpiredAsync(component);
            return;
        }

        if (nextRoomLocked)
        {
            string message = requiredBalance > 0
                ? $"You need {FormatMoney(requiredBalance)} to enter the next room. " +
                  $"Your balance is {FormatMoney(currentBalance)}."
                : "There is no higher room.";
            await component.RespondAsync(message, ephemeral: true);
            return;
        }

        await UpdateMessageAsync(component, view!);
    }

    private async Task HandleResetRunAsync(
        SocketMessageComponent component,
        string[] parts)
    {
        if (!TryParseMachineSession(parts, out int machineId, out string sessionId))
            return;

        MessageComponent? view = null;
        bool invalidSession;
        bool normalModeReset = false;

        lock (_syncRoot)
        {
            ulong guildId = component.GuildId!.Value;
            ulong userId = component.User.Id;
            SlotMachine? machine = FindOwnedMachine(
                guildId,
                userId,
                machineId,
                sessionId);

            invalidSession = machine is null;

            if (machine is not null)
            {
                if (!machine.IsSpeedMode)
                {
                    // Reset Run is intentionally exclusive to /slotsspeed.
                    // This also prevents stale normal /slots messages from
                    // resetting a run after the button has been removed.
                    normalModeReset = true;
                }
                else
                {
                    PlayerData player = machine.Player ?? GetSpeedPlayer(guildId, userId);
                    string displayName = machine.OwnerDisplayName ?? component.User.Username;

                    ReleaseSpeedMachine(guildId, userId);
                    player.ResetRun();

                    SlotMachine restartedMachine = CreateSpeedMachine(
                        guildId,
                        userId,
                        displayName,
                        player,
                        MachineBatches[0],
                        component.Message);
                    ResolvedSlotSymbol[] symbols = GetResolvedSymbols(
                        component,
                        restartedMachine);
                    ResolvedSlotSymbol[,] idleReels = BuildIdleReels(symbols);
                    var idleWinningCells = new bool[3, 3];
                    restartedMachine.LastReels = idleReels;
                    restartedMachine.LastWinningCells = idleWinningCells;
                    restartedMachine.LastResult =
                        "Choose a bet when you're ready.";
                    view = BuildMachineView(
                        restartedMachine,
                        player,
                        symbols,
                        idleReels,
                        idleWinningCells,
                        restartedMachine.LastResult);
                }
            }
        }

        if (invalidSession)
        {
            await RespondSessionExpiredAsync(component);
            return;
        }

        if (normalModeReset)
        {
            await component.RespondAsync(
                "Reset Run is only available in `/slotsspeed`.",
                ephemeral: true);
            return;
        }

        await UpdateMessageAsync(component, view!);
    }

    private async Task HandlePlayAgainAsync(
        SocketMessageComponent component,
        string[] parts)
    {
        if (!TryParseVictoryAction(parts, out ulong victoryUserId, out bool speedMode))
            return;

        if (component.User.Id != victoryUserId)
        {
            await component.RespondAsync(
                "Only the winning player can start a new run.",
                ephemeral: true);
            return;
        }

        ulong guildId = component.GuildId!.Value;
        ulong userId = component.User.Id;
        string displayName = component.User is SocketGuildUser guildUser
            ? guildUser.DisplayName
            : component.User.Username;
        MessageComponent? view = null;

        lock (_syncRoot)
        {
            PlayerData player = speedMode
                ? GetSpeedPlayer(guildId, userId)
                : GetPlayer(guildId, userId);

            if (player.HasCompletedRun)
            {
                player.ResetRun();

                if (speedMode)
                {
                    ReleaseSpeedMachine(guildId, userId);
                    SlotMachine restartedMachine = CreateSpeedMachine(
                        guildId,
                        userId,
                        displayName,
                        player,
                        MachineBatches[0],
                        component.Message);
                    ResolvedSlotSymbol[] symbols = GetResolvedSymbols(
                        component,
                        restartedMachine);
                    ResolvedSlotSymbol[,] idleReels = BuildIdleReels(symbols);
                    var idleWinningCells = new bool[3, 3];
                    restartedMachine.LastReels = idleReels;
                    restartedMachine.LastWinningCells = idleWinningCells;
                    restartedMachine.LastResult = "Choose a bet when you're ready.";
                    view = BuildMachineView(
                        restartedMachine,
                        player,
                        symbols,
                        idleReels,
                        idleWinningCells,
                        restartedMachine.LastResult);
                }
                else
                {
                    ReleaseUserMachine(guildId, userId);
                    CloseLobby(guildId, userId);
                    string lobbyId = CreateLobby(
                        guildId,
                        userId,
                        displayName,
                        player,
                        component.Message);
                    view = BuildLobbyView(
                        guildId,
                        userId,
                        lobbyId,
                        batchIndex: 0);
                }
            }
        }

        if (view is null)
        {
            await component.RespondAsync(
                "That completed run is no longer available.",
                ephemeral: true);
            return;
        }

        await UpdateMessageAsync(component, view);
    }

    private async Task HandleShareResultAsync(
        SocketMessageComponent component,
        string[] parts)
    {
        if (!TryParseVictoryAction(parts, out ulong victoryUserId, out bool speedMode))
            return;

        if (component.User.Id != victoryUserId)
        {
            await component.RespondAsync(
                "Only the winning player can share this result.",
                ephemeral: true);
            return;
        }

        MessageComponent? sharedView = null;

        lock (_syncRoot)
        {
            PlayerData player = speedMode
                ? GetSpeedPlayer(component.GuildId!.Value, component.User.Id)
                : GetPlayer(component.GuildId!.Value, component.User.Id);

            if (player.HasCompletedRun)
            {
                string displayName = component.User is SocketGuildUser guildUser
                    ? guildUser.DisplayName
                    : component.User.Username;
                sharedView = BuildSharedVictoryView(
                    component.GuildId.Value,
                    displayName,
                    player);
            }
        }

        if (sharedView is null)
        {
            await component.RespondAsync(
                "That completed run is no longer available.",
                ephemeral: true);
            return;
        }

        await component.RespondAsync(
            "Sharing your victory result…",
            ephemeral: true);
        await component.Channel.SendMessageAsync(
            components: sharedView,
            flags: MessageFlags.ComponentsV2);
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
                ResolvedSlotSymbol[] symbols = GetResolvedSymbols(
                    component,
                    claimedMachine);
                ResolvedSlotSymbol[,] idleReels = BuildIdleReels(symbols);
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
        bool saveLeaderboard = false;

        lock (_syncRoot)
        {
            SlotMachine? machine = FindOwnedMachine(
                component.GuildId!.Value,
                component.User.Id,
                machineId,
                sessionId);

            invalidSession = machine is null;

            if (machine is not null)
            {
                PlayerData player = machine.Player ?? GetPlayer(
                    component.GuildId.Value,
                    component.User.Id);
                player.SellOrgan();
                machine.LastInteractionUtc = DateTimeOffset.UtcNow;
                machine.Message = component.Message;

                string displayName =
                    machine.OwnerDisplayName ?? component.User.Username;

                if (TryCompleteRun(
                        component.GuildId.Value,
                        displayName,
                        player,
                        out saveLeaderboard))
                {
                    view = BuildVictoryView(
                        component.GuildId.Value,
                        displayName,
                        player,
                        machine.IsSpeedMode);
                    if (machine.IsSpeedMode)
                        ReleaseSpeedMachine(component.GuildId.Value, component.User.Id);
                    else
                        ReleaseMachine(machine);
                }
                else
                {
                    machine.LastResult =
                        $"🫀 Sold an organ for {FormatMoney(PlayerData.OrganSaleValue)}.";

                    ResolvedSlotSymbol[] symbols = GetResolvedSymbols(
                        component,
                        machine);
                    view = BuildMachineView(
                        machine,
                        player,
                        symbols,
                        machine.LastReels ?? BuildIdleReels(symbols),
                        machine.LastWinningCells ?? new bool[3, 3],
                        machine.LastResult);
                }
            }
        }

        if (invalidSession)
        {
            await RespondSessionExpiredAsync(component);
            return;
        }

        try
        {
            await UpdateMessageAsync(component, view!);
        }
        finally
        {
            if (saveLeaderboard)
                _leaderboard.Save();
        }
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
        bool saveLeaderboard = false;

        lock (_syncRoot)
        {
            invalidLobby = !TryGetCurrentLobby(
                guildId,
                component.User.Id,
                lobbyId,
                out LobbySession? lobby);

            if (!invalidLobby)
            {
                TouchLobby(
                    guildId,
                    component.User.Id,
                    lobbyId,
                    component.Message);
                PlayerData player = GetPlayer(guildId, component.User.Id);
                player.SellOrgan();

                if (TryCompleteRun(
                        guildId,
                        lobby!.DisplayName,
                        player,
                        out saveLeaderboard))
                {
                    CloseLobby(guildId, component.User.Id);
                    view = BuildVictoryView(guildId, lobby.DisplayName, player);
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

        try
        {
            await UpdateMessageAsync(component, view!);
        }
        finally
        {
            if (saveLeaderboard)
                _leaderboard.Save();
        }
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
        bool saveLeaderboard = false;
        long requiredBalance = 0;
        long currentBalance = 0;

        lock (_syncRoot)
        {
            SlotMachine? machine = FindOwnedMachine(
                component.GuildId!.Value,
                component.User.Id,
                machineId,
                sessionId);

            invalidSession = machine is null;

            if (machine is not null)
            {
                PlayerData player = machine.Player ?? GetPlayer(component.GuildId.Value, component.User.Id);

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
                    ResolvedSlotSymbol[] symbols = GetResolvedSymbols(
                        component,
                        machine);
                    SpinResult spin = Spin(
                        machine,
                        player,
                        betAmount,
                        symbols);

                    string displayName =
                        machine.OwnerDisplayName ?? component.User.Username;

                    if (TryCompleteRun(
                            component.GuildId.Value,
                            displayName,
                            player,
                            out saveLeaderboard))
                    {
                        view = BuildVictoryView(
                            component.GuildId.Value,
                            displayName,
                            player,
                            machine.IsSpeedMode);
                        if (machine.IsSpeedMode)
                            ReleaseSpeedMachine(component.GuildId.Value, component.User.Id);
                        else
                            ReleaseMachine(machine);
                    }
                    else
                    {
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

        try
        {
            await UpdateMessageAsync(component, view!);
        }
        finally
        {
            if (saveLeaderboard)
                _leaderboard.Save();
        }
    }

    private async Task HandleLeaveAsync(SocketMessageComponent component, string[] parts)
    {
        if (!TryParseMachineSession(parts, out int machineId, out string sessionId))
            return;

        MessageComponent? view = null;
        bool invalidSession;

        lock (_syncRoot)
        {
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
                if (machine.IsSpeedMode)
                    ReleaseSpeedMachine(component.GuildId.Value, component.User.Id);
                else
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
        bool speedModeNoLobby = false;

        lock (_syncRoot)
        {
            SlotMachine? machine = FindOwnedMachine(guildId, userId, machineId, sessionId);
            invalidSession = machine is null;

            if (machine is not null)
            {
                if (machine.IsSpeedMode)
                {
                    machine.LastInteractionUtc = DateTimeOffset.UtcNow;
                    machine.Message = component.Message;
                    speedModeNoLobby = true;
                }
                else
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
        }

        if (speedModeNoLobby)
        {
            await component.RespondAsync(
                "Speed mode has no lobby. Progress using **Next Room**, or reset the run to return to the Chud Room.",
                ephemeral: true);
            return;
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
        if (!_activeLobbies.TryGetValue((guildId, userId), out lobby) ||
            lobby.Id != lobbyId)
        {
            return false;
        }

        return !IsExpired(lobby.LastInteractionUtc);
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

        List<SlotMachine> machines = GetMachines(guildId);
        int machineStartIndex = batch.Index * MachinesPerBatch;
        int machineEndIndex = machineStartIndex + MachinesPerBatch;

        for (int i = machineStartIndex; i < machineEndIndex; i++)
        {
            SlotMachine machine = machines[i];

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
        IReadOnlyList<ResolvedSlotSymbol> symbols,
        ResolvedSlotSymbol[,] reels,
        bool[,] winningCells,
        string? result)
    {
        var navigationButtons = new ActionRowBuilder();

        if (!machine.IsSpeedMode)
        {
            navigationButtons.WithButton(
                "Choose Machine",
                $"slots:lobby:{machine.Id}:{machine.SessionId}",
                ButtonStyle.Secondary,
                emote: new Emoji("↩️"));
        }

        navigationButtons.WithButton(
            "Sell Organs",
            $"slots:sell:{machine.Id}:{machine.SessionId}",
            ButtonStyle.Danger,
            emote: new Emoji("🫀"));

        if (machine.IsSpeedMode)
        {
            navigationButtons.WithButton(
                "Reset Run",
                $"slots:reset:{machine.Id}:{machine.SessionId}",
                ButtonStyle.Danger,
                emote: new Emoji("💣"));
        }

        navigationButtons.WithButton(
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

        MachineBatch? nextBatch = GetBatch(machine.Batch.Index + 1);
        bool canEnterNextRoom = nextBatch is not null &&
            player.Balance >= nextBatch.RequiredBalance;

        if (canEnterNextRoom)
        {
            betButtons.WithButton(
                "Next Room",
                $"slots:nextroom:{machine.Id}:{machine.SessionId}",
                ButtonStyle.Success,
                emote: new Emoji("➡️"));
        }
        else
        {
            betButtons.WithButton(
                "All In",
                $"slots:roll:{machine.Id}:{machine.SessionId}:all",
                ButtonStyle.Danger,
                disabled: player.Balance <= 0);
        }

        string linePayouts = machine.LinePayouts ??= BuildLinePayouts(symbols);

        string machineInformation =
            $"**Line Payouts:** {linePayouts}\n" +
            $"**Multi-Line Bonus:** +{AdditionalLineBonusPercent}% per extra line\n" +
            $"**Machine Stats:** {machine.TotalRolls} rolls • {machine.TotalWins} wins";

        string machineTitle = machine.IsSpeedMode
            ? "Slot Machine"
            : $"Slot Machine {machine.Number}";

        var container = new ContainerBuilder()
            .WithAccentColor(machine.Batch.AccentColor)
            .WithTextDisplay(
                $"**Player:** {machine.OwnerDisplayName ?? "Unknown player"} • " +
                $"**Organs Sold:** {player.OrgansSold}\n" +
                $"**Balance:** {FormatMoney(player.Balance)}")
            .WithActionRow(navigationButtons)
            .WithTextDisplay($"## 🎰 {machineTitle} 🎰\n\n")
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
            .WithTextDisplay($"**Balance:** {FormatMoney(player.Balance)}\n" + "**Next Spin Bet:**")
            .WithActionRow(betButtons);

        return new ComponentBuilderV2()
            .WithContainer(container)
            .Build();
    }


    private static string BuildLinePayouts(
        IReadOnlyList<ResolvedSlotSymbol> symbols)
    {
        var builder = new StringBuilder(64);

        for (int i = 0; i < symbols.Count; i++)
        {
            if (i > 0)
                builder.Append(" • ");

            ResolvedSlotSymbol symbol = symbols[i];
            builder.Append(symbol.DisplayEmoji);
            builder.Append(" ×");
            builder.Append(symbol.LineMultiplier);
        }

        return builder.ToString();
    }

    private static ActionRowBuilder BuildReelRow(
        SlotMachine machine,
        ResolvedSlotSymbol[,] reels,
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
                    .WithEmote(ParseReelEmote(
                        reels[row, column].DisplayEmoji)));
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

    private static ResolvedSlotSymbol[,] BuildIdleReels(
        IReadOnlyList<ResolvedSlotSymbol> symbols) =>
        new[,]
        {
            { symbols[0], symbols[1], symbols[2] },
            { symbols[4], symbols[3], symbols[0] },
            { symbols[2], symbols[4], symbols[1] }
        };

    private static ResolvedSlotSymbol[] GetResolvedSymbols(
        SocketMessageComponent component,
        SlotMachine machine)
    {
        if (machine.ResolvedSymbols is not null)
            return machine.ResolvedSymbols;

        machine.ResolvedSymbols = ResolveSymbols(
            (component.Channel as SocketGuildChannel)?.Guild,
            machine.Batch);
        return machine.ResolvedSymbols;
    }

    private static ResolvedSlotSymbol[] GetResolvedSymbols(
        SocketSlashCommand command,
        SlotMachine machine)
    {
        if (machine.ResolvedSymbols is not null)
            return machine.ResolvedSymbols;

        machine.ResolvedSymbols = ResolveSymbols(
            (command.Channel as SocketGuildChannel)?.Guild,
            machine.Batch);
        return machine.ResolvedSymbols;
    }

    private static ResolvedSlotSymbol[] ResolveSymbols(
        SocketGuild? guild,
        MachineBatch batch)
    {
        int[] symbolIndexes = RoomSymbolIndexes[batch.Index];
        var symbols = new ResolvedSlotSymbol[symbolIndexes.Length];

        for (int i = 0; i < symbolIndexes.Length; i++)
            symbols[i] = ResolveSymbol(guild, SymbolDefinitions[symbolIndexes[i]]);

        return symbols;
    }

    private static ResolvedSlotSymbol ResolveSymbol(
        SocketGuild? guild,
        SlotSymbol symbol)
    {
        string displayEmoji = symbol.FallbackEmoji;
        ulong customEmojiId = GetCustomEmojiId(guild, symbol);

        if (customEmojiId != 0 && guild is not null)
        {
            foreach (GuildEmote customEmoji in guild.Emotes)
            {
                if (customEmoji.Id != customEmojiId)
                    continue;

                if (customEmoji.IsAvailable == true)
                    displayEmoji = customEmoji.ToString();

                break;
            }
        }

        return new ResolvedSlotSymbol(
            displayEmoji,
            symbol.LineMultiplier);
    }

    private static ulong GetCustomEmojiId(
        SocketGuild? guild,
        SlotSymbol symbol)
    {
        if (guild is not null &&
            GuildCustomEmojiIds.TryGetValue(
                guild.Id,
                out Dictionary<SlotSymbolKey, ulong>? guildEmojiIds) &&
            guildEmojiIds.TryGetValue(symbol.Key, out ulong guildEmojiId))
        {
            return guildEmojiId;
        }

        return 0;
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
        long balance,
        string? detail = null) =>
        new ComponentBuilderV2()
            .WithContainer(container => container
                .WithAccentColor(new Color(100, 100, 100))
                .WithTextDisplay(
                    $"{displayName} has left the casino with a balance of {FormatMoney(balance)}." +
                    (detail is null ? string.Empty : $"\n{detail}")))
            .Build();

    private MessageComponent BuildLeaderboardView(ulong guildId)
    {
        string leaderboard = BuildLeaderboardText(guildId, 10);

        return new ComponentBuilderV2()
            .WithContainer(container => container
                .WithAccentColor(new Color(241, 196, 15))
                .WithTextDisplay(
                    "# 🏆 Slots Leaderboard 🏆\n" +
                    "Ranked by **fewest spins**, then **fewest organs sold**. " +
                    "Exact ties go to whoever set the record first.\n\n" +
                    leaderboard))
            .Build();
    }

    private static MessageComponent BuildPayoutsView(SocketSlashCommand command)
    {
        SocketGuild? guild = (command.Channel as SocketGuildChannel)?.Guild;
        var symbols = new ResolvedSlotSymbol[SymbolDefinitions.Length];

        for (int i = 0; i < SymbolDefinitions.Length; i++)
            symbols[i] = ResolveSymbol(guild, SymbolDefinitions[i]);

        Array.Sort(
            symbols,
            static (left, right) => left.LineMultiplier.CompareTo(right.LineMultiplier));

        var payoutsBuilder = new StringBuilder(256);

        for (int i = 0; i < symbols.Length; i++)
        {
            if (i > 0)
                payoutsBuilder.Append('\n');

            ResolvedSlotSymbol symbol = symbols[i];
            payoutsBuilder.Append(symbol.DisplayEmoji);
            payoutsBuilder.Append("  **×");
            payoutsBuilder.Append(symbol.LineMultiplier);
            payoutsBuilder.Append("**");
        }

        return new ComponentBuilderV2()
            .WithContainer(container => container
                .WithAccentColor(new Color(52, 152, 219))
                .WithTextDisplay(
                    "# 🎰 Slot Payouts\n" +
                    "Each multiplier applies to a completed winning line.\n\n" +
                    payoutsBuilder +
                    $"\n\n**Multi-Line Bonus:** +{AdditionalLineBonusPercent}% per extra winning line"))
            .Build();
    }

    private string BuildLeaderboardText(ulong guildId, int maxEntries)
    {
        string[] medals = ["🥇", "🥈", "🥉"];
        IReadOnlyList<SlotLeaderboardEntry> leaders =
            _leaderboard.GetTopEntries(guildId, maxEntries);

        if (leaders.Count == 0)
            return "No completed runs yet.";

        var builder = new StringBuilder(maxEntries <= 3 ? 256 : 768);

        for (int i = 0; i < leaders.Count; i++)
        {
            if (i > 0)
                builder.Append('\n');

            string rankEmoji = i < medals.Length
                ? medals[i]
                : "🎰";

            builder.Append(rankEmoji);
            builder.Append(' ');
            builder.Append(i + 1);
            builder.Append(". **");

            SlotLeaderboardEntry entry = leaders[i];
            builder.Append(entry.DisplayName);
            builder.Append("** — **Spins:** ");
            builder.Append(entry.TotalSpins.ToString("N0", CultureInfo.InvariantCulture));
            builder.Append(" | **Time:** ");
            builder.Append(FormatDuration(entry.Duration));
            builder.Append(" | **Organs Sold:** ");
            builder.Append(entry.OrgansSold.ToString("N0", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private MessageComponent BuildVictoryView(
        ulong guildId,
        string displayName,
        PlayerData player,
        bool speedMode = false)
    {
        var actions = new ActionRowBuilder()
            .WithButton(
                "Play Again",
                $"slots:playagain:{player.UserId}:{(speedMode ? "speed" : "normal")}",
                ButtonStyle.Primary,
                emote: new Emoji("🔄"))
            .WithButton(
                "Share Result",
                $"slots:share:{player.UserId}:{(speedMode ? "speed" : "normal")}",
                ButtonStyle.Success,
                emote: new Emoji("📣"));

        var container = new ContainerBuilder()
            .WithAccentColor(new Color(241, 196, 15))
            .WithTextDisplay(BuildVictoryText(guildId, displayName, player))
            .WithActionRow(actions);

        return new ComponentBuilderV2()
            .WithContainer(container)
            .Build();
    }

    private MessageComponent BuildSharedVictoryView(
        ulong guildId,
        string displayName,
        PlayerData player) =>
        new ComponentBuilderV2()
            .WithContainer(container => container
                .WithAccentColor(new Color(241, 196, 15))
                .WithTextDisplay(BuildVictoryText(guildId, displayName, player)))
            .Build();

    private string BuildVictoryText(
        ulong guildId,
        string displayName,
        PlayerData player)
    {
        string leaderboard = BuildLeaderboardText(guildId, 3);

        return "# 🏆 Congratulations! 🏆\n" +
            $"**{displayName}, you are now a trillionaire!**\n\n" +
            $"**Final Balance:** {FormatMoney(player.Balance)}\n" +
            "## Run Statistics\n" +
            $"**Time Taken:** {FormatDuration(player.RunDuration)}\n" +
            $"**Total Spins:** {player.TotalSpins.ToString("N0", CultureInfo.InvariantCulture)}\n" +
            $"**Organs Sold:** {player.OrgansSold.ToString("N0", CultureInfo.InvariantCulture)}\n\n" +
            "## Server Leaderboard\n" +
            leaderboard;
    }

    private bool TryCompleteRun(
        ulong guildId,
        string displayName,
        PlayerData player,
        out bool leaderboardChanged)
    {
        leaderboardChanged = false;
        bool wasAlreadyComplete = player.HasCompletedRun;

        if (!player.TryCompleteRun(DateTimeOffset.UtcNow))
            return false;

        if (!wasAlreadyComplete && player.CompletedUtc.HasValue)
        {
            leaderboardChanged = _leaderboard.RecordPersonalBest(
                guildId,
                player.UserId,
                displayName,
                player.RunDuration,
                player.CompletedUtc.Value,
                player.TotalSpins,
                player.OrgansSold);
        }

        return true;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d " +
                $"{duration.Hours:D2}h {duration.Minutes:D2}m " +
                $"{duration.Seconds:D2}.{duration.Milliseconds:D3}s";
        }

        return $"{duration.Hours:D2}h {duration.Minutes:D2}m " +
            $"{duration.Seconds:D2}.{duration.Milliseconds:D3}s";
    }

    private PlayerData GetPlayer(ulong guildId, ulong userId)
    {
        var key = (guildId, userId);

        if (_players.TryGetValue(key, out PlayerData? player))
            return player;

        player = new PlayerData(userId);
        _players[key] = player;
        return player;
    }

    private PlayerData GetSpeedPlayer(ulong guildId, ulong userId)
    {
        var key = (guildId, userId);

        if (_speedPlayers.TryGetValue(key, out PlayerData? player))
            return player;

        player = new PlayerData(userId);
        _speedPlayers[key] = player;
        return player;
    }

    private static MachineBatch? GetBatch(int batchIndex) =>
        (uint)batchIndex < (uint)MachineBatches.Length
            ? MachineBatches[batchIndex]
            : null;

    private static MachineBatch GetAccessibleBatch(
        PlayerData player,
        int preferredBatchIndex)
    {
        MachineBatch? preferredBatch = GetBatch(preferredBatchIndex);

        if (preferredBatch is not null && player.Balance >= preferredBatch.RequiredBalance)
            return preferredBatch;

        return MachineBatches[0];
    }

    private static bool IsExpired(DateTimeOffset lastInteractionUtc) =>
        lastInteractionUtc != default &&
        DateTimeOffset.UtcNow - lastInteractionUtc >= SessionTimeout;

    private List<SlotMachine> GetMachines(ulong guildId)
    {
        if (_machinesByGuild.TryGetValue(guildId, out List<SlotMachine>? machines))
            return machines;

        int totalMachines = MachineBatches.Length * MachinesPerBatch;
        machines = new List<SlotMachine>(totalMachines);

        for (int batchIndex = 0; batchIndex < MachineBatches.Length; batchIndex++)
        {
            MachineBatch batch = MachineBatches[batchIndex];

            for (int number = 1; number <= MachinesPerBatch; number++)
            {
                machines.Add(new SlotMachine(
                    batch.Index * MachinesPerBatch + number,
                    number,
                    batch));
            }
        }

        _machinesByGuild[guildId] = machines;
        return machines;
    }

    private static SlotMachine? GetMachineById(
        List<SlotMachine> machines,
        int machineId)
    {
        int index = machineId - 1;
        return (uint)index < (uint)machines.Count
            ? machines[index]
            : null;
    }

    private bool TryClaimMachine(
        ulong guildId,
        ulong userId,
        string userDisplayName,
        PlayerData player,
        int machineId,
        out SlotMachine? machine)
    {
        machine = GetMachineById(GetMachines(guildId), machineId);

        if (machine is null || player.Balance < machine.Batch.RequiredBalance)
            return false;

        if (machine.IsClaimed)
            return false;

        machine.OwnerUserId = userId;
        machine.OwnerDisplayName = userDisplayName;
        machine.SessionId = Guid.NewGuid().ToString("N");
        machine.LastInteractionUtc = DateTimeOffset.UtcNow;
        machine.Player = player;
        return true;
    }

    private SlotMachine? FindOwnedMachine(
        ulong guildId,
        ulong userId,
        int machineId,
        string sessionId)
    {
        if (_speedMachines.TryGetValue((guildId, userId), out SlotMachine? speedMachine) &&
            speedMachine.Id == machineId &&
            speedMachine.SessionId == sessionId &&
            !IsExpired(speedMachine.LastInteractionUtc))
        {
            return speedMachine;
        }

        SlotMachine? machine = GetMachineById(GetMachines(guildId), machineId);

        if (machine is null || IsExpired(machine.LastInteractionUtc))
            return null;

        return machine.OwnerUserId == userId && machine.SessionId == sessionId
            ? machine
            : null;
    }

    private SlotMachine CreateSpeedMachine(
        ulong guildId,
        ulong userId,
        string displayName,
        PlayerData player,
        MachineBatch batch,
        IUserMessage? message = null)
    {
        var machine = new SlotMachine(
            10_000 + batch.Index,
            1,
            batch)
        {
            OwnerUserId = userId,
            OwnerDisplayName = displayName,
            SessionId = Guid.NewGuid().ToString("N"),
            LastInteractionUtc = DateTimeOffset.UtcNow,
            Message = message,
            Player = player,
            IsSpeedMode = true
        };

        _speedMachines[(guildId, userId)] = machine;
        return machine;
    }

    private void ReleaseSpeedMachine(ulong guildId, ulong userId)
    {
        if (!_speedMachines.Remove((guildId, userId), out SlotMachine? machine))
            return;

        ReleaseMachine(machine);
    }

    private void ReleaseUserMachine(ulong guildId, ulong userId)
    {
        List<SlotMachine> machines = GetMachines(guildId);

        for (int i = 0; i < machines.Count; i++)
        {
            SlotMachine machine = machines[i];

            if (machine.OwnerUserId != userId)
                continue;

            ReleaseMachine(machine);
            return;
        }
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
        machine.ResolvedSymbols = null;
        machine.LinePayouts = null;
        machine.IsSpeedMode = false;
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

    private static bool TryParseVictoryAction(
        string[] parts,
        out ulong userId,
        out bool speedMode)
    {
        userId = 0;
        speedMode = false;

        if ((parts.Length != 3 && parts.Length != 4) ||
            !ulong.TryParse(parts[2], out userId))
        {
            return false;
        }

        speedMode = parts.Length == 4 && parts[3] == "speed";
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

        if (!long.TryParse(betToken, out betAmount))
            return false;

        long[] validBets = machine.Batch.BetAmounts;

        for (int i = 0; i < validBets.Length; i++)
        {
            if (validBets[i] == betAmount)
                return true;
        }

        return false;
    }

    private static SpinResult Spin(
        SlotMachine machine,
        PlayerData player,
        long betAmount,
        IReadOnlyList<ResolvedSlotSymbol> symbols)
    {
        if (!player.TrySpend(betAmount))
            throw new InvalidOperationException("The player cannot afford this spin.");

        DateTimeOffset spinTime = DateTimeOffset.UtcNow;
        player.RecordSpin(spinTime);

        var slots = new ResolvedSlotSymbol[3, 3];
        int symbolCount = symbols.Count;

        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
                slots[row, column] = symbols[Random.Shared.Next(symbolCount)];
        }

        var winningCells = new bool[3, 3];
        int winningLineCount = 0;
        int baseMultiplier = 0;

        void CheckLine(
            int row1, int column1,
            int row2, int column2,
            int row3, int column3)
        {
            ResolvedSlotSymbol symbol = slots[row1, column1];

            if (symbol != slots[row2, column2] ||
                symbol != slots[row3, column3])
            {
                return;
            }

            winningLineCount++;
            baseMultiplier += symbol.LineMultiplier;
            winningCells[row1, column1] = true;
            winningCells[row2, column2] = true;
            winningCells[row3, column3] = true;
        }

        for (int row = 0; row < 3; row++)
            CheckLine(row, 0, row, 1, row, 2);

        for (int column = 0; column < 3; column++)
            CheckLine(0, column, 1, column, 2, column);

        CheckLine(0, 0, 1, 1, 2, 2);
        CheckLine(0, 2, 1, 1, 2, 0);

        bool winner = winningLineCount > 0;

        machine.TotalRolls++;
        machine.TotalWins += winner ? 1 : 0;
        machine.LastInteractionUtc = spinTime;

        long payout = 0;
        decimal effectiveMultiplier = 0;

        if (winner)
        {
            int bonusPercent = 100 +
                ((winningLineCount - 1) * AdditionalLineBonusPercent);

            effectiveMultiplier = baseMultiplier * bonusPercent / 100m;
            payout = checked((long)(betAmount * effectiveMultiplier));
            player.Credit(payout);
        }

        string result = winner
            ? $"🎉 **{winningLineCount} winning " +
              $"{(winningLineCount == 1 ? "line" : "lines")}! " +
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

        foreach (List<SlotMachine> machines in _machinesByGuild.Values)
        {
            for (int i = 0; i < machines.Count; i++)
            {
                SlotMachine machine = machines[i];

                if (!machine.IsClaimed || now - machine.LastInteractionUtc < SessionTimeout)
                    continue;

                IUserMessage? message = machine.Message;
                string displayName = machine.OwnerDisplayName ?? "The player";
                long balance = machine.Player?.Balance ?? 0;
                ReleaseMachine(machine);

                if (message is not null)
                    _ = UpdateExpiredMessageAsync(message, displayName, balance);
            }
        }

        List<(ulong GuildId, ulong UserId)>? expiredSpeedKeys = null;

        foreach (var entry in _speedMachines)
        {
            if (now - entry.Value.LastInteractionUtc < SessionTimeout)
                continue;

            expiredSpeedKeys ??= [];
            expiredSpeedKeys.Add(entry.Key);
        }

        if (expiredSpeedKeys is not null)
        {
            for (int i = 0; i < expiredSpeedKeys.Count; i++)
            {
                var key = expiredSpeedKeys[i];

                if (!_speedMachines.Remove(key, out SlotMachine? machine))
                    continue;

                IUserMessage? message = machine.Message;
                string displayName = machine.OwnerDisplayName ?? "The player";
                long balance = machine.Player?.Balance ?? 0;
                ReleaseMachine(machine);

                if (message is not null)
                    _ = UpdateExpiredMessageAsync(message, displayName, balance);
            }
        }

        List<(ulong GuildId, ulong UserId)>? expiredLobbyKeys = null;

        foreach (var entry in _activeLobbies)
        {
            if (now - entry.Value.LastInteractionUtc < SessionTimeout)
                continue;

            expiredLobbyKeys ??= [];
            expiredLobbyKeys.Add(entry.Key);
        }

        if (expiredLobbyKeys is null)
            return;

        for (int i = 0; i < expiredLobbyKeys.Count; i++)
        {
            var key = expiredLobbyKeys[i];

            if (!_activeLobbies.Remove(key, out LobbySession? lobby))
                continue;

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

    internal sealed record SpinResult(
        ResolvedSlotSymbol[,] Reels,
        bool[,] WinningCells,
        string Result);


    internal enum SlotSymbolKey
    {
        Custom1,
        Custom2,
        Custom3,
        Custom4,
        Custom5,
        Custom6,
        Custom7,
        Custom8,
        Custom9,
        Custom10,
        Custom11,
        Custom12,
        Custom13,
        Custom14
    }

    internal sealed record SlotSymbol(
        SlotSymbolKey Key,
        string FallbackEmoji,
        int LineMultiplier);

    internal sealed record ResolvedSlotSymbol(
        string DisplayEmoji,
        int LineMultiplier);

    internal sealed class LobbySession(
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

    internal sealed record MachineBatch(
        int Index,
        long RequiredBalance,
        long[] BetAmounts,
        string ButtonLabel,
        string ColorEmoji,
        Color AccentColor);

    internal sealed class SlotMachine(
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
        public ResolvedSlotSymbol[,]? LastReels { get; set; }
        public bool[,]? LastWinningCells { get; set; }
        public string? LastResult { get; set; }
        public ResolvedSlotSymbol[]? ResolvedSymbols { get; set; }
        public string? LinePayouts { get; set; }
        public int TotalRolls { get; set; }
        public int TotalWins { get; set; }
        public bool IsSpeedMode { get; set; }
        public bool IsClaimed => OwnerUserId.HasValue;
    }
}
