using Discord;
using Discord.WebSocket;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace NuttyBot;

internal sealed class DailySlotsGame
{
    private const int MaxSelectedBuffs = 4;
    private const int DailyBuffOfferCount = 10;
    // Buff rotation playtest control. The server reads the seed from this text file
    // every time a new Daily Slots session is opened, so you can change the available
    // 10-buff offer without restarting the bot. If the file is missing or invalid,
    // BuffRotationFallbackSeed is used instead.
    private const int BuffRotationFallbackSeed = 1;
    private const string BuffRotationSeedFileName = "daily-slots-buff-seed.txt";
    private const int BoardSize = 5;
    private const int MaxSpins = 10;
    private const int WildSymbolIndex = -2;
    private const int BaseJackpotMultiplier = 100;
    private const int JackpotBoostMultiplier = 5;
    private const int BaseSolutionRevealReward = 20;
    private const int CherrySymbolIndex = 0;
    private const int LemonSymbolIndex = 1;
    private const int WatermelonSymbolIndex = 4;

    // Discord Components V2 allows 40 total components per message. The game
    // view deliberately uses exactly 40:
    //   1 outer container
    //   2 text displays (header/payouts + balance/status)
    //   7 action rows
    //   30 buttons (25 board + 4 buffs + 1 spin)
    // The solution board is therefore rendered as emoji text while the slot
    // machine remains a fully interactive 5x5 button grid.

    private static readonly string[] DailySymbols =
    [
        "🍒", "🍋", "🍊", "🍇", "🍉"
    ];

    // Every symbol shown on a normal spin starts with this payout value. Jackpot
    // and symbol-value buffs can modify these values for the current run.
    private static readonly int[] SymbolPayoutValues =
    [
        3, 5, 8, 13, 21
    ];

    private static readonly DailyBuffDefinition[] BuffDefinitions =
    [
        new(
            "row-left",
            "Shift Row Left",
            "⬅️",
            100,
            2,
            DailyBuffTargetMode.Cell,
            "Shift the selected row left by one. New solution matches are revealed."),
        new(
            "row-right",
            "Shift Row Right",
            "➡️",
            100,
            2,
            DailyBuffTargetMode.Cell,
            "Shift the selected row right by one. New solution matches are revealed."),
        new(
            "column-up",
            "Shift Column Up",
            "⬆️",
            100,
            2,
            DailyBuffTargetMode.Cell,
            "Shift the selected column up by one. New solution matches are revealed."),
        new(
            "column-down",
            "Shift Column Down",
            "⬇️",
            100,
            2,
            DailyBuffTargetMode.Cell,
            "Shift the selected column down by one. New solution matches are revealed."),
        new(
            "reroll",
            "Reroll Symbol",
            "🎲",
            150,
            3,
            DailyBuffTargetMode.Cell,
            "Reroll one selected symbol and check against the solution."),
        new(
            "lucky-spin",
            "Lucky Spin",
            "🍀",
            300,
            2,
            DailyBuffTargetMode.None,
            "After the next spin resolves, reveal one unsolved solution symbol. Stackable."),
        new(
            "wild",
            "Wildcard",
            "🃏",
            500,
            1,
            DailyBuffTargetMode.Cell,
            "Instantly solves the selected position and turns the current symbol into a Wildcard. A Wildcard symbol can complete a Jackpot."),
        new(
            "double-payout",
            "Double Payout",
            "💵",
            200,
            1,
            DailyBuffTargetMode.None,
            "Double all slot and Jackpot money earned when you press Spin next."),
        new(
            "jackpot-boost",
            "Jackpot Boost",
            "💰",
            555,
            1,
            DailyBuffTargetMode.None,
            $"The next new Jackpot payout is multiplied by {JackpotBoostMultiplier}."),
        new(
            "perfect-spin",
            "Perfect Spin",
            "🌟",
            6_000,
            1,
            DailyBuffTargetMode.None,
            "Your next spin exactly matches the daily solution board."),

        new(
            "cash-injection",
            "Cash Injection",
            "💸",
            0,
            1,
            DailyBuffTargetMode.None,
            "Gain $1,000 immediately."),
        new(
            "insider-tip",
            "Insider Tip",
            "🕵️",
            200,
            3,
            DailyBuffTargetMode.Cell,
            "Change a selected slot symbol to that position's true solution symbol without revealing the solution cell."),
        new(
            "compound-interest",
            "Compound Interest",
            "📈",
            300,
            1,
            DailyBuffTargetMode.None,
            "All future slot and Jackpot payouts from Spin are increased by 50% for this run."),
        new(
            "four-kind-jackpots",
            "Four of a Kind",
            "4️⃣",
            500,
            1,
            DailyBuffTargetMode.None,
            "A contiguous 4-in-a-row now counts as a Jackpot."),
        new(
            "mega-jackpots",
            "Mega Jackpots",
            "🚀",
            400,
            1,
            DailyBuffTargetMode.None,
            "Increase the base Jackpot multiplier from x100 to x400 for the rest of the run."),
        new(
            "cherry-premium",
            "Cherry Premium",
            "🍒",
            200,
            1,
            DailyBuffTargetMode.None,
            "Cherries are worth $30 for the rest of the run."),
        new(
            "melon-scrambler",
            "Melon Scrambler",
            "🍉",
            350,
            2,
            DailyBuffTargetMode.None,
            "Change every Watermelon currently on the slot board into a random non-Watermelon symbol."),
        new(
            "lemon-aid",
            "Lemon Aid",
            "🍋",
            500,
            1,
            DailyBuffTargetMode.None,
            "Solve every Lemon on the solution board."),

        new(
            "overtime",
            "Overtime",
            "⏱️",
            400,
            1,
            DailyBuffTargetMode.None,
            "Add two Spins to this run."),
        new(
            "peek",
            "Peek",
            "👀",
            250,
            2,
            DailyBuffTargetMode.None,
            "Immediately reveal one random unsolved solution cell."),
        new(
            "mirror-row",
            "Mirror Row",
            "↔️",
            125,
            2,
            DailyBuffTargetMode.Cell,
            "Reverse the selected row."),
        new(
            "mirror-column",
            "Mirror Column",
            "↕️",
            125,
            2,
            DailyBuffTargetMode.Cell,
            "Reverse the selected column."),
        new(
            "shuffle-row",
            "Shuffle Row",
            "🔀",
            175,
            2,
            DailyBuffTargetMode.Cell,
            "Randomly shuffle the selected row."),
        new(
            "shuffle-column",
            "Shuffle Column",
            "🔃",
            175,
            2,
            DailyBuffTargetMode.Cell,
            "Randomly shuffle the selected column."),
        new(
            "clone-symbol",
            "Clone Symbol",
            "🧬",
            250,
            2,
            DailyBuffTargetMode.Cell,
            "Copy the selected symbol into one random other slot cell."),
        new(
            "market-corner",
            "Market Corner",
            "📊",
            350,
            1,
            DailyBuffTargetMode.Cell,
            "Double the payout value of the selected symbol for the rest of the run."),
        new(
            "research-grant",
            "Research Grant",
            "🧪",
            300,
            1,
            DailyBuffTargetMode.None,
            "Increase each future solution reveal reward from $20 to $50."),
        new(
            "coupon-book",
            "Coupon Book",
            "🎟️",
            250,
            1,
            DailyBuffTargetMode.None,
            "All future buff costs are reduced by 50%."),
        new(
            "row-scout",
            "Row Scout",
            "🔭",
            150,
            2,
            DailyBuffTargetMode.Cell,
            "Reveal one random unsolved solution cell in the selected row."),
        new(
            "column-scout",
            "Column Scout",
            "🧭",
            150,
            2,
            DailyBuffTargetMode.Cell,
            "Reveal one random unsolved solution cell in the selected column.")
    ];

    private readonly object _syncRoot = new();
    private readonly Dictionary<(ulong GuildId, ulong UserId), DailySlotsSession> _sessions = [];

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

        DailySlotsSession session;

