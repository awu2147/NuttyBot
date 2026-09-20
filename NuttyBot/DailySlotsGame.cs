using Discord;
using Discord.WebSocket;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace NuttyBot;

internal sealed class DailySlotsGame
{
    private const int MaxSelectedBuffs = 4;
    private const int BoardSize = 5;
    private const int MaxSpins = 10;
    private const int WildSymbolIndex = -2;
    private const int FiveInARowMultiplier = 100;
    private const int JackpotBoostMultiplier = 5;
    private const int SolutionRevealReward = 20;

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

    // Every symbol shown on a normal spin pays this value. A completed
    // horizontal, vertical, or diagonal 5-in-a-row pays that symbol's value x100.
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
            "Instantly solves the selected position and turns the current symbol into a Wildcard. A Wildcard symbol can complete a 5-in-a-row."),
        new(
            "double-payout",
            "Double Payout",
            "💵",
            200,
            1,
            DailyBuffTargetMode.None,
            "Double all money earned when you press Spin next."),
        new(
            "jackpot-boost",
            "Jackpot Boost",
            "💰",
            555,
            1,
            DailyBuffTargetMode.None,
            $"The next new 5-in-a-row payout is multiplied by {JackpotBoostMultiplier}."),
        new(
            "perfect-spin",
            "Perfect Spin",
            "🌟",
            6_000,
            1,
            DailyBuffTargetMode.None,
            "Your next spin exactly matches the daily solution board.")
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
                else if (session.Balance < buffState.Definition.Cost)
                {
                    session.StatusMessage = BuildStatus(
                        "💰",
                        $"You need ${buffState.Definition.Cost:N0} to use {buffState.Definition.Name}.");
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
                else if (session.SpinsRemaining <= 0 || session.IsSolutionComplete)
                {
                    session.StatusMessage = BuildStatus(
                        buffState.Definition.Emoji,
                        "There is no future spin available for that buff.");
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
                else if (session.Balance < buffState.Definition.Cost)
                {
                    session.PendingTargetBuffId = null;
                    session.StatusMessage = BuildStatus(
                        "💰",
                        $"You need ${buffState.Definition.Cost:N0} to use {buffState.Definition.Name}.");
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
                SpendBuff(session, buffState);
                session.GuaranteedSolutionHitsNextSpin++;
                session.StatusMessage =
                    BuildStatus(buff.Emoji, "Lucky Spin used! Next spin will reveal " +
                    $"{session.GuaranteedSolutionHitsNextSpin} bonus solution " +
                    $"{(session.GuaranteedSolutionHitsNextSpin == 1 ? "symbol" : "symbols")} " +
                    "after the regular spin resolves.");
                break;

            case "double-payout":
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
                    "Double Payout used! All money from the next Spin will be doubled.");
                break;

            case "jackpot-boost":
                if (session.JackpotBoostPending)
                {
                    session.StatusMessage = BuildStatus(
                        buff.Emoji,
                        "Jackpot Boost is already waiting for your next 5-in-a-row.");
                    return;
                }

                SpendBuff(session, buffState);
                session.JackpotBoostPending = true;
                session.StatusMessage = BuildStatus(
                    buff.Emoji,
                    $"Jackpot Boost used! Your next new 5-in-a-row payout will be x{JackpotBoostMultiplier}.");
                break;

            case "perfect-spin":
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
        // Wildcards use -2 and are still valid targets for shifts/rerolls.
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
                    session.Balance += SolutionRevealReward;

                PaylinePayoutResult wildResult = PayNewFiveInARows(session);
                session.Balance += wildResult.Payout;

                session.StatusMessage = BuildManipulationStatus(
                    buff,
                    $"Symbol {FormatSlotSymbol(oldSymbol)} became a Wildcard {buff.Emoji}",
                    wildResult,
                    newlySolved ? 1 : 0,
                    solutionCompleted: !wasSolutionComplete && session.IsSolutionComplete);
                break;

            default:
                // This path should never be reached because only targeted buffs
                // can arrive here, but refunding keeps the state safe if a future
                // definition is accidentally misconfigured.
                session.Balance += buff.Cost;
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
            session.Balance += (long)newlyRevealed * SolutionRevealReward;

        PaylinePayoutResult result = PayNewFiveInARows(session);
        session.Balance += result.Payout;
        session.StatusMessage = BuildManipulationStatus(
            buff,
            actionText,
            result,
            newlyRevealed,
            solutionCompleted: !wasSolutionComplete && session.IsSolutionComplete);
    }

    private static string BuildManipulationStatus(
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
                $"{(result.LineCount == 1 ? string.Empty : "s")}" +
                $"!");
        }

        if (solutionCompleted)
            status.Append(" Solution complete!");

        long totalPayout = result.Payout + ((long)newlyRevealed * SolutionRevealReward);
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
            _ => $"{buff.Name} selected — choose a target symbol."
        };

        return BuildStatus(buff.Emoji, prompt);
    }

    private static void SpendBuff(
        DailySlotsSession session,
        DailyBuffState buffState)
    {
        session.Balance -= buffState.Definition.Cost;
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
        // genuinely new symbol entering a payline from the same five pieces merely
        // being rotated within that payline.
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
        long solutionReward = (long)newlyRevealed * SolutionRevealReward;
        session.Balance += solutionReward;

        long basePayout = CalculateBasePayout(session.SlotSymbolIndexes);
        PaylinePayoutResult paylineResult = PayNewFiveInARows(session);
        long spinPayout = basePayout + paylineResult.Payout;

        if (doublePayout)
            spinPayout *= 2;

        session.Balance += spinPayout;
        session.SpinsRemaining--;
        session.HasSpunAtLeastOnce = true;

        long totalPayout = spinPayout + solutionReward;

        var status = new StringBuilder(160);
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

        if (doublePayout)
            status.Append(" Double Payout applied!");

        if (session.IsSolutionComplete)
            status.Append(" Solution complete!");
        else if (session.SpinsRemaining <= 0)
            status.Append(" No spins remaining.");

        // Keep the combined payout last so regular spins match buff-action status formatting.
        // totalPayout already includes base symbol payout, jackpot payouts after boosts,
        // Double Payout, and the $20 reward for each newly revealed solution cell.
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

                if (slotSymbolIndex == session.SolutionSymbolIndexes[row, column])
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

    private static long CalculateBasePayout(int[,] board)
    {
        long payout = 0;

        for (int row = 0; row < BoardSize; row++)
        {
            for (int column = 0; column < BoardSize; column++)
            {
                int symbolIndex = board[row, column];

                if (symbolIndex >= 0)
                    payout += SymbolPayoutValues[symbolIndex];
            }
        }

        return payout;
    }

    private static PaylinePayoutResult PayNewFiveInARows(DailySlotsSession session)
    {
        long payout = 0;
        int lineCount = 0;
        bool usedJackpotBoost = false;

        for (int row = 0; row < BoardSize; row++)
        {
            if (!TryGetHorizontalPaylineSymbol(session.SlotSymbolIndexes, row, out int symbolIndex))
                continue;

            string signature = BuildHorizontalPaylineSignature(session, row);

            if (!session.PaidPaylineSignaturesThisSpin.Add(signature))
                continue;

            long linePayout = SymbolPayoutValues[symbolIndex] * FiveInARowMultiplier;

            if (session.JackpotBoostPending)
            {
                linePayout *= JackpotBoostMultiplier;
                session.JackpotBoostPending = false;
                usedJackpotBoost = true;
            }

            payout += linePayout;
            lineCount++;
        }

        for (int column = 0; column < BoardSize; column++)
        {
            if (!TryGetVerticalPaylineSymbol(session.SlotSymbolIndexes, column, out int symbolIndex))
                continue;

            string signature = BuildVerticalPaylineSignature(session, column);

            if (!session.PaidPaylineSignaturesThisSpin.Add(signature))
                continue;

            long linePayout = SymbolPayoutValues[symbolIndex] * FiveInARowMultiplier;

            if (session.JackpotBoostPending)
            {
                linePayout *= JackpotBoostMultiplier;
                session.JackpotBoostPending = false;
                usedJackpotBoost = true;
            }

            payout += linePayout;
            lineCount++;
        }

        for (int diagonal = 0; diagonal < 2; diagonal++)
        {
            if (!TryGetDiagonalPaylineSymbol(session.SlotSymbolIndexes, diagonal, out int symbolIndex))
                continue;

            string signature = BuildDiagonalPaylineSignature(session, diagonal);

            if (!session.PaidPaylineSignaturesThisSpin.Add(signature))
                continue;

            long linePayout = SymbolPayoutValues[symbolIndex] * FiveInARowMultiplier;

            if (session.JackpotBoostPending)
            {
                linePayout *= JackpotBoostMultiplier;
                session.JackpotBoostPending = false;
                usedJackpotBoost = true;
            }

            payout += linePayout;
            lineCount++;
        }

        return new PaylinePayoutResult(payout, lineCount, usedJackpotBoost);
    }

    private static string BuildHorizontalPaylineSignature(
        DailySlotsSession session,
        int row)
    {
        var tokenIds = new long[BoardSize];

        for (int column = 0; column < BoardSize; column++)
            tokenIds[column] = session.SlotTokenIds[row, column];

        return BuildPaylineSignature($"R{row}", tokenIds);
    }

    private static string BuildVerticalPaylineSignature(
        DailySlotsSession session,
        int column)
    {
        var tokenIds = new long[BoardSize];

        for (int row = 0; row < BoardSize; row++)
            tokenIds[row] = session.SlotTokenIds[row, column];

        return BuildPaylineSignature($"C{column}", tokenIds);
    }

    private static string BuildDiagonalPaylineSignature(
        DailySlotsSession session,
        int diagonal)
    {
        var tokenIds = new long[BoardSize];

        for (int row = 0; row < BoardSize; row++)
        {
            int column = diagonal == 0
                ? row
                : BoardSize - 1 - row;

            tokenIds[row] = session.SlotTokenIds[row, column];
        }

        return BuildPaylineSignature($"D{diagonal}", tokenIds);
    }

    private static string BuildPaylineSignature(string lineKey, long[] tokenIds)
    {
        // Order is intentionally ignored. Rotating the same five pieces inside a
        // winning row/column is still the same jackpot. A perpendicular shift that
        // moves a neighbouring piece into the line changes the token set and can
        // therefore award that line once more.
        Array.Sort(tokenIds);
        return $"{lineKey}:{string.Join(",", tokenIds)}";
    }

    private static bool TryGetHorizontalPaylineSymbol(
        int[,] board,
        int row,
        out int symbolIndex)
    {
        symbolIndex = -1;

        for (int column = 0; column < BoardSize; column++)
        {
            int current = board[row, column];

            if (current == -1)
                return false;

            if (current == WildSymbolIndex)
                continue;

            if (symbolIndex < 0)
            {
                symbolIndex = current;
            }
            else if (symbolIndex != current)
            {
                return false;
            }
        }

        if (symbolIndex < 0)
            symbolIndex = GetHighestPayingSymbolIndex();

        return true;
    }

    private static bool TryGetVerticalPaylineSymbol(
        int[,] board,
        int column,
        out int symbolIndex)
    {
        symbolIndex = -1;

        for (int row = 0; row < BoardSize; row++)
        {
            int current = board[row, column];

            if (current == -1)
                return false;

            if (current == WildSymbolIndex)
                continue;

            if (symbolIndex < 0)
            {
                symbolIndex = current;
            }
            else if (symbolIndex != current)
            {
                return false;
            }
        }

        if (symbolIndex < 0)
            symbolIndex = GetHighestPayingSymbolIndex();

        return true;
    }

    private static bool TryGetDiagonalPaylineSymbol(
        int[,] board,
        int diagonal,
        out int symbolIndex)
    {
        symbolIndex = -1;

        for (int i = 0; i < BoardSize; i++)
        {
            int column = diagonal == 0
                ? i
                : BoardSize - 1 - i;

            int current = board[i, column];

            if (current == -1)
                return false;

            if (current == WildSymbolIndex)
                continue;

            if (symbolIndex < 0)
            {
                symbolIndex = current;
            }
            else if (symbolIndex != current)
            {
                return false;
            }
        }

        if (symbolIndex < 0)
            symbolIndex = GetHighestPayingSymbolIndex();

        return true;
    }

    private static bool[,] BuildActivePaylineCellMask(int[,] board)
    {
        var winningCells = new bool[BoardSize, BoardSize];

        // Derive highlighting from the current board rather than payout history.
        // Any board manipulation that creates or breaks a 5-in-a-row therefore
        // updates the green cells immediately on the next view rebuild.
        for (int row = 0; row < BoardSize; row++)
        {
            if (!TryGetHorizontalPaylineSymbol(board, row, out _))
                continue;

            for (int column = 0; column < BoardSize; column++)
                winningCells[row, column] = true;
        }

        for (int column = 0; column < BoardSize; column++)
        {
            if (!TryGetVerticalPaylineSymbol(board, column, out _))
                continue;

            for (int row = 0; row < BoardSize; row++)
                winningCells[row, column] = true;
        }

        for (int diagonal = 0; diagonal < 2; diagonal++)
        {
            if (!TryGetDiagonalPaylineSymbol(board, diagonal, out _))
                continue;

            for (int i = 0; i < BoardSize; i++)
            {
                int column = diagonal == 0
                    ? i
                    : BoardSize - 1 - i;

                winningCells[i, column] = true;
            }
        }

        return winningCells;
    }

    private static int GetHighestPayingSymbolIndex()
    {
        int bestIndex = 0;

        for (int i = 1; i < SymbolPayoutValues.Length; i++)
        {
            if (SymbolPayoutValues[i] > SymbolPayoutValues[bestIndex])
                bestIndex = i;
        }

        return bestIndex;
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
            .WithTextDisplay($"**Select buffs to begin ({selectedCount}/{MaxSelectedBuffs})**");

        // Discord performs text wrapping on the client, and trailing whitespace-like
        // glyphs do not reliably reserve rendered width/height. Keep the lobby
        // deterministic instead: descriptions that need two lines are split explicitly.
        // If any description uses a second line, every other buff receives one blank
        // second description line so all Sections have the same height.
        string[][] descriptionLines = BuffDefinitions
            .Select(GetBuffSelectionDescriptionLines)
            .ToArray();

        bool useSecondDescriptionLine = descriptionLines.Any(lines => lines.Length > 1);

        for (int index = 0; index < BuffDefinitions.Length; index++)
        {
            DailyBuffDefinition buff = BuffDefinitions[index];
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
            "jackpot-boost" => [$"Next new jackpot payout ×{JackpotBoostMultiplier}."],
            "perfect-spin" => ["Next spin matches the", "solution."],
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
        bool[,] activePaylineCells = BuildActivePaylineCellMask(session.SlotSymbolIndexes);

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
                BuildPayoutLegend());

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
            $"**Spins Remaining:** {session.SpinsRemaining}/{MaxSpins}\n" +
            session.StatusMessage);

        var buffRow = new ActionRowBuilder();

        foreach (DailyBuffState buffState in session.Buffs)
        {
            bool hasUses = buffState.UsesRemaining > 0;
            bool canAfford = session.Balance >= buffState.Definition.Cost;
            bool isAvailable = hasUses && canAfford;

            buffRow.WithButton(
                new ButtonBuilder()
                    .WithLabel(
                        $"${buffState.Definition.Cost:N0} " +
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

    private static string BuildPayoutLegend()
    {
        var builder = new StringBuilder(180);
        builder.Append("**Payouts:** ");

        for (int i = 0; i < DailySymbols.Length; i++)
        {
            if (i > 0)
            {
                if (i == 6)
                    builder.AppendLine();
                else
                    builder.Append(" • ");
            }

            builder.Append(DailySymbols[i]);
            builder.Append(" $");
            builder.Append(SymbolPayoutValues[i].ToString("N0"));
        }

        builder.AppendLine();
        builder.Append($"**5-in-a-row:** symbol value ×{FiveInARowMultiplier} • **Solution reveal:** +${SolutionRevealReward}");
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
        public HashSet<string> SelectedBuffIds { get; } =
        [
            "row-right",
            "column-down",
            "lucky-spin",
            "wild"
        ];

        public bool Started { get; private set; }
        public DateOnly SolutionDate { get; private set; }
        public int[,] SolutionSymbolIndexes { get; private set; } = new int[BoardSize, BoardSize];
        public bool[,] RevealedSolutionCells { get; } = new bool[BoardSize, BoardSize];
        public bool[,,] EliminatedSolutionSymbols { get; } =
            new bool[BoardSize, BoardSize, DailySymbols.Length];
        public int[,] SlotSymbolIndexes { get; } = CreateEmptySlotBoard();
        public int[,] WildcardOriginalSymbolIndexes { get; } = CreateEmptySlotBoard();
        public long[,] SlotTokenIds { get; } = new long[BoardSize, BoardSize];
        public List<DailyBuffState> Buffs { get; } = [];
        public HashSet<string> PaidPaylineSignaturesThisSpin { get; } = [];

        public long Balance { get; set; }
        public int SpinsRemaining { get; set; } = MaxSpins;
        public bool HasSpunAtLeastOnce { get; set; }
        public string StatusMessage { get; set; } = "🎰 • Spin slots to begin.";
        public string? PendingTargetBuffId { get; set; }
        public int GuaranteedSolutionHitsNextSpin { get; set; }
        public bool DoublePayoutNextSpin { get; set; }
        public bool JackpotBoostPending { get; set; }
        public bool PerfectSpinNextSpin { get; set; }
        public long NextSlotTokenId { get; set; } = 1;

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

            SolutionDate = DateOnly.FromDateTime(DateTime.UtcNow);
            SolutionSymbolIndexes = BuildDailySolution(SolutionDate);
            Balance = 0;
            SpinsRemaining = MaxSpins;
            HasSpunAtLeastOnce = false;
            StatusMessage = "🎰 • Spin slots to begin.";
            PendingTargetBuffId = null;
            GuaranteedSolutionHitsNextSpin = 0;
            DoublePayoutNextSpin = false;
            JackpotBoostPending = false;
            PerfectSpinNextSpin = false;
            NextSlotTokenId = 1;
            PaidPaylineSignaturesThisSpin.Clear();
            ClearBoard(WildcardOriginalSymbolIndexes, -1);
            Array.Clear(SlotTokenIds, 0, SlotTokenIds.Length);
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
            SelectedBuffIds.ToArray(),
            Buffs.Select(x => (x.Definition, x.UsesRemaining)).ToArray(),
            PaidPaylineSignaturesThisSpin.ToArray(),
            Balance,
            SpinsRemaining,
            HasSpunAtLeastOnce,
            StatusMessage,
            PendingTargetBuffId,
            GuaranteedSolutionHitsNextSpin,
            DoublePayoutNextSpin,
            JackpotBoostPending,
            PerfectSpinNextSpin,
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
            HasSpunAtLeastOnce = snapshot.HasSpunAtLeastOnce;
            StatusMessage = snapshot.StatusMessage;
            PendingTargetBuffId = snapshot.PendingTargetBuffId;
            GuaranteedSolutionHitsNextSpin = snapshot.GuaranteedSolutionHitsNextSpin;
            DoublePayoutNextSpin = snapshot.DoublePayoutNextSpin;
            JackpotBoostPending = snapshot.JackpotBoostPending;
            PerfectSpinNextSpin = snapshot.PerfectSpinNextSpin;
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
        string[] SelectedBuffIds,
        (DailyBuffDefinition Definition, int UsesRemaining)[] Buffs,
        string[] PaidPaylineSignaturesThisSpin,
        long Balance,
        int SpinsRemaining,
        bool HasSpunAtLeastOnce,
        string StatusMessage,
        string? PendingTargetBuffId,
        int GuaranteedSolutionHitsNextSpin,
        bool DoublePayoutNextSpin,
        bool JackpotBoostPending,
        bool PerfectSpinNextSpin,
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