        lock (_syncRoot)
        {
            session = new DailySlotsSession(
                command.GuildId.Value,
                command.User.Id,
                Guid.NewGuid().ToString("N"));

            session.InitializeDailyBuffSelection();
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
                "This Daily Slots game belongs to another player.",
                ephemeral: true);
            return;
        }

        ulong guildId = component.GuildId.Value;
        string sessionId = parts[3];
        DailySlotsSessionSnapshot? snapshot = null;
        DailySlotsSession? snapshotSession = null;

        // Take a lightweight in-memory snapshot before processing. We then use
        // component.UpdateAsync as the single Discord acknowledgement + message
        // update, matching the original fast interaction path. If Discord rejects
        // the update or processing throws, the catch below restores the state.
        lock (_syncRoot)
        {
            if (TryGetSession(guildId, ownerUserId, sessionId, out DailySlotsSession? current))
            {
                snapshotSession = current;
                snapshot = current.CreateSnapshot();
            }
        }

        try
        {
            switch (parts[1])
            {
                case "buff" when parts.Length == 5:
                    await HandleBuffSelectionAsync(
                        component,
                        guildId,
                        ownerUserId,
                        sessionId,
                        parts[4]);
                    return;

                case "start":
                    await HandleStartGameAsync(
                        component,
                        guildId,
                        ownerUserId,
                        sessionId);
                    return;

                case "spin":
                    await HandleSpinAsync(
                        component,
                        guildId,
                        ownerUserId,
                        sessionId);
                    return;

                case "cell" when parts.Length == 6 &&
                                      int.TryParse(parts[4], out int row) &&
                                      int.TryParse(parts[5], out int column):
                    await HandleCellAsync(
                        component,
                        guildId,
                        ownerUserId,
                        sessionId,
                        row,
                        column);
                    return;

                case "use" when parts.Length == 5:
                    await HandleUseBuffAsync(
                        component,
                        guildId,
                        ownerUserId,
                        sessionId,
                        parts[4]);
                    return;
            }

            await component.DeferAsync();
        }
        catch
        {
            // State is only committed if the Discord interaction/update succeeds.
            if (snapshotSession is not null && snapshot is not null)
            {
                lock (_syncRoot)
                {
                    if (TryGetSession(guildId, ownerUserId, sessionId, out DailySlotsSession? current) &&
                        ReferenceEquals(current, snapshotSession))
                    {
                        current.RestoreSnapshot(snapshot);
                    }
                }
            }

            throw;
        }
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
            if (!TryGetSession(guildId, userId, sessionId, out DailySlotsSession? session) ||
                session.Started)
            {
                invalidSession = true;
            }
            else
            {
                DailyBuffDefinition? buff = BuffDefinitions
                    .FirstOrDefault(x =>
                        x.Id == buffId &&
                        session.AvailableBuffIds.Contains(x.Id));

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
            await RespondInvalidSessionAsync(component);
            return;
        }

        // At 4/4, clicking a fifth grey buff intentionally changes nothing,
        // but still acknowledge the interaction without performing a message edit.
        if (selectionLimitReached || view is null)
        {
            await component.DeferAsync();
            return;
        }

        await UpdateMessageAsync(component, view);
    }

    private async Task HandleStartGameAsync(
        SocketMessageComponent component,
        ulong guildId,
        ulong userId,
        string sessionId)
    {
        MessageComponent? view = null;
        bool invalidSession = false;
        bool notReady = false;
        bool startedNow = false;

        lock (_syncRoot)
        {
            if (!TryGetSession(guildId, userId, sessionId, out DailySlotsSession? session))
            {
                invalidSession = true;
            }
            else if (!session.Started)
            {
                if (session.SelectedBuffIds.Count != MaxSelectedBuffs)
                {
                    notReady = true;
                }
                else
                {
                    session.StartGame();
                    startedNow = true;
                    view = BuildGameView(session);
                }
            }
            else
            {
                view = BuildGameView(session);
            }
        }

        if (invalidSession)
        {
            await RespondInvalidSessionAsync(component);
            return;
        }

        if (notReady || view is null)
        {
            await component.DeferAsync();
            return;
        }

        await UpdateMessageAsync(component, view);

        // Discord mobile can occasionally fail to paint one of the Unicode emotes
        // when the gameplay component tree is first created. A second edit shortly
        // afterwards forces the client to redraw the exact same room. Rebuild from
        // the current session state so a very fast interaction is not intentionally
        // overwritten with the original pre-interaction view. This refresh is only
        // performed on the initial transition into the game, never on normal clicks.
        if (startedNow)
            await RefreshGameViewAfterStartAsync(component.Message, guildId, userId, sessionId);
    }

    private async Task RefreshGameViewAfterStartAsync(
        IUserMessage message,
        ulong guildId,
        ulong userId,
        string sessionId)
    {
        try
        {
            await Task.Delay(200);

            MessageComponent? refreshedView = null;

            lock (_syncRoot)
            {
                if (TryGetSession(guildId, userId, sessionId, out DailySlotsSession? session) &&
                    session.Started)
                {
                    refreshedView = BuildGameView(session);
                }
            }

            if (refreshedView is null)
                return;

            await message.ModifyAsync(properties =>
            {
                properties.Components = refreshedView;
            });
        }
        catch
        {
            // This is only a best-effort mobile rendering workaround. The initial
            // UpdateAsync already succeeded, so a failed redraw must not roll back
            // or otherwise disturb the active game session.
        }
    }

    private async Task HandleSpinAsync(
        SocketMessageComponent component,
        ulong guildId,
        ulong userId,
        string sessionId)
    {
        MessageComponent? view = null;
        bool invalidSession = false;

        lock (_syncRoot)
        {
            if (!TryGetSession(guildId, userId, sessionId, out DailySlotsSession? session) ||
                !session.Started)
            {
                invalidSession = true;
            }
            else
            {
                if (session.SpinsRemaining > 0 && !session.IsSolutionComplete)
                {
                    Spin(session);
                }

                view = BuildGameView(session);
            }
        }

        if (invalidSession)
        {
            await RespondInvalidSessionAsync(component);
            return;
        }

        if (view is null)
        {
            await component.DeferAsync();
            return;
        }

        await UpdateMessageAsync(component, view);
    }

    private async Task HandleUseBuffAsync(
        SocketMessageComponent component,
        ulong guildId,
        ulong userId,
        string sessionId,
        string buffId)
    {
        MessageComponent? view = null;
        bool invalidSession = false;

        lock (_syncRoot)
        {
            if (!TryGetSession(guildId, userId, sessionId, out DailySlotsSession? session) ||
                !session.Started)
            {
                invalidSession = true;
            }
            else
            {
                DailyBuffState? buffState = session.Buffs
                    .FirstOrDefault(x => x.Definition.Id == buffId);

                if (buffState is null)
                {
                    session.StatusMessage = BuildStatus("⚠️", "That buff is not in this run's loadout.");
                }
                else if (buffState.UsesRemaining <= 0)
                {
                    session.StatusMessage = BuildStatus(
                        buffState.Definition.Emoji,
                        $"{buffState.Definition.Name} has no uses remaining.");
                }
                else if (session.Balance < GetBuffCost(session, buffState.Definition))
                {
                    long cost = GetBuffCost(session, buffState.Definition);
                    session.StatusMessage = BuildStatus(
                        "💰",
                        $"You need ${cost:N0} to use {buffState.Definition.Name}.");
                }
                else if (buffState.Definition.TargetMode == DailyBuffTargetMode.Cell)
                {
                    if (!session.HasSpunAtLeastOnce)
                    {
                        session.StatusMessage = BuildStatus(
                            buffState.Definition.Emoji,
                            "Spin the slots once before using a board-manipulation buff.");
                    }
                    else if (session.PendingTargetBuffId == buffId)
                    {
                        session.PendingTargetBuffId = null;
                        session.StatusMessage = BuildStatus(
                            buffState.Definition.Emoji,
                            $"{buffState.Definition.Name} cancelled.");
                    }
                    else
                    {
                        session.PendingTargetBuffId = buffId;
                        session.StatusMessage = BuildTargetPrompt(buffState.Definition);
                    }
                }
                else
                {
                    session.PendingTargetBuffId = null;
                    ActivateImmediateBuff(session, buffState);
                }

                view = BuildGameView(session);
            }
        }

        if (invalidSession)
        {
            await RespondInvalidSessionAsync(component);
            return;
        }

        if (view is null)
        {
            await component.DeferAsync();
            return;
        }

        await UpdateMessageAsync(component, view);
    }

    private async Task HandleCellAsync(
        SocketMessageComponent component,
        ulong guildId,
        ulong userId,
        string sessionId,
        int row,
        int column)
    {
        MessageComponent? view = null;
        bool invalidSession = false;

        lock (_syncRoot)
        {
            if (!TryGetSession(guildId, userId, sessionId, out DailySlotsSession? session) ||
                !session.Started)
            {
                invalidSession = true;
            }
            else if (row < 0 || row >= BoardSize || column < 0 || column >= BoardSize)
            {
                session.StatusMessage = BuildStatus("⚠️", "That target cell is invalid.");
                view = BuildGameView(session);
            }
            else if (string.IsNullOrWhiteSpace(session.PendingTargetBuffId))
            {
                session.StatusMessage = BuildPossibleSymbolsStatus(session, row, column);
                view = BuildGameView(session);
            }
            else
            {
                DailyBuffState? buffState = session.Buffs
                    .FirstOrDefault(x => x.Definition.Id == session.PendingTargetBuffId);

                if (buffState is null || buffState.UsesRemaining <= 0)
                {
                    session.PendingTargetBuffId = null;
                    session.StatusMessage = BuildStatus("⚠️", "That buff is no longer available.");
                }
                else if (session.Balance < GetBuffCost(session, buffState.Definition))
                {
                    long cost = GetBuffCost(session, buffState.Definition);
                    session.PendingTargetBuffId = null;
                    session.StatusMessage = BuildStatus(
                        "💰",
                        $"You need ${cost:N0} to use {buffState.Definition.Name}.");
                }
                else
                {
                    ResolveTargetedBuff(session, buffState, row, column);
                }

                view = BuildGameView(session);
            }
        }

        if (invalidSession)
        {
            await RespondInvalidSessionAsync(component);
            return;
        }

        if (view is null)
        {
            await component.DeferAsync();
            return;
        }

        await UpdateMessageAsync(component, view);
    }

    private static string BuildStatus(string contextEmoji, string message) =>
        $"{contextEmoji} • {message}";

    private static string BuildPossibleSymbolsStatus(
        DailySlotsSession session,
        int row,
        int column)
    {
        string header = $"🔎 • Possible symbols for position [{row + 1},{column + 1}]:";

        if (session.RevealedSolutionCells[row, column])
        {
            int solvedSymbolIndex = session.SolutionSymbolIndexes[row, column];
            return $"{header}\n{DailySymbols[solvedSymbolIndex]}";
        }

        var possibleSymbols = new List<string>(DailySymbols.Length);

        for (int symbolIndex = 0; symbolIndex < DailySymbols.Length; symbolIndex++)
        {
            if (!session.EliminatedSolutionSymbols[row, column, symbolIndex])
                possibleSymbols.Add(DailySymbols[symbolIndex]);
        }

        return $"{header}\n{string.Join(" ", possibleSymbols)}";
    }

    private static void ActivateImmediateBuff(
        DailySlotsSession session,
        DailyBuffState buffState)
    {
        DailyBuffDefinition buff = buffState.Definition;

        switch (buff.Id)
        {
            case "lucky-spin":
                if (session.SpinsRemaining <= 0 || session.IsSolutionComplete)
                {
                    session.StatusMessage = BuildStatus(
                        buff.Emoji,
                        "There is no future spin available for Lucky Spin.");
                    return;
                }

                SpendBuff(session, buffState);
                session.GuaranteedSolutionHitsNextSpin++;
                session.StatusMessage =
                    BuildStatus(buff.Emoji, "Lucky Spin used! Next spin will reveal " +
                    $"{session.GuaranteedSolutionHitsNextSpin} bonus solution " +
                    $"{(session.GuaranteedSolutionHitsNextSpin == 1 ? "symbol" : "symbols")} " +
                    "after the regular spin resolves.");
                break;

            case "double-payout":
                if (session.SpinsRemaining <= 0 || session.IsSolutionComplete)
                {
                    session.StatusMessage = BuildStatus(
                        buff.Emoji,
                        "There is no future spin available for Double Payout.");
                    return;
                }

                if (session.DoublePayoutNextSpin)
                {
                    session.StatusMessage = BuildStatus(
                        buff.Emoji,
                        "Double Payout is already armed for the next spin.");
                    return;
                }

                SpendBuff(session, buffState);
                session.DoublePayoutNextSpin = true;
                session.StatusMessage = BuildStatus(
                    buff.Emoji,
                    "Double Payout used! Slot and Jackpot money from the next Spin will be doubled.");
                break;

            case "jackpot-boost":
                if (session.IsSolutionComplete)
                {
                    session.StatusMessage = BuildStatus(
                        buff.Emoji,
                        "The solution is already complete.");
                    return;
                }

                if (session.JackpotBoostPending)
                {
                    session.StatusMessage = BuildStatus(
                        buff.Emoji,
                        "Jackpot Boost is already waiting for your next Jackpot.");
                    return;
                }

                SpendBuff(session, buffState);
                session.JackpotBoostPending = true;
                session.StatusMessage = BuildStatus(
                    buff.Emoji,
                    $"Jackpot Boost used! Your next new Jackpot payout will be x{JackpotBoostMultiplier}.");
                break;

            case "perfect-spin":
                if (session.SpinsRemaining <= 0 || session.IsSolutionComplete)
                {
                    session.StatusMessage = BuildStatus(
                        buff.Emoji,
                        "There is no future spin available for Perfect Spin.");
                    return;
                }

                if (session.PerfectSpinNextSpin)
                {
                    session.StatusMessage = BuildStatus(buff.Emoji, "Perfect Spin is already armed.");
                    return;
                }

                SpendBuff(session, buffState);
                session.PerfectSpinNextSpin = true;
                session.StatusMessage = BuildStatus(
                    buff.Emoji,
                    "Perfect Spin armed! Your next spin will exactly match the solution board.");
                break;

            case "cash-injection":
                SpendBuff(session, buffState);
                session.Balance += 1_000;
                session.StatusMessage = BuildStatus(
                    buff.Emoji,
                    "Cash Injection used! +$1,000.");
                break;

            case "compound-interest":
                if (session.CompoundInterestActive)
                {
                    session.StatusMessage = BuildStatus(
                        buff.Emoji,
                        "Compound Interest is already active.");
                    return;
                }

                SpendBuff(session, buffState);
                session.CompoundInterestActive = true;
                session.StatusMessage = BuildStatus(
                    buff.Emoji,
                    "Compound Interest activated! Future Spin payouts earn +50%.");
                break;

            case "four-kind-jackpots":
                if (session.FourInARowJackpotsEnabled)
                {
                    session.StatusMessage = BuildStatus(
                        buff.Emoji,
                        "Four of a Kind is already active.");
                    return;
                }

                SpendBuff(session, buffState);
                session.FourInARowJackpotsEnabled = true;

                PaylinePayoutResult fourKindResult = PayNewFiveInARows(session);
                session.Balance += fourKindResult.Payout;

                session.StatusMessage = BuildStatus(
                    buff.Emoji,
                    fourKindResult.LineCount > 0
                        ? $"Four of a Kind activated! {fourKindResult.LineCount} new Jackpot" +
                          $"{(fourKindResult.LineCount == 1 ? string.Empty : "s")} triggered. " +
                          $"+${fourKindResult.Payout:N0}."
                        : "Four of a Kind activated! 4-in-a-row can now trigger Jackpots.");
                break;

            case "mega-jackpots":
                if (session.JackpotMultiplier >= 400)
                {
                    session.StatusMessage = BuildStatus(
                        buff.Emoji,
                        "Mega Jackpots is already active.");
                    return;
                }

                SpendBuff(session, buffState);
                session.JackpotMultiplier = 400;
                session.StatusMessage = BuildStatus(
                    buff.Emoji,
                    "Mega Jackpots activated! The base Jackpot multiplier is now x400.");
                break;

            case "cherry-premium":
                if (session.CurrentSymbolPayoutValues[CherrySymbolIndex] >= 30)
                {
                    session.StatusMessage = BuildStatus(
                        buff.Emoji,
                        "Cherry Premium is already active.");
                    return;
                }

                SpendBuff(session, buffState);
                session.CurrentSymbolPayoutValues[CherrySymbolIndex] = 30;
                session.StatusMessage = BuildStatus(
                    buff.Emoji,
                    "Cherry Premium activated! Cherries are now worth $30.");
                break;

            case "melon-scrambler":
                if (!session.HasSpunAtLeastOnce)
                {
                    session.StatusMessage = BuildStatus(
                        buff.Emoji,
                        "Spin the slots once before using Melon Scrambler.");
                    return;
                }

                int watermelonCount = 0;

                for (int row = 0; row < BoardSize; row++)
                {
                    for (int column = 0; column < BoardSize; column++)
                    {
                        if (session.SlotSymbolIndexes[row, column] == WatermelonSymbolIndex)
                            watermelonCount++;
                    }
                }

                if (watermelonCount == 0)
                {
                    session.StatusMessage = BuildStatus(
                        buff.Emoji,
                        "There are no Watermelons on the current slot board.");
                    return;
                }

                SpendBuff(session, buffState);

                for (int row = 0; row < BoardSize; row++)
                {
                    for (int column = 0; column < BoardSize; column++)
                    {
                        if (session.SlotSymbolIndexes[row, column] != WatermelonSymbolIndex)
                            continue;

                        session.SlotSymbolIndexes[row, column] =
                            Random.Shared.Next(DailySymbols.Length - 1);
                        session.WildcardOriginalSymbolIndexes[row, column] = -1;
                    }
                }

                FinishBoardManipulation(
                    session,
                    buff,
                    $"{watermelonCount} Watermelon{(watermelonCount == 1 ? string.Empty : "s")} scrambled",
                    checkSolution: true);
                break;

            case "lemon-aid":
                int lemonsRevealed = 0;
                bool wasLemonComplete = session.IsSolutionComplete;

                for (int row = 0; row < BoardSize; row++)
                {
                    for (int column = 0; column < BoardSize; column++)
                    {
                        if (session.SolutionSymbolIndexes[row, column] != LemonSymbolIndex ||
                            session.RevealedSolutionCells[row, column])
                        {
                            continue;
                        }

                        session.RevealedSolutionCells[row, column] = true;
                        lemonsRevealed++;
                    }
                }

                if (lemonsRevealed == 0)
                {
                    session.StatusMessage = BuildStatus(
                        buff.Emoji,
                        "There are no unsolved Lemons left on the solution board.");
                    return;
                }

                SpendBuff(session, buffState);
                long lemonReward = (long)lemonsRevealed * session.SolutionRevealValue;
                session.Balance += lemonReward;

                session.StatusMessage = BuildStatus(
                    buff.Emoji,
                    $"Lemon Aid solved {lemonsRevealed} Lemon" +
                    $"{(lemonsRevealed == 1 ? string.Empty : "s")}! " +
                    $"+${lemonReward:N0}." +
                    (!wasLemonComplete && session.IsSolutionComplete
                        ? " Solution complete!"
                        : string.Empty));
                break;

            case "overtime":
                SpendBuff(session, buffState);
                session.SpinsRemaining += 2;
                session.MaxSpinsThisRun += 2;
                session.StatusMessage = BuildStatus(
                    buff.Emoji,
                    "Overtime added 2 Spins to this run.");
                break;

            case "peek":
                if (session.IsSolutionComplete)
                {
                    session.StatusMessage = BuildStatus(
                        buff.Emoji,
                        "The solution is already complete.");
                    return;
                }

                var unsolvedCells = new List<(int Row, int Column)>();

                for (int row = 0; row < BoardSize; row++)
                {
                    for (int column = 0; column < BoardSize; column++)
                    {
                        if (!session.RevealedSolutionCells[row, column])
                            unsolvedCells.Add((row, column));
                    }
                }

                if (unsolvedCells.Count == 0)
                    return;

                SpendBuff(session, buffState);
                (int peekRow, int peekColumn) = unsolvedCells[Random.Shared.Next(unsolvedCells.Count)];
                session.RevealedSolutionCells[peekRow, peekColumn] = true;
                session.Balance += session.SolutionRevealValue;

                session.StatusMessage = BuildStatus(
                    buff.Emoji,
                    $"Peek revealed position [{peekRow + 1},{peekColumn + 1}]! " +
                    $"+${session.SolutionRevealValue:N0}." +
                    (session.IsSolutionComplete ? " Solution complete!" : string.Empty));
                break;

            case "research-grant":
                if (session.SolutionRevealValue >= 50)
                {
                    session.StatusMessage = BuildStatus(
                        buff.Emoji,
                        "Research Grant is already active.");
                    return;
                }

                SpendBuff(session, buffState);
                session.SolutionRevealValue = 50;
                session.StatusMessage = BuildStatus(
                    buff.Emoji,
                    "Research Grant activated! Future solution reveals now pay $50.");
                break;

            case "coupon-book":
                if (session.BuffCostDiscountActive)
                {
                    session.StatusMessage = BuildStatus(
                        buff.Emoji,
                        "Coupon Book is already active.");
                    return;
                }

                SpendBuff(session, buffState);
                session.BuffCostDiscountActive = true;
                session.StatusMessage = BuildStatus(
                    buff.Emoji,
                    "Coupon Book activated! Future buff costs are 50% cheaper.");
                break;

            default:
                session.StatusMessage = BuildStatus(buff.Emoji, $"{buff.Name} is not implemented yet.");
                break;
        }
    }

    private static void ResolveTargetedBuff(
        DailySlotsSession session,
        DailyBuffState buffState,
        int row,
        int column)
    {
        DailyBuffDefinition buff = buffState.Definition;
        int currentSymbol = session.SlotSymbolIndexes[row, column];

        // -1 means the machine has not produced a symbol at this position yet.
        // Wildcards use -2 and are still valid targets for most board effects.
        if (currentSymbol == -1)
        {
            session.StatusMessage = BuildStatus(
                buff.Emoji,
                "That cell does not contain a slot symbol yet.");
            return;
        }

        // Applying Wildcard to an existing Wildcard is a no-op and costs nothing.
        if (buff.Id == "wild" && currentSymbol == WildSymbolIndex)
        {
            session.PendingTargetBuffId = null;
            session.StatusMessage = BuildStatus(buff.Emoji, "That symbol is already a Wildcard.");
            return;
        }

        if (buff.Id == "clone-symbol" && currentSymbol == WildSymbolIndex)
        {
            session.PendingTargetBuffId = null;
            session.StatusMessage = BuildStatus(
                buff.Emoji,
                "Clone Symbol needs a normal slot symbol, not a Wildcard.");
            return;
        }

        if (buff.Id == "insider-tip" &&
            session.RevealedSolutionCells[row, column])
        {
            session.PendingTargetBuffId = null;
            session.StatusMessage = BuildStatus(
                buff.Emoji,
                "That solution position is already revealed.");
            return;
        }

        if (buff.Id == "row-scout" &&
            !HasUnsolvedSolutionCellInRow(session, row))
        {
            session.PendingTargetBuffId = null;
            session.StatusMessage = BuildStatus(
                buff.Emoji,
                "That solution row is already fully solved.");
            return;
        }

        if (buff.Id == "column-scout" &&
            !HasUnsolvedSolutionCellInColumn(session, column))
        {
            session.PendingTargetBuffId = null;
            session.StatusMessage = BuildStatus(
                buff.Emoji,
                "That solution column is already fully solved.");
            return;
        }

        SpendBuff(session, buffState);
        session.PendingTargetBuffId = null;

        int oldSymbol;

        switch (buff.Id)
        {
            case "row-left":
                ShiftRowLeft(session.SlotSymbolIndexes, row);
                ShiftRowLeft(session.WildcardOriginalSymbolIndexes, row);
                ShiftRowLeft(session.SlotTokenIds, row);
                FinishBoardManipulation(session, buff, "Shift Row Left completed", checkSolution: true);
                break;

            case "row-right":
                ShiftRowRight(session.SlotSymbolIndexes, row);
                ShiftRowRight(session.WildcardOriginalSymbolIndexes, row);
                ShiftRowRight(session.SlotTokenIds, row);
                FinishBoardManipulation(session, buff, "Shift Row Right completed", checkSolution: true);
                break;

            case "column-up":
                ShiftColumnUp(session.SlotSymbolIndexes, column);
                ShiftColumnUp(session.WildcardOriginalSymbolIndexes, column);
                ShiftColumnUp(session.SlotTokenIds, column);
                FinishBoardManipulation(session, buff, "Shift Column Up completed", checkSolution: true);
                break;

            case "column-down":
                ShiftColumnDown(session.SlotSymbolIndexes, column);
                ShiftColumnDown(session.WildcardOriginalSymbolIndexes, column);
                ShiftColumnDown(session.SlotTokenIds, column);
                FinishBoardManipulation(session, buff, "Shift Column Down completed", checkSolution: true);
                break;

            case "reroll":
                oldSymbol = GetUnderlyingSlotSymbolIndex(session, row, column);
                session.SlotSymbolIndexes[row, column] = RollDifferentSymbol(oldSymbol);
                session.WildcardOriginalSymbolIndexes[row, column] = -1;
                FinishBoardManipulation(
                    session,
                    buff,
                    $"Symbol {FormatSlotSymbol(oldSymbol)} rerolled into {DailySymbols[session.SlotSymbolIndexes[row, column]]}",
                    checkSolution: true);
                break;

            case "wild":
                bool wasSolutionComplete = session.IsSolutionComplete;
                oldSymbol = GetUnderlyingSlotSymbolIndex(session, row, column);
                session.WildcardOriginalSymbolIndexes[row, column] = oldSymbol;
                session.SlotSymbolIndexes[row, column] = WildSymbolIndex;

                bool newlySolved = !session.RevealedSolutionCells[row, column];
                session.RevealedSolutionCells[row, column] = true;

                if (newlySolved)
                    session.Balance += session.SolutionRevealValue;

                PaylinePayoutResult wildResult = PayNewFiveInARows(session);
                session.Balance += wildResult.Payout;

                session.StatusMessage = BuildManipulationStatus(
                    session,
                    buff,
                    $"Symbol {FormatSlotSymbol(oldSymbol)} became a Wildcard {buff.Emoji}",
                    wildResult,
                    newlySolved ? 1 : 0,
                    solutionCompleted: !wasSolutionComplete && session.IsSolutionComplete);
                break;

            case "insider-tip":
                int trueSymbol = session.SolutionSymbolIndexes[row, column];
                session.SlotSymbolIndexes[row, column] = trueSymbol;
                session.WildcardOriginalSymbolIndexes[row, column] = -1;
                session.SuppressedNaturalRevealTokenIds[row, column] =
                    session.SlotTokenIds[row, column];

                for (int symbolIndex = 0; symbolIndex < DailySymbols.Length; symbolIndex++)
                {
                    session.EliminatedSolutionSymbols[row, column, symbolIndex] =
                        symbolIndex != trueSymbol;
                }

                PaylinePayoutResult insiderResult = PayNewFiveInARows(session);
                session.Balance += insiderResult.Payout;
                session.StatusMessage = BuildManipulationStatus(
                    session,
                    buff,
                    $"Position [{row + 1},{column + 1}] changed to {DailySymbols[trueSymbol]} without revealing the solution",
                    insiderResult);
                break;

            case "mirror-row":
                ReverseRow(session.SlotSymbolIndexes, row);
                ReverseRow(session.WildcardOriginalSymbolIndexes, row);
                ReverseRow(session.SlotTokenIds, row);
                FinishBoardManipulation(session, buff, "Selected row mirrored", checkSolution: true);
                break;

            case "mirror-column":
                ReverseColumn(session.SlotSymbolIndexes, column);
                ReverseColumn(session.WildcardOriginalSymbolIndexes, column);
                ReverseColumn(session.SlotTokenIds, column);
                FinishBoardManipulation(session, buff, "Selected column mirrored", checkSolution: true);
                break;

            case "shuffle-row":
                ShuffleRow(session, row);
                FinishBoardManipulation(session, buff, "Selected row shuffled", checkSolution: true);
                break;

            case "shuffle-column":
                ShuffleColumn(session, column);
                FinishBoardManipulation(session, buff, "Selected column shuffled", checkSolution: true);
                break;

            case "clone-symbol":
                var cloneTargets = new List<(int Row, int Column)>();

                for (int targetRow = 0; targetRow < BoardSize; targetRow++)
                {
                    for (int targetColumn = 0; targetColumn < BoardSize; targetColumn++)
                    {
                        if (targetRow != row || targetColumn != column)
                            cloneTargets.Add((targetRow, targetColumn));
                    }
                }

                (int cloneRow, int cloneColumn) =
                    cloneTargets[Random.Shared.Next(cloneTargets.Count)];

                session.SlotSymbolIndexes[cloneRow, cloneColumn] = currentSymbol;
                session.WildcardOriginalSymbolIndexes[cloneRow, cloneColumn] = -1;

                FinishBoardManipulation(
                    session,
                    buff,
                    $"{DailySymbols[currentSymbol]} cloned into position [{cloneRow + 1},{cloneColumn + 1}]",
                    checkSolution: true);
                break;

            case "market-corner":
                int marketSymbol = GetUnderlyingSlotSymbolIndex(session, row, column);

                if (marketSymbol < 0 || marketSymbol >= DailySymbols.Length)
                {
                    // Defensive refund. Normal gameplay should never reach this path.
                    session.Balance += GetBuffCost(session, buff);
                    buffState.UsesRemaining++;
                    session.StatusMessage = BuildStatus(
                        buff.Emoji,
                        "That symbol cannot be used for Market Corner.");
                    break;
                }

                session.CurrentSymbolPayoutValues[marketSymbol] =
                    checked(session.CurrentSymbolPayoutValues[marketSymbol] * 2);

                session.StatusMessage = BuildStatus(
                    buff.Emoji,
                    $"Market Corner doubled {DailySymbols[marketSymbol]} to " +
                    $"${session.CurrentSymbolPayoutValues[marketSymbol]:N0} per symbol.");
                break;

            case "row-scout":
                RevealRandomSolutionCellInRow(session, row, out int scoutRow, out int scoutColumn);
                session.Balance += session.SolutionRevealValue;
                session.StatusMessage = BuildStatus(
                    buff.Emoji,
                    $"Row Scout revealed position [{scoutRow + 1},{scoutColumn + 1}]! " +
                    $"+${session.SolutionRevealValue:N0}." +
                    (session.IsSolutionComplete ? " Solution complete!" : string.Empty));
                break;

            case "column-scout":
                RevealRandomSolutionCellInColumn(session, column, out int columnScoutRow, out int columnScoutColumn);
                session.Balance += session.SolutionRevealValue;
                session.StatusMessage = BuildStatus(
                    buff.Emoji,
                    $"Column Scout revealed position [{columnScoutRow + 1},{columnScoutColumn + 1}]! " +
                    $"+${session.SolutionRevealValue:N0}." +
                    (session.IsSolutionComplete ? " Solution complete!" : string.Empty));
                break;

            default:
                // This path should never be reached because only targeted buffs
                // can arrive here, but refunding keeps the state safe if a future
                // definition is accidentally misconfigured.
                session.Balance += GetBuffCost(session, buff);
                buffState.UsesRemaining++;
                session.StatusMessage = BuildStatus(buff.Emoji, $"{buff.Name} is not implemented yet.");
                break;
        }
    }

    private static int RollDifferentSymbol(int oldSymbolIndex)
    {
        if (DailySymbols.Length <= 1)
            return 0;

        if (oldSymbolIndex < 0 || oldSymbolIndex >= DailySymbols.Length)
            return Random.Shared.Next(DailySymbols.Length);

        // Roll from N-1 possibilities, then skip over the old symbol. This
        // guarantees a reroll can never return the same symbol it replaced.
        int newSymbolIndex = Random.Shared.Next(DailySymbols.Length - 1);

        if (newSymbolIndex >= oldSymbolIndex)
            newSymbolIndex++;

        return newSymbolIndex;
    }

    private static int GetUnderlyingSlotSymbolIndex(
        DailySlotsSession session,
        int row,
        int column)
    {
        int symbolIndex = session.SlotSymbolIndexes[row, column];

        if (symbolIndex == WildSymbolIndex)
            return session.WildcardOriginalSymbolIndexes[row, column];

        return symbolIndex;
    }

    private static string FormatSlotSymbol(int symbolIndex) =>
        symbolIndex >= 0 && symbolIndex < DailySymbols.Length
            ? DailySymbols[symbolIndex]
            : "symbol";

    private static void FinishBoardManipulation(
        DailySlotsSession session,
        DailyBuffDefinition buff,
        string actionText,
        bool checkSolution)
    {
        bool wasSolutionComplete = session.IsSolutionComplete;
        int newlyRevealed = 0;

        if (checkSolution)
        {
            newlyRevealed += RevealNaturalSolutionHits(session);

            // A Wildcard is a persistent board piece. If a shift moves one onto an
            // unrevealed solution position, that new position is solved immediately.
            // This lets players deliberately move a Wildcard around the board to
            // uncover additional cells.
            newlyRevealed += RevealWildcardSolutionHits(session);
        }

        if (newlyRevealed > 0)
            session.Balance += (long)newlyRevealed * session.SolutionRevealValue;

        PaylinePayoutResult result = PayNewFiveInARows(session);
        session.Balance += result.Payout;
        session.StatusMessage = BuildManipulationStatus(
            session,
            buff,
            actionText,
            result,
            newlyRevealed,
            solutionCompleted: !wasSolutionComplete && session.IsSolutionComplete);
    }

    private static string BuildManipulationStatus(
        DailySlotsSession session,
        DailyBuffDefinition buff,
        string actionText,
        PaylinePayoutResult result,
        int newlyRevealed = 0,
        bool solutionCompleted = false)
    {
        var status = new StringBuilder(180);
        status.Append(buff.Emoji);
        status.Append(" • ");

        AppendSentence(status, actionText);

        if (newlyRevealed > 0)
        {
            status.Append(
                $" {newlyRevealed} new " +
                $"{(newlyRevealed == 1 ? "solution" : "solutions")} found!");
        }

        if (result.LineCount > 0)
        {
            status.Append(
                $" {result.LineCount} new Jackpot" +
                $"{(result.LineCount == 1 ? string.Empty : "s")}!");
        }

        if (solutionCompleted)
            status.Append(" Solution complete!");

        long totalPayout =
            result.Payout +
            ((long)newlyRevealed * session.SolutionRevealValue);

        status.Append($" +${totalPayout:N0}.");

        if (result.UsedJackpotBoost)
            status.Append($" Jackpot Boost x{JackpotBoostMultiplier} applied!");

        return status.ToString();
    }

    private static void AppendSentence(StringBuilder builder, string text)
    {
        string trimmed = text.Trim();
        builder.Append(trimmed);

        if (trimmed.Length == 0)
            return;

        char last = trimmed[^1];

        if (last is not ('.' or '!' or '?'))
            builder.Append('.');
    }

    private static string BuildTargetPrompt(DailyBuffDefinition buff)
    {
        string prompt = buff.Id switch
        {
            "row-left" => "Shift Row Left selected — choose any symbol in the row you want to shift.",
            "row-right" => "Shift Row Right selected — choose any symbol in the row you want to shift.",
            "column-up" => "Shift Column Up selected — choose any symbol in the column you want to shift.",
            "column-down" => "Shift Column Down selected — choose any symbol in the column you want to shift.",
            "reroll" => "Reroll Symbol selected — choose the symbol you want to reroll.",
            "wild" => "Wildcard selected — choose the symbol to solve and turn into a Wildcard.",
            "insider-tip" => "Insider Tip selected — choose a cell to copy its true solution symbol into.",
            "mirror-row" => "Mirror Row selected — choose any symbol in the row you want to reverse.",
            "mirror-column" => "Mirror Column selected — choose any symbol in the column you want to reverse.",
            "shuffle-row" => "Shuffle Row selected — choose any symbol in the row you want to shuffle.",
            "shuffle-column" => "Shuffle Column selected — choose any symbol in the column you want to shuffle.",
            "clone-symbol" => "Clone Symbol selected — choose the normal symbol you want to copy.",
            "market-corner" => "Market Corner selected — choose the symbol whose payout value you want to double.",
            "row-scout" => "Row Scout selected — choose any symbol in the solution row you want to scout.",
            "column-scout" => "Column Scout selected — choose any symbol in the solution column you want to scout.",
            _ => $"{buff.Name} selected — choose a target symbol."
        };

        return BuildStatus(buff.Emoji, prompt);
    }

    private static long GetBuffCost(
        DailySlotsSession session,
        DailyBuffDefinition buff)
    {
        if (!session.BuffCostDiscountActive || buff.Cost == 0)
            return buff.Cost;

        // Round half-costs up so odd prices such as $555 become $278.
        return (buff.Cost + 1) / 2;
    }

    private static void SpendBuff(
        DailySlotsSession session,
        DailyBuffState buffState)
    {
        session.Balance -= GetBuffCost(session, buffState.Definition);
        buffState.UsesRemaining--;
    }

    private static void Spin(DailySlotsSession session)
    {
        session.PendingTargetBuffId = null;
        session.PaidPaylineSignaturesThisSpin.Clear();

        bool perfectSpin = session.PerfectSpinNextSpin;
        bool doublePayout = session.DoublePayoutNextSpin;
        int guaranteedHits = session.GuaranteedSolutionHitsNextSpin;

        session.PerfectSpinNextSpin = false;
        session.DoublePayoutNextSpin = false;
        session.GuaranteedSolutionHitsNextSpin = 0;
        ClearBoard(session.WildcardOriginalSymbolIndexes, -1);
        Array.Clear(
            session.SuppressedNaturalRevealTokenIds,
            0,
            session.SuppressedNaturalRevealTokenIds.Length);

        if (perfectSpin)
        {
            for (int row = 0; row < BoardSize; row++)
            {
                for (int column = 0; column < BoardSize; column++)
                {
                    session.SlotSymbolIndexes[row, column] =
                        session.SolutionSymbolIndexes[row, column];
                }
            }
        }
        else
        {
            for (int row = 0; row < BoardSize; row++)
            {
                for (int column = 0; column < BoardSize; column++)
                {
                    session.SlotSymbolIndexes[row, column] =
                        Random.Shared.Next(DailySymbols.Length);
                }
            }
        }

        // Give every freshly-spun board piece a stable identity. Shift buffs move
        // these IDs with the symbols, which lets jackpot logic distinguish a
        // genuinely new symbol entering a payline from the same pieces being
        // rotated within that payline.
        for (int row = 0; row < BoardSize; row++)
        {
            for (int column = 0; column < BoardSize; column++)
                session.SlotTokenIds[row, column] = session.NextSlotTokenId++;
        }

        // Resolve the completely normal random spin first. Lucky Spin is then
        // applied as a true bonus on top, so stacked uses can never replace or
        // overlap solution hits the player would have received naturally.
        int naturalReveals = RevealNaturalSolutionHits(session);
        int luckyReveals = RevealGuaranteedBonusSolutions(session, guaranteedHits);
        int newlyRevealed = naturalReveals + luckyReveals;
        long solutionReward = (long)newlyRevealed * session.SolutionRevealValue;
        session.Balance += solutionReward;

        long basePayout = CalculateBasePayout(session);
        PaylinePayoutResult paylineResult = PayNewFiveInARows(session);
        long spinPayout = basePayout + paylineResult.Payout;

        if (session.CompoundInterestActive)
            spinPayout = checked((spinPayout * 3) / 2);

        if (doublePayout)
            spinPayout = checked(spinPayout * 2);

        session.Balance += spinPayout;
        session.SpinsRemaining--;
        session.HasSpunAtLeastOnce = true;

        long totalPayout = spinPayout + solutionReward;

        var status = new StringBuilder(190);
        status.Append("🎰 • Spin complete.");

        if (newlyRevealed > 0)
        {
            status.Append(
                $" {newlyRevealed} new " +
                $"{(newlyRevealed == 1 ? "solution" : "solutions")} found!");
        }

        if (paylineResult.LineCount > 0)
        {
            status.Append(
                $" {paylineResult.LineCount} Jackpot" +
                $"{(paylineResult.LineCount == 1 ? string.Empty : "s")} hit!");

            if (paylineResult.UsedJackpotBoost)
                status.Append($" Jackpot Boost x{JackpotBoostMultiplier} applied!");
        }

        if (session.CompoundInterestActive)
            status.Append(" Compound Interest +50% applied!");

        if (doublePayout)
            status.Append(" Double Payout applied!");

        if (session.IsSolutionComplete)
            status.Append(" Solution complete!");
        else if (session.SpinsRemaining <= 0)
            status.Append(" No spins remaining.");

        status.Append($" +${totalPayout:N0}.");

        session.StatusMessage = status.ToString();
    }

    private static int RevealGuaranteedBonusSolutions(
        DailySlotsSession session,
        int guaranteedHits)
    {
        if (guaranteedHits <= 0)
            return 0;

        var candidates = new List<(int Row, int Column)>();

        for (int row = 0; row < BoardSize; row++)
        {
            for (int column = 0; column < BoardSize; column++)
            {
                if (!session.RevealedSolutionCells[row, column])
                    candidates.Add((row, column));
            }
        }

        int hitsToReveal = Math.Min(guaranteedHits, candidates.Count);

        for (int i = 0; i < hitsToReveal; i++)
        {
            int chosenIndex = Random.Shared.Next(i, candidates.Count);
            (candidates[i], candidates[chosenIndex]) =
                (candidates[chosenIndex], candidates[i]);

            (int row, int column) = candidates[i];

            // Lucky Spin is a post-spin bonus. It reveals the solution square
            // without changing the random slot result, so it cannot create
            // extra symbol-value or 5-in-a-row payouts.
            session.RevealedSolutionCells[row, column] = true;
        }

        return hitsToReveal;
    }

    private static int RevealWildcardSolutionHits(DailySlotsSession session)
    {
        int newlyRevealed = 0;

        for (int row = 0; row < BoardSize; row++)
        {
            for (int column = 0; column < BoardSize; column++)
            {
                if (session.RevealedSolutionCells[row, column] ||
                    session.SlotSymbolIndexes[row, column] != WildSymbolIndex)
                {
                    continue;
                }

                session.RevealedSolutionCells[row, column] = true;
                newlyRevealed++;
            }
        }

        return newlyRevealed;
    }

    private static int RevealNaturalSolutionHits(DailySlotsSession session)
    {
        int newlyRevealed = 0;

        for (int row = 0; row < BoardSize; row++)
        {
            for (int column = 0; column < BoardSize; column++)
            {
                if (session.RevealedSolutionCells[row, column])
                    continue;

                int slotSymbolIndex = session.SlotSymbolIndexes[row, column];
                int solutionSymbolIndex = session.SolutionSymbolIndexes[row, column];
                long suppressedTokenId =
                    session.SuppressedNaturalRevealTokenIds[row, column];

                if (suppressedTokenId != 0)
                {
                    bool sameHintedPieceStillInPlace =
                        session.SlotTokenIds[row, column] == suppressedTokenId &&
                        slotSymbolIndex == solutionSymbolIndex;

                    if (sameHintedPieceStillInPlace)
                        continue;

                    session.SuppressedNaturalRevealTokenIds[row, column] = 0;
                }

                if (slotSymbolIndex == solutionSymbolIndex)
                {
                    session.RevealedSolutionCells[row, column] = true;
                    newlyRevealed++;
                }
                else if (slotSymbolIndex >= 0)
                {
                    // A checked symbol that failed to solve this position can never
                    // be the daily solution for that cell, so remember the deduction.
                    session.EliminatedSolutionSymbols[row, column, slotSymbolIndex] = true;
                }
            }
        }

        return newlyRevealed;
    }

    private static long CalculateBasePayout(DailySlotsSession session)
    {
        long payout = 0;

        for (int row = 0; row < BoardSize; row++)
        {
            for (int column = 0; column < BoardSize; column++)
            {
                int symbolIndex = session.SlotSymbolIndexes[row, column];

                if (symbolIndex >= 0)
                    payout += session.CurrentSymbolPayoutValues[symbolIndex];
            }
        }

        return payout;
    }

    private static PaylinePayoutResult PayNewFiveInARows(DailySlotsSession session)
    {
        long payout = 0;
        int lineCount = 0;
        bool usedJackpotBoost = false;

        void PayLine(string lineKey, (int Row, int Column)[] cells)
        {
            if (!TryGetPaylineMatch(session, cells, out PaylineMatch match))
                return;

            string signature = BuildPaylineSignature(session, lineKey, match.Cells);

            if (!session.PaidPaylineSignaturesThisSpin.Add(signature))
                return;

            long linePayout =
                (long)session.CurrentSymbolPayoutValues[match.SymbolIndex] *
                session.JackpotMultiplier;

            if (session.JackpotBoostPending)
            {
                linePayout = checked(linePayout * JackpotBoostMultiplier);
                session.JackpotBoostPending = false;
                usedJackpotBoost = true;
            }

            payout = checked(payout + linePayout);
            lineCount++;
        }

        for (int row = 0; row < BoardSize; row++)
        {
            var cells = new (int Row, int Column)[BoardSize];

            for (int column = 0; column < BoardSize; column++)
                cells[column] = (row, column);

            PayLine($"R{row}", cells);
        }

        for (int column = 0; column < BoardSize; column++)
        {
            var cells = new (int Row, int Column)[BoardSize];

            for (int row = 0; row < BoardSize; row++)
                cells[row] = (row, column);

            PayLine($"C{column}", cells);
        }

        for (int diagonal = 0; diagonal < 2; diagonal++)
        {
            var cells = new (int Row, int Column)[BoardSize];

            for (int row = 0; row < BoardSize; row++)
            {
                int column = diagonal == 0
                    ? row
                    : BoardSize - 1 - row;

                cells[row] = (row, column);
            }

            PayLine($"D{diagonal}", cells);
        }

        return new PaylinePayoutResult(payout, lineCount, usedJackpotBoost);
    }

    private static string BuildPaylineSignature(
        DailySlotsSession session,
        string lineKey,
        IReadOnlyList<(int Row, int Column)> cells)
    {
        var tokenIds = new long[cells.Count];

        for (int i = 0; i < cells.Count; i++)
        {
            (int row, int column) = cells[i];
            tokenIds[i] = session.SlotTokenIds[row, column];
        }

        // Order is intentionally ignored. Rotating the same contributing pieces
        // inside a winning line is still the same Jackpot. If a different physical
        // piece enters the qualifying 4/5-symbol composition, the signature changes.
        Array.Sort(tokenIds);
        return $"{lineKey}:{string.Join(",", tokenIds)}";
    }

    private static bool TryGetPaylineMatch(
        DailySlotsSession session,
        IReadOnlyList<(int Row, int Column)> cells,
        out PaylineMatch match)
    {
        match = null!;

        for (int i = 0; i < cells.Count; i++)
        {
            (int row, int column) = cells[i];

            if (session.SlotSymbolIndexes[row, column] == -1)
                return false;
        }

        // A normal Jackpot always takes priority over the 4-in-a-row upgrade.
        if (TryGetExactLineMatch(session, cells, out match))
            return true;

        if (!session.FourInARowJackpotsEnabled || cells.Count != BoardSize)
            return false;

        PaylineMatch? bestFourMatch = null;
        int bestPayoutValue = -1;

        // On a five-cell line, "4 in a row" means one of the two contiguous
        // four-cell windows: positions 0-3 or positions 1-4.
        for (int start = 0; start <= 1; start++)
        {
            var window = new (int Row, int Column)[4];

            for (int i = 0; i < window.Length; i++)
                window[i] = cells[start + i];

            if (!TryGetExactLineMatch(session, window, out PaylineMatch fourMatch))
                continue;

            int payoutValue = session.CurrentSymbolPayoutValues[fourMatch.SymbolIndex];

            if (bestFourMatch is null || payoutValue > bestPayoutValue)
            {
                bestFourMatch = fourMatch;
                bestPayoutValue = payoutValue;
            }
        }

        if (bestFourMatch is null)
            return false;

        match = bestFourMatch;
        return true;
    }

    private static bool TryGetExactLineMatch(
        DailySlotsSession session,
        IReadOnlyList<(int Row, int Column)> cells,
        out PaylineMatch match)
    {
        match = null!;
        int bestSymbolIndex = -1;
        int bestPayoutValue = -1;

        for (int symbolIndex = 0; symbolIndex < DailySymbols.Length; symbolIndex++)
        {
            bool compatible = true;

            for (int i = 0; i < cells.Count; i++)
            {
                (int row, int column) = cells[i];
                int current = session.SlotSymbolIndexes[row, column];

                if (current != symbolIndex && current != WildSymbolIndex)
                {
                    compatible = false;
                    break;
                }
            }

            if (!compatible)
                continue;

            int payoutValue = session.CurrentSymbolPayoutValues[symbolIndex];

            if (payoutValue > bestPayoutValue)
            {
                bestSymbolIndex = symbolIndex;
                bestPayoutValue = payoutValue;
            }
        }

        if (bestSymbolIndex < 0)
            return false;

        match = new PaylineMatch(bestSymbolIndex, cells.ToArray());
        return true;
    }

    private static bool[,] BuildActivePaylineCellMask(DailySlotsSession session)
    {
        var winningCells = new bool[BoardSize, BoardSize];

        void Highlight((int Row, int Column)[] cells)
        {
            if (!TryGetPaylineMatch(session, cells, out PaylineMatch match))
                return;

            foreach ((int row, int column) in match.Cells)
                winningCells[row, column] = true;
        }

        for (int row = 0; row < BoardSize; row++)
        {
            var cells = new (int Row, int Column)[BoardSize];

            for (int column = 0; column < BoardSize; column++)
                cells[column] = (row, column);

            Highlight(cells);
        }

        for (int column = 0; column < BoardSize; column++)
        {
            var cells = new (int Row, int Column)[BoardSize];

            for (int row = 0; row < BoardSize; row++)
                cells[row] = (row, column);

            Highlight(cells);
        }

        for (int diagonal = 0; diagonal < 2; diagonal++)
        {
            var cells = new (int Row, int Column)[BoardSize];

            for (int row = 0; row < BoardSize; row++)
            {
                int column = diagonal == 0
                    ? row
                    : BoardSize - 1 - row;

                cells[row] = (row, column);
            }

            Highlight(cells);
        }

        return winningCells;
    }

    private static int GetHighestPayingSymbolIndex(DailySlotsSession session)
    {
        int bestIndex = 0;

        for (int i = 1; i < session.CurrentSymbolPayoutValues.Length; i++)
        {
            if (session.CurrentSymbolPayoutValues[i] >
                session.CurrentSymbolPayoutValues[bestIndex])
            {
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static void ReverseRow<T>(T[,] board, int row)
    {
        for (int left = 0, right = BoardSize - 1; left < right; left++, right--)
            (board[row, left], board[row, right]) = (board[row, right], board[row, left]);
    }

    private static void ReverseColumn<T>(T[,] board, int column)
    {
        for (int top = 0, bottom = BoardSize - 1; top < bottom; top++, bottom--)
            (board[top, column], board[bottom, column]) = (board[bottom, column], board[top, column]);
    }

    private static void ShuffleRow(DailySlotsSession session, int row)
    {
        for (int i = BoardSize - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);

            (session.SlotSymbolIndexes[row, i], session.SlotSymbolIndexes[row, j]) =
                (session.SlotSymbolIndexes[row, j], session.SlotSymbolIndexes[row, i]);
            (session.WildcardOriginalSymbolIndexes[row, i], session.WildcardOriginalSymbolIndexes[row, j]) =
                (session.WildcardOriginalSymbolIndexes[row, j], session.WildcardOriginalSymbolIndexes[row, i]);
            (session.SlotTokenIds[row, i], session.SlotTokenIds[row, j]) =
                (session.SlotTokenIds[row, j], session.SlotTokenIds[row, i]);
        }
    }

    private static void ShuffleColumn(DailySlotsSession session, int column)
    {
        for (int i = BoardSize - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);

            (session.SlotSymbolIndexes[i, column], session.SlotSymbolIndexes[j, column]) =
                (session.SlotSymbolIndexes[j, column], session.SlotSymbolIndexes[i, column]);
            (session.WildcardOriginalSymbolIndexes[i, column], session.WildcardOriginalSymbolIndexes[j, column]) =
                (session.WildcardOriginalSymbolIndexes[j, column], session.WildcardOriginalSymbolIndexes[i, column]);
            (session.SlotTokenIds[i, column], session.SlotTokenIds[j, column]) =
                (session.SlotTokenIds[j, column], session.SlotTokenIds[i, column]);
        }
    }

    private static bool HasUnsolvedSolutionCellInRow(
        DailySlotsSession session,
        int row)
    {
        for (int column = 0; column < BoardSize; column++)
        {
            if (!session.RevealedSolutionCells[row, column])
                return true;
        }

        return false;
    }

    private static bool HasUnsolvedSolutionCellInColumn(
        DailySlotsSession session,
        int column)
    {
        for (int row = 0; row < BoardSize; row++)
        {
            if (!session.RevealedSolutionCells[row, column])
                return true;
        }

        return false;
    }

    private static void RevealRandomSolutionCellInRow(
        DailySlotsSession session,
        int row,
        out int revealedRow,
        out int revealedColumn)
    {
        var candidates = new List<int>();

        for (int column = 0; column < BoardSize; column++)
        {
            if (!session.RevealedSolutionCells[row, column])
                candidates.Add(column);
        }

        revealedRow = row;
        revealedColumn = candidates[Random.Shared.Next(candidates.Count)];
        session.RevealedSolutionCells[revealedRow, revealedColumn] = true;
    }

    private static void RevealRandomSolutionCellInColumn(
        DailySlotsSession session,
        int column,
        out int revealedRow,
        out int revealedColumn)
    {
        var candidates = new List<int>();

        for (int row = 0; row < BoardSize; row++)
        {
            if (!session.RevealedSolutionCells[row, column])
                candidates.Add(row);
        }

        revealedRow = candidates[Random.Shared.Next(candidates.Count)];
        revealedColumn = column;
        session.RevealedSolutionCells[revealedRow, revealedColumn] = true;
    }

    private static void ClearBoard(int[,] board, int value)
    {
        for (int row = 0; row < BoardSize; row++)
        {
            for (int column = 0; column < BoardSize; column++)
                board[row, column] = value;
        }
    }

    private static void ShiftRowLeft<T>(T[,] board, int row)
    {
        T first = board[row, 0];

        for (int column = 0; column < BoardSize - 1; column++)
            board[row, column] = board[row, column + 1];

        board[row, BoardSize - 1] = first;
    }

    private static void ShiftRowRight<T>(T[,] board, int row)
    {
        T last = board[row, BoardSize - 1];

        for (int column = BoardSize - 1; column > 0; column--)
            board[row, column] = board[row, column - 1];

        board[row, 0] = last;
    }

    private static void ShiftColumnUp<T>(T[,] board, int column)
    {
        T first = board[0, column];

        for (int row = 0; row < BoardSize - 1; row++)
            board[row, column] = board[row + 1, column];

        board[BoardSize - 1, column] = first;
    }

    private static void ShiftColumnDown<T>(T[,] board, int column)
    {
        T last = board[BoardSize - 1, column];

        for (int row = BoardSize - 1; row > 0; row--)
            board[row, column] = board[row - 1, column];

        board[0, column] = last;
    }

    private static MessageComponent BuildBuffSelectionView(DailySlotsSession session)
    {
        int selectedCount = session.SelectedBuffIds.Count;

        var container = new ContainerBuilder()
            .WithAccentColor(new Color(241, 196, 15))
            .WithTextDisplay("## 🎰 Daily Slots 🎰")
            .WithTextDisplay($"**Today's buffs — select {MaxSelectedBuffs} to begin ({selectedCount}/{MaxSelectedBuffs})**");

        // Discord performs text wrapping on the client, and trailing whitespace-like
        // glyphs do not reliably reserve rendered width/height. Keep the lobby
        // deterministic instead: descriptions that need two lines are split explicitly.
        // If any description uses a second line, every other buff receives one blank
        // second description line so all Sections have the same height.
        DailyBuffDefinition[] availableBuffs = GetAvailableBuffDefinitions(session);

        string[][] descriptionLines = availableBuffs
            .Select(GetBuffSelectionDescriptionLines)
            .ToArray();

        bool useSecondDescriptionLine = descriptionLines.Any(lines => lines.Length > 1);

        for (int index = 0; index < availableBuffs.Length; index++)
        {
            DailyBuffDefinition buff = availableBuffs[index];
            bool selected = session.SelectedBuffIds.Contains(buff.Id);

            var selectButton = new ButtonBuilder()
                .WithLabel(selected ? "Selected" : "Select")
                .WithCustomId(
                    $"slotsdaily:buff:{session.UserId}:{session.SessionId}:{buff.Id}")
                .WithStyle(selected ? ButtonStyle.Success : ButtonStyle.Secondary);

            container.WithSection(
                new SectionBuilder()
                    .WithTextDisplay(BuildBuffSelectionSectionText(
                        buff,
                        descriptionLines[index],
                        useSecondDescriptionLine))
                    .WithAccessory(selectButton));
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

    private const string BlankDescriptionLine = "\u2800";

    private static string[] GetBuffSelectionDescriptionLines(DailyBuffDefinition buff) =>
        buff.Id switch
        {
            "row-left" => ["Shift selected row left by", "one."],
            "row-right" => ["Shift selected row right by", "one."],
            "column-up" => ["Shift selected column up by", "one."],
            "column-down" => ["Shift selected column down", "by one."],
            "reroll" => ["Reroll one selected symbol."],
            "lucky-spin" => ["Reveal 1 extra solution next", "spin."],
            "wild" => ["Solve 1 cell and make it Wild."],
            "double-payout" => ["Double your next Spin payout."],
            "jackpot-boost" => [$"Next new Jackpot payout ×{JackpotBoostMultiplier}."],
            "perfect-spin" => ["Next Spin matches the", "solution."],

            "cash-injection" => ["Gain $1,000 immediately."],
            "insider-tip" => ["Set a cell to its true solution", "without revealing it."],
            "compound-interest" => ["All future Spin payouts", "earn +50%."],
            "four-kind-jackpots" => ["4-in-a-row can now trigger", "Jackpots."],
            "mega-jackpots" => ["Base Jackpot multiplier becomes", "×400 for this run."],
            "cherry-premium" => ["Cherries are worth $30", "for this run."],
            "melon-scrambler" => ["Replace all Watermelons with", "random non-Watermelons."],
            "lemon-aid" => ["Solve every Lemon on the", "solution board."],

            "overtime" => ["Add 2 Spins to this run."],
            "peek" => ["Reveal 1 random unsolved", "solution cell now."],
            "mirror-row" => ["Reverse the selected row."],
            "mirror-column" => ["Reverse the selected column."],
            "shuffle-row" => ["Randomly shuffle selected row."],
            "shuffle-column" => ["Randomly shuffle selected column."],
            "clone-symbol" => ["Copy selected symbol into", "1 random other cell."],
            "market-corner" => ["Double selected symbol payout", "for this run."],
            "research-grant" => ["Solution reveals now pay $50", "for this run."],
            "coupon-book" => ["All future buff costs are", "50% cheaper."],
            "row-scout" => ["Reveal 1 unsolved solution", "cell in selected row."],
            "column-scout" => ["Reveal 1 unsolved solution", "cell in selected column."],
            _ => [buff.Effect]
        };

    private static string BuildBuffSelectionSectionText(
        DailyBuffDefinition buff,
        string[] descriptionLines,
        bool useSecondDescriptionLine)
    {
        string useText = buff.UseCount == 1 ? "1 use" : $"{buff.UseCount} uses";

        var builder = new StringBuilder(160);
        builder.AppendLine($"{buff.Emoji} **{buff.Name}**");
        builder.AppendLine($"-# ${buff.Cost:N0} • {useText}");
        builder.Append($"-# {descriptionLines[0]}");

        if (useSecondDescriptionLine)
        {
            builder.AppendLine();
            builder.Append("-# ");
            builder.Append(descriptionLines.Length > 1
                ? descriptionLines[1]
                : BlankDescriptionLine);
        }

        return builder.ToString();
    }

    private static MessageComponent BuildGameView(DailySlotsSession session)
    {
        bool[,] activePaylineCells = BuildActivePaylineCellMask(session);

        // 40/40 Components V2 budget:
        //   container 1
        //   header/payout text 1
        //   five slot rows 30
        //   balance/status text 1
        //   four-buff row 5
        //   spin row 2
        var container = new ContainerBuilder()
            .WithAccentColor(new Color(241, 196, 15))
            .WithTextDisplay(
                "## 🎰 Daily Slots 🎰\n" +
                "**Solution Board:**\n" +
                BuildSolutionGridText(session) +
                "\n\n**Slot Machine:**\n" +
                BuildPayoutLegend(session));

        for (int row = 0; row < BoardSize; row++)
        {
            var slotRow = new ActionRowBuilder();

            for (int column = 0; column < BoardSize; column++)
            {
                int symbolIndex = session.SlotSymbolIndexes[row, column];
                string emoji = symbolIndex switch
                {
                    WildSymbolIndex => "🃏",
                    >= 0 => DailySymbols[symbolIndex],
                    _ => "❔"
                };

                slotRow.WithButton(
                    new ButtonBuilder()
                        .WithCustomId(
                            $"slotsdaily:cell:{session.UserId}:{session.SessionId}:{row}:{column}")
                        .WithStyle(activePaylineCells[row, column]
                            ? ButtonStyle.Success
                            : ButtonStyle.Secondary)
                        .WithEmote(new Emoji(emoji)));
            }

            container.WithActionRow(slotRow);
        }

        container.WithTextDisplay(
            $"**Balance:** ${session.Balance:N0} • " +
            $"**Spins Remaining:** {session.SpinsRemaining}/{session.MaxSpinsThisRun}\n" +
            session.StatusMessage);

        var buffRow = new ActionRowBuilder();

        foreach (DailyBuffState buffState in session.Buffs)
        {
            bool hasUses = buffState.UsesRemaining > 0;
            long buffCost = GetBuffCost(session, buffState.Definition);
            bool canAfford = session.Balance >= buffCost;
            bool isAvailable = hasUses && canAfford;

            buffRow.WithButton(
                new ButtonBuilder()
                    .WithLabel(
                        $"${buffCost:N0} " +
                        $"{buffState.Definition.Emoji} " +
                        $"{buffState.UsesRemaining}/{buffState.Definition.UseCount}")
                    .WithCustomId(
                        $"slotsdaily:use:{session.UserId}:{session.SessionId}:{buffState.Definition.Id}")
                    .WithStyle(isAvailable ? ButtonStyle.Primary : ButtonStyle.Secondary)
                    .WithDisabled(!isAvailable));
        }

        container.WithActionRow(buffRow);

        bool spinDisabled = session.SpinsRemaining <= 0 || session.IsSolutionComplete;

        container.WithActionRow(
            new ActionRowBuilder()
                .WithButton(
                    "Spin Slots",
                    $"slotsdaily:spin:{session.UserId}:{session.SessionId}",
                    ButtonStyle.Danger,
                    emote: new Emoji("🎰"),
                    disabled: spinDisabled));

        return new ComponentBuilderV2()
            .WithContainer(container)
            .Build();
    }

    private static string BuildPayoutLegend(DailySlotsSession session)
    {
        var builder = new StringBuilder(220);
        builder.Append("**Payouts:** ");

        for (int i = 0; i < DailySymbols.Length; i++)
        {
            if (i > 0)
                builder.Append(" • ");

            builder.Append(DailySymbols[i]);
            builder.Append(" $");
            builder.Append(session.CurrentSymbolPayoutValues[i].ToString("N0"));
        }

        builder.AppendLine();
        builder.Append(
            $"**Jackpot:** {(session.FourInARowJackpotsEnabled ? "4+" : "5")}-in-a-row " +
            $"• symbol value ×{session.JackpotMultiplier} " +
            $"• **Solution reveal:** +${session.SolutionRevealValue}");

        if (session.CompoundInterestActive)
            builder.Append(" • **Interest:** +50% Spin payouts");

        if (session.BuffCostDiscountActive)
            builder.Append(" • **Buff costs:** 50% off");

        return builder.ToString();
    }

    private static string BuildSolutionGridText(DailySlotsSession session)
    {
        var builder = new StringBuilder(128);

        for (int row = 0; row < BoardSize; row++)
        {
            for (int column = 0; column < BoardSize; column++)
            {
                if (session.RevealedSolutionCells[row, column])
                {
                    int symbolIndex = session.SolutionSymbolIndexes[row, column];
                    builder.Append(DailySymbols[symbolIndex]);
                }
                else
                {
                    builder.Append("⬛");
                }
            }

            if (row < BoardSize - 1)
                builder.AppendLine();
        }

        return builder.ToString();
    }

    private static int[,] BuildDailySolution(DateOnly date)
    {
        var solution = new int[BoardSize, BoardSize];

        for (int cellIndex = 0; cellIndex < BoardSize * BoardSize; cellIndex++)
        {
            // Hash each position separately so today's board is stable across bot
            // restarts and identical for every player, while spins remain random.
            string seedText = $"NuttyBot-DailySlots|{date:yyyy-MM-dd}|{cellIndex}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seedText));
            uint value = BitConverter.ToUInt32(hash, 0);

            int row = cellIndex / BoardSize;
            int column = cellIndex % BoardSize;
            solution[row, column] = (int)(value % DailySymbols.Length);
        }

        return solution;
    }

    private static DailyBuffDefinition[] BuildBuffRotation()
    {
        // Read once per newly-created lobby. We intentionally do not cache this value:
        // editing daily-slots-buff-seed.txt while the bot is running changes the next
        // /slotsdaily lobby immediately, while already-open lobbies keep their offer.
        int rotationSeed = ReadBuffRotationSeed();

        return BuffDefinitions
            .Select(buff => (
                Buff: buff,
                SortKey: GetBuffRotationSortKey(buff.Id, rotationSeed)))
            .OrderBy(entry => entry.SortKey)
            .ThenBy(entry => entry.Buff.Id, StringComparer.Ordinal)
            .Take(DailyBuffOfferCount)
            .Select(entry => entry.Buff)
            .ToArray();
    }

    private static int ReadBuffRotationSeed()
    {
        string seedFilePath = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            BuffRotationSeedFileName);

        try
        {
            if (!System.IO.File.Exists(seedFilePath))
                return BuffRotationFallbackSeed;

            string seedText = System.IO.File.ReadAllText(seedFilePath).Trim();

            return int.TryParse(seedText, out int seed)
                ? seed
                : BuffRotationFallbackSeed;
        }
        catch
        {
            // A temporary read/permission problem should never prevent the game from
            // opening. Fall back to the hard-coded seed and try the file again next time.
            return BuffRotationFallbackSeed;
        }
    }

    private static ulong GetBuffRotationSortKey(string buffId, int rotationSeed)
    {
        string seedText = $"NuttyBot-DailySlots-Buffs|{rotationSeed}|{buffId}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seedText));
        return BitConverter.ToUInt64(hash, 0);
    }

    private static DailyBuffDefinition[] GetAvailableBuffDefinitions(DailySlotsSession session) =>
        session.AvailableBuffIds
            .Select(id => BuffDefinitions.First(buff => buff.Id == id))
            .ToArray();

    private static DailyBuffDefinition[] GetSelectedBuffDefinitions(DailySlotsSession session) =>
        BuffDefinitions
            .Where(buff => session.SelectedBuffIds.Contains(buff.Id))
            .OrderBy(buff => buff.Cost)
            .ThenBy(buff => Array.IndexOf(BuffDefinitions, buff))
            .ToArray();

    private bool TryGetSession(
        ulong guildId,
        ulong userId,
        string sessionId,
        out DailySlotsSession? session)
    {
        if (_sessions.TryGetValue((guildId, userId), out session) &&
            session.SessionId == sessionId)
        {
            return true;
        }

        session = null;
        return false;
    }

    private static Task UpdateMessageAsync(
        SocketMessageComponent component,
        MessageComponent view)
    {
        return component.UpdateAsync(message =>
        {
            message.Components = view;
        });
    }

    private static Task RespondInvalidSessionAsync(SocketMessageComponent component) =>
        component.RespondAsync(
            "This Daily Slots session is no longer active. Run /slotsdaily to open a new one.",
            ephemeral: true);

    private sealed class DailySlotsSession(
        ulong guildId,
        ulong userId,
        string sessionId)
    {
        public ulong GuildId { get; } = guildId;
        public ulong UserId { get; } = userId;
        public string SessionId { get; } = sessionId;
        public DateOnly DailyDate { get; } = DateOnly.FromDateTime(DateTime.UtcNow);
        public string[] AvailableBuffIds { get; private set; } = [];
        public HashSet<string> SelectedBuffIds { get; } = [];

        public bool Started { get; private set; }
        public DateOnly SolutionDate { get; private set; }
        public int[,] SolutionSymbolIndexes { get; private set; } = new int[BoardSize, BoardSize];
        public bool[,] RevealedSolutionCells { get; } = new bool[BoardSize, BoardSize];
        public bool[,,] EliminatedSolutionSymbols { get; } =
            new bool[BoardSize, BoardSize, DailySymbols.Length];
        public int[,] SlotSymbolIndexes { get; } = CreateEmptySlotBoard();
        public int[,] WildcardOriginalSymbolIndexes { get; } = CreateEmptySlotBoard();
        public long[,] SlotTokenIds { get; } = new long[BoardSize, BoardSize];
        public long[,] SuppressedNaturalRevealTokenIds { get; } = new long[BoardSize, BoardSize];
        public int[] CurrentSymbolPayoutValues { get; } = (int[])SymbolPayoutValues.Clone();
        public List<DailyBuffState> Buffs { get; } = [];
        public HashSet<string> PaidPaylineSignaturesThisSpin { get; } = [];

        public long Balance { get; set; }
        public int SpinsRemaining { get; set; } = MaxSpins;
        public int MaxSpinsThisRun { get; set; } = MaxSpins;
        public bool HasSpunAtLeastOnce { get; set; }
        public string StatusMessage { get; set; } = "🎰 • Spin slots to begin.";
        public string? PendingTargetBuffId { get; set; }
        public int GuaranteedSolutionHitsNextSpin { get; set; }
        public bool DoublePayoutNextSpin { get; set; }
        public bool JackpotBoostPending { get; set; }
        public bool PerfectSpinNextSpin { get; set; }
        public bool CompoundInterestActive { get; set; }
        public bool FourInARowJackpotsEnabled { get; set; }
        public bool BuffCostDiscountActive { get; set; }
        public int JackpotMultiplier { get; set; } = BaseJackpotMultiplier;
        public int SolutionRevealValue { get; set; } = BaseSolutionRevealReward;
        public long NextSlotTokenId { get; set; } = 1;

        public void InitializeDailyBuffSelection()
        {
            if (AvailableBuffIds.Length > 0)
                return;

            DailyBuffDefinition[] rotation = BuildBuffRotation();
            AvailableBuffIds = rotation.Select(buff => buff.Id).ToArray();

            // Always open the selection lobby at 0/4. The daily/seeded rotation
            // controls which 10 buffs are offered, but the player chooses all four.
            SelectedBuffIds.Clear();
        }

        public bool IsSolutionComplete
        {
            get
            {
                for (int row = 0; row < BoardSize; row++)
                {
                    for (int column = 0; column < BoardSize; column++)
                    {
                        if (!RevealedSolutionCells[row, column])
                            return false;
                    }
                }

                return true;
            }
        }

        public void StartGame()
        {
            if (Started)
                return;

            SolutionDate = DailyDate;
            SolutionSymbolIndexes = BuildDailySolution(SolutionDate);
            Balance = 0;
            SpinsRemaining = MaxSpins;
            MaxSpinsThisRun = MaxSpins;
            HasSpunAtLeastOnce = false;
            StatusMessage = "🎰 • Spin slots to begin.";
            PendingTargetBuffId = null;
            GuaranteedSolutionHitsNextSpin = 0;
            DoublePayoutNextSpin = false;
            JackpotBoostPending = false;
            PerfectSpinNextSpin = false;
            CompoundInterestActive = false;
            FourInARowJackpotsEnabled = false;
            BuffCostDiscountActive = false;
            JackpotMultiplier = BaseJackpotMultiplier;
            SolutionRevealValue = BaseSolutionRevealReward;
            Array.Copy(SymbolPayoutValues, CurrentSymbolPayoutValues, SymbolPayoutValues.Length);
            NextSlotTokenId = 1;
            PaidPaylineSignaturesThisSpin.Clear();
            ClearBoard(WildcardOriginalSymbolIndexes, -1);
            Array.Clear(SlotTokenIds, 0, SlotTokenIds.Length);
            Array.Clear(SuppressedNaturalRevealTokenIds, 0, SuppressedNaturalRevealTokenIds.Length);
            Array.Clear(EliminatedSolutionSymbols, 0, EliminatedSolutionSymbols.Length);

            Buffs.Clear();

            foreach (DailyBuffDefinition buff in GetSelectedBuffDefinitions(this))
            {
                Buffs.Add(new DailyBuffState(buff, buff.UseCount));
            }

            Started = true;
        }

        public DailySlotsSessionSnapshot CreateSnapshot() => new(
            Started,
            SolutionDate,
            (int[,])SolutionSymbolIndexes.Clone(),
            (bool[,])RevealedSolutionCells.Clone(),
            (bool[,,])EliminatedSolutionSymbols.Clone(),
            (int[,])SlotSymbolIndexes.Clone(),
            (int[,])WildcardOriginalSymbolIndexes.Clone(),
            (long[,])SlotTokenIds.Clone(),
            (long[,])SuppressedNaturalRevealTokenIds.Clone(),
            (int[])CurrentSymbolPayoutValues.Clone(),
            SelectedBuffIds.ToArray(),
            Buffs.Select(x => (x.Definition, x.UsesRemaining)).ToArray(),
            PaidPaylineSignaturesThisSpin.ToArray(),
            Balance,
            SpinsRemaining,
            MaxSpinsThisRun,
            HasSpunAtLeastOnce,
            StatusMessage,
            PendingTargetBuffId,
            GuaranteedSolutionHitsNextSpin,
            DoublePayoutNextSpin,
            JackpotBoostPending,
            PerfectSpinNextSpin,
            CompoundInterestActive,
            FourInARowJackpotsEnabled,
            BuffCostDiscountActive,
            JackpotMultiplier,
            SolutionRevealValue,
            NextSlotTokenId);

        public void RestoreSnapshot(DailySlotsSessionSnapshot snapshot)
        {
            Started = snapshot.Started;
            SolutionDate = snapshot.SolutionDate;
            SolutionSymbolIndexes = (int[,])snapshot.SolutionSymbolIndexes.Clone();
            CopyArray(snapshot.RevealedSolutionCells, RevealedSolutionCells);
            CopyArray(snapshot.EliminatedSolutionSymbols, EliminatedSolutionSymbols);
            CopyArray(snapshot.SlotSymbolIndexes, SlotSymbolIndexes);
            CopyArray(snapshot.WildcardOriginalSymbolIndexes, WildcardOriginalSymbolIndexes);
            CopyArray(snapshot.SlotTokenIds, SlotTokenIds);
            CopyArray(snapshot.SuppressedNaturalRevealTokenIds, SuppressedNaturalRevealTokenIds);
            Array.Copy(
                snapshot.CurrentSymbolPayoutValues,
                CurrentSymbolPayoutValues,
                CurrentSymbolPayoutValues.Length);

            SelectedBuffIds.Clear();
            foreach (string id in snapshot.SelectedBuffIds)
                SelectedBuffIds.Add(id);

            Buffs.Clear();
            foreach ((DailyBuffDefinition definition, int usesRemaining) in snapshot.Buffs)
                Buffs.Add(new DailyBuffState(definition, usesRemaining));

            PaidPaylineSignaturesThisSpin.Clear();
            foreach (string key in snapshot.PaidPaylineSignaturesThisSpin)
                PaidPaylineSignaturesThisSpin.Add(key);

            Balance = snapshot.Balance;
            SpinsRemaining = snapshot.SpinsRemaining;
            MaxSpinsThisRun = snapshot.MaxSpinsThisRun;
            HasSpunAtLeastOnce = snapshot.HasSpunAtLeastOnce;
            StatusMessage = snapshot.StatusMessage;
            PendingTargetBuffId = snapshot.PendingTargetBuffId;
            GuaranteedSolutionHitsNextSpin = snapshot.GuaranteedSolutionHitsNextSpin;
            DoublePayoutNextSpin = snapshot.DoublePayoutNextSpin;
            JackpotBoostPending = snapshot.JackpotBoostPending;
            PerfectSpinNextSpin = snapshot.PerfectSpinNextSpin;
            CompoundInterestActive = snapshot.CompoundInterestActive;
            FourInARowJackpotsEnabled = snapshot.FourInARowJackpotsEnabled;
            BuffCostDiscountActive = snapshot.BuffCostDiscountActive;
            JackpotMultiplier = snapshot.JackpotMultiplier;
            SolutionRevealValue = snapshot.SolutionRevealValue;
            NextSlotTokenId = snapshot.NextSlotTokenId;
        }

        private static void CopyArray<T>(T[,] source, T[,] destination)
        {
            for (int row = 0; row < BoardSize; row++)
            {
                for (int column = 0; column < BoardSize; column++)
                    destination[row, column] = source[row, column];
            }
        }

        private static void CopyArray<T>(T[,,] source, T[,,] destination)
        {
            for (int row = 0; row < BoardSize; row++)
            {
                for (int column = 0; column < BoardSize; column++)
                {
                    for (int symbol = 0; symbol < DailySymbols.Length; symbol++)
                        destination[row, column, symbol] = source[row, column, symbol];
                }
            }
        }

        private static int[,] CreateEmptySlotBoard()
        {
            var board = new int[BoardSize, BoardSize];

            for (int row = 0; row < BoardSize; row++)
            {
                for (int column = 0; column < BoardSize; column++)
                {
                    board[row, column] = -1;
                }
            }

            return board;
        }
    }

    private sealed record DailySlotsSessionSnapshot(
        bool Started,
        DateOnly SolutionDate,
        int[,] SolutionSymbolIndexes,
        bool[,] RevealedSolutionCells,
        bool[,,] EliminatedSolutionSymbols,
        int[,] SlotSymbolIndexes,
        int[,] WildcardOriginalSymbolIndexes,
        long[,] SlotTokenIds,
        long[,] SuppressedNaturalRevealTokenIds,
        int[] CurrentSymbolPayoutValues,
        string[] SelectedBuffIds,
        (DailyBuffDefinition Definition, int UsesRemaining)[] Buffs,
        string[] PaidPaylineSignaturesThisSpin,
        long Balance,
        int SpinsRemaining,
        int MaxSpinsThisRun,
        bool HasSpunAtLeastOnce,
        string StatusMessage,
        string? PendingTargetBuffId,
        int GuaranteedSolutionHitsNextSpin,
        bool DoublePayoutNextSpin,
        bool JackpotBoostPending,
        bool PerfectSpinNextSpin,
        bool CompoundInterestActive,
        bool FourInARowJackpotsEnabled,
        bool BuffCostDiscountActive,
        int JackpotMultiplier,
        int SolutionRevealValue,
        long NextSlotTokenId);

    private sealed class DailyBuffState(
        DailyBuffDefinition definition,
        int usesRemaining)
    {
        public DailyBuffDefinition Definition { get; } = definition;
        public int UsesRemaining { get; set; } = usesRemaining;
    }

    private sealed record DailyBuffDefinition(
        string Id,
        string Name,
        string Emoji,
        long Cost,
        int UseCount,
        DailyBuffTargetMode TargetMode,
        string Effect);

    private sealed record PaylineMatch(
        int SymbolIndex,
        (int Row, int Column)[] Cells);

    private sealed record PaylinePayoutResult(
        long Payout,
        int LineCount,
        bool UsedJackpotBoost);

    private enum DailyBuffTargetMode
    {
        None,
        Cell
    }
}
