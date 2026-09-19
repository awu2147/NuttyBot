using Discord;
using Discord.WebSocket;
using System.Security.Cryptography;
using System.Text;

namespace NuttyBot;

internal sealed class DailySlotsGame
{
    private const int MaxSelectedBuffs = 4;
    private const int BoardSize = 5;
    private const int MaxSpins = 10;
    private const int WildSymbolIndex = -2;
    private const int FiveInARowMultiplier = 100;
    private const int JackpotBoostMultiplier = 3;

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
        "🍒", "🍋", "🍊", "🍇", "🍉", "🔔",
        "💎", "👑", "🍀", "🪙", "💰", "🌟"
    ];

    // Every symbol shown on a normal spin pays this value. A completed
    // horizontal or vertical 5-in-a-row pays that symbol's value x100.
    // 🌟 is deliberately worth $100 so its 5-in-a-row jackpot is $10,000,
    // enough to fund Perfect Spin by itself.
    private static readonly int[] SymbolPayoutValues =
    [
        1, 2, 3, 4, 5, 6,
        8, 10, 15, 25, 50, 100
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
            "Shift the selected row left by one. The leftmost symbol wraps to the right. New solution matches are revealed."),
        new(
            "row-right",
            "Shift Row Right",
            "➡️",
            100,
            2,
            DailyBuffTargetMode.Cell,
            "Shift the selected row right by one. The rightmost symbol wraps to the left. New solution matches are revealed."),
        new(
            "column-up",
            "Shift Column Up",
            "⬆️",
            100,
            2,
            DailyBuffTargetMode.Cell,
            "Shift the selected column up by one. The top symbol wraps to the bottom. New solution matches are revealed."),
        new(
            "column-down",
            "Shift Column Down",
            "⬇️",
            100,
            2,
            DailyBuffTargetMode.Cell,
            "Shift the selected column down by one. The bottom symbol wraps to the top. New solution matches are revealed."),
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
            "After the next normal spin resolves, reveal one additional unsolved solution symbol. Stacks with additional uses."),
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
            650,
            1,
            DailyBuffTargetMode.None,
            "Double all money earned when you press Spin next."),
        new(
            "jackpot-boost",
            "Jackpot Boost",
            "💰",
            1_000,
            1,
            DailyBuffTargetMode.None,
            $"The next new 5-in-a-row payout is multiplied by {JackpotBoostMultiplier}."),
        new(
            "perfect-spin",
            "Perfect Spin",
            "🌟",
            10_000,
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

        // At 4/4, clicking a fifth grey buff intentionally changes nothing.
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
                    session.StatusMessage = "That buff is not in this run's loadout.";
                }
                else if (buffState.UsesRemaining <= 0)
                {
                    session.StatusMessage = $"{buffState.Definition.Emoji} {buffState.Definition.Name} has no uses remaining.";
                }
                else if (session.Balance < buffState.Definition.Cost)
                {
                    session.StatusMessage =
                        $"You need ${buffState.Definition.Cost:N0} to use {buffState.Definition.Name}.";
                }
                else if (buffState.Definition.TargetMode == DailyBuffTargetMode.Cell)
                {
                    if (!session.HasSpunAtLeastOnce)
                    {
                        session.StatusMessage = "Spin the slots once before using a board-manipulation buff.";
                    }
                    else if (session.PendingTargetBuffId == buffId)
                    {
                        session.PendingTargetBuffId = null;
                        session.StatusMessage = $"{buffState.Definition.Name} cancelled.";
                    }
                    else
                    {
                        session.PendingTargetBuffId = buffId;
                        session.StatusMessage = BuildTargetPrompt(buffState.Definition);
                    }
                }
                else if (session.SpinsRemaining <= 0 || session.IsSolutionComplete)
                {
                    session.StatusMessage = "There is no future spin available for that buff.";
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
                session.StatusMessage = "That target cell is invalid.";
                view = BuildGameView(session);
            }
            else if (string.IsNullOrWhiteSpace(session.PendingTargetBuffId))
            {
                // Cells are intentionally inert unless a targeted buff is armed.
                view = null;
            }
            else
            {
                DailyBuffState? buffState = session.Buffs
                    .FirstOrDefault(x => x.Definition.Id == session.PendingTargetBuffId);

                if (buffState is null || buffState.UsesRemaining <= 0)
                {
                    session.PendingTargetBuffId = null;
                    session.StatusMessage = "That buff is no longer available.";
                }
                else if (session.Balance < buffState.Definition.Cost)
                {
                    session.PendingTargetBuffId = null;
                    session.StatusMessage =
                        $"You need ${buffState.Definition.Cost:N0} to use {buffState.Definition.Name}.";
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
                    $"🍀 Lucky Spin used! Next spin will reveal " +
                    $"{session.GuaranteedSolutionHitsNextSpin} bonus solution " +
                    $"{(session.GuaranteedSolutionHitsNextSpin == 1 ? "symbol" : "symbols")} " +
                    "after the regular spin resolves.";
                break;

            case "double-payout":
                if (session.DoublePayoutNextSpin)
                {
                    session.StatusMessage = "💵 Double Payout is already armed for the next spin.";
                    return;
                }

                SpendBuff(session, buffState);
                session.DoublePayoutNextSpin = true;
                session.StatusMessage = "💵 Double Payout used! All money from the next Spin will be doubled.";
                break;

            case "jackpot-boost":
                if (session.JackpotBoostPending)
                {
                    session.StatusMessage = "💰 Jackpot Boost is already waiting for your next 5-in-a-row.";
                    return;
                }

                SpendBuff(session, buffState);
                session.JackpotBoostPending = true;
                session.StatusMessage =
                    $"💰 Jackpot Boost used! Your next new 5-in-a-row payout will be x{JackpotBoostMultiplier}.";
                break;

            case "perfect-spin":
                if (session.PerfectSpinNextSpin)
                {
                    session.StatusMessage = "🌟 Perfect Spin is already armed.";
                    return;
                }

                SpendBuff(session, buffState);
                session.PerfectSpinNextSpin = true;
                session.StatusMessage = "🌟 Perfect Spin armed! Your next spin will exactly match the solution board.";
                break;

            default:
                session.StatusMessage = $"{buff.Emoji} {buff.Name} is not implemented yet.";
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

        if (session.SlotSymbolIndexes[row, column] < 0 && buff.Id != "wild")
        {
            session.StatusMessage = "That cell does not contain a slot symbol yet.";
            return;
        }

        SpendBuff(session, buffState);
        session.PendingTargetBuffId = null;

        switch (buff.Id)
        {
            case "row-left":
                ShiftRowLeft(session.SlotSymbolIndexes, row);
                FinishBoardManipulation(session, buff, $"row {row + 1} shifted left", checkSolution: true);
                break;

            case "row-right":
                ShiftRowRight(session.SlotSymbolIndexes, row);
                FinishBoardManipulation(session, buff, $"row {row + 1} shifted right", checkSolution: true);
                break;

            case "column-up":
                ShiftColumnUp(session.SlotSymbolIndexes, column);
                FinishBoardManipulation(session, buff, $"column {column + 1} shifted up", checkSolution: true);
                break;

            case "column-down":
                ShiftColumnDown(session.SlotSymbolIndexes, column);
                FinishBoardManipulation(session, buff, $"column {column + 1} shifted down", checkSolution: true);
                break;

            case "reroll":
                var old = session.SlotSymbolIndexes[row, column];
                session.SlotSymbolIndexes[row, column] = Random.Shared.Next(DailySymbols.Length);
                FinishBoardManipulation(session, buff, $"{DailySymbols[old]} rerolled into {DailySymbols[session.SlotSymbolIndexes[row, column]]}", checkSolution: true);
                break;

            case "wild":
                session.SlotSymbolIndexes[row, column] = WildSymbolIndex;
                bool newlySolved = !session.RevealedSolutionCells[row, column];
                session.RevealedSolutionCells[row, column] = true;

                PaylinePayoutResult wildResult = PayNewFiveInARows(session);
                session.Balance += wildResult.Payout;

                string solvedText = newlySolved
                    ? "The selected solution symbol was revealed."
                    : "That solution symbol was already revealed.";

                session.StatusMessage = BuildManipulationStatus(
                    buff,
                    $"symbol at [{row + 1},{column + 1}] became a Wildcard. {solvedText}",
                    wildResult);
                break;

            default:
                // This path should never be reached because only targeted buffs
                // can arrive here, but refunding keeps the state safe if a future
                // definition is accidentally misconfigured.
                session.Balance += buff.Cost;
                buffState.UsesRemaining++;
                session.StatusMessage = $"{buff.Name} is not implemented yet.";
                break;
        }
    }

    private static void FinishBoardManipulation(
        DailySlotsSession session,
        DailyBuffDefinition buff,
        string actionText,
        bool checkSolution)
    {
        int newlyRevealed = checkSolution
            ? RevealNaturalSolutionHits(session)
            : 0;

        PaylinePayoutResult result = PayNewFiveInARows(session);
        session.Balance += result.Payout;
        session.StatusMessage = BuildManipulationStatus(
            buff,
            actionText,
            result,
            newlyRevealed);
    }

    private static string BuildManipulationStatus(
        DailyBuffDefinition buff,
        string actionText,
        PaylinePayoutResult result,
        int newlyRevealed = 0)
    {
        var status = new StringBuilder(180);
        status.Append($"{buff.Emoji} {buff.Name} used — {actionText}.");

        if (newlyRevealed > 0)
        {
            status.Append(
                $" {newlyRevealed} new solution " +
                $"{(newlyRevealed == 1 ? "symbol" : "symbols")} revealed!");
        }

        status.Append(
            $" {result.LineCount} new 5-in-a-row" +
            $"{(result.LineCount == 1 ? string.Empty : "s")}! " +
            $"+${result.Payout:N0}.");

        if (result.UsedJackpotBoost)
            status.Append($" Jackpot Boost x{JackpotBoostMultiplier} applied!");

        return status.ToString();
    }

    private static string BuildTargetPrompt(DailyBuffDefinition buff) => buff.Id switch
    {
        "row-left" => "⬅️ Shift Row Left selected — choose any symbol in the row you want to shift.",
        "row-right" => "➡️ Shift Row Right selected — choose any symbol in the row you want to shift.",
        "column-up" => "⬆️ Shift Column Up selected — choose any symbol in the column you want to shift.",
        "column-down" => "⬇️ Shift Column Down selected — choose any symbol in the column you want to shift.",
        "reroll" => "🎲 Reroll Symbol selected — choose the symbol you want to reroll.",
        "wild" => "🃏 Wildcard selected — choose the symbol to solve and turn into a Wildcard.",
        _ => $"{buff.Emoji} {buff.Name} selected — choose a target symbol."
    };

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
        session.PaidPaylinesThisSpin.Clear();

        bool perfectSpin = session.PerfectSpinNextSpin;
        bool doublePayout = session.DoublePayoutNextSpin;
        int guaranteedHits = session.GuaranteedSolutionHitsNextSpin;

        session.PerfectSpinNextSpin = false;
        session.DoublePayoutNextSpin = false;
        session.GuaranteedSolutionHitsNextSpin = 0;

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

        // Resolve the completely normal random spin first. Lucky Spin is then
        // applied as a true bonus on top, so stacked uses can never replace or
        // overlap solution hits the player would have received naturally.
        int naturalReveals = RevealNaturalSolutionHits(session);
        int luckyReveals = RevealGuaranteedBonusSolutions(session, guaranteedHits);
        int newlyRevealed = naturalReveals + luckyReveals;

        long basePayout = CalculateBasePayout(session.SlotSymbolIndexes);
        PaylinePayoutResult paylineResult = PayNewFiveInARows(session);
        long spinPayout = basePayout + paylineResult.Payout;

        if (doublePayout)
            spinPayout *= 2;

        session.Balance += spinPayout;
        session.SpinsRemaining--;
        session.HasSpunAtLeastOnce = true;

        var status = new StringBuilder(160);
        status.Append($"🎰 Spin complete — +${spinPayout:N0}");

        if (doublePayout)
            status.Append(" (Double Payout)");

        status.Append($" • {newlyRevealed} new solution {(newlyRevealed == 1 ? "cell" : "cells")}");

        if (luckyReveals > 0)
            status.Append($" ({naturalReveals} natural + {luckyReveals} Lucky Spin)");

        if (paylineResult.LineCount > 0)
        {
            status.Append($" • {paylineResult.LineCount} 5-in-a-row");

            if (paylineResult.LineCount > 1)
                status.Append('s');

            if (paylineResult.UsedJackpotBoost)
                status.Append($" (Jackpot Boost x{JackpotBoostMultiplier})");
        }

        if (session.IsSolutionComplete)
            status.Append(" • Solution complete!");
        else if (session.SpinsRemaining <= 0)
            status.Append(" • No spins remaining.");

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

    private static int RevealNaturalSolutionHits(DailySlotsSession session)
    {
        int newlyRevealed = 0;

        for (int row = 0; row < BoardSize; row++)
        {
            for (int column = 0; column < BoardSize; column++)
            {
                if (session.SlotSymbolIndexes[row, column] ==
                    session.SolutionSymbolIndexes[row, column] &&
                    !session.RevealedSolutionCells[row, column])
                {
                    session.RevealedSolutionCells[row, column] = true;
                    newlyRevealed++;
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
            string key = $"R{row}";

            if (session.PaidPaylinesThisSpin.Contains(key) ||
                !TryGetHorizontalPaylineSymbol(session.SlotSymbolIndexes, row, out int symbolIndex))
            {
                continue;
            }

            long linePayout = SymbolPayoutValues[symbolIndex] * FiveInARowMultiplier;

            if (session.JackpotBoostPending)
            {
                linePayout *= JackpotBoostMultiplier;
                session.JackpotBoostPending = false;
                usedJackpotBoost = true;
            }

            session.PaidPaylinesThisSpin.Add(key);
            payout += linePayout;
            lineCount++;
        }

        for (int column = 0; column < BoardSize; column++)
        {
            string key = $"C{column}";

            if (session.PaidPaylinesThisSpin.Contains(key) ||
                !TryGetVerticalPaylineSymbol(session.SlotSymbolIndexes, column, out int symbolIndex))
            {
                continue;
            }

            long linePayout = SymbolPayoutValues[symbolIndex] * FiveInARowMultiplier;

            if (session.JackpotBoostPending)
            {
                linePayout *= JackpotBoostMultiplier;
                session.JackpotBoostPending = false;
                usedJackpotBoost = true;
            }

            session.PaidPaylinesThisSpin.Add(key);
            payout += linePayout;
            lineCount++;
        }

        return new PaylinePayoutResult(payout, lineCount, usedJackpotBoost);
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

    private static void ShiftRowLeft(int[,] board, int row)
    {
        int first = board[row, 0];

        for (int column = 0; column < BoardSize - 1; column++)
            board[row, column] = board[row, column + 1];

        board[row, BoardSize - 1] = first;
    }

    private static void ShiftRowRight(int[,] board, int row)
    {
        int last = board[row, BoardSize - 1];

        for (int column = BoardSize - 1; column > 0; column--)
            board[row, column] = board[row, column - 1];

        board[row, 0] = last;
    }

    private static void ShiftColumnUp(int[,] board, int column)
    {
        int first = board[0, column];

        for (int row = 0; row < BoardSize - 1; row++)
            board[row, column] = board[row + 1, column];

        board[BoardSize - 1, column] = first;
    }

    private static void ShiftColumnDown(int[,] board, int column)
    {
        int last = board[BoardSize - 1, column];

        for (int row = BoardSize - 1; row > 0; row--)
            board[row, column] = board[row - 1, column];

        board[0, column] = last;
    }

    private static MessageComponent BuildBuffSelectionView(DailySlotsSession session)
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

        DailyBuffDefinition[] selectedBuffs = GetSelectedBuffDefinitions(session);

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

    private static MessageComponent BuildGameView(DailySlotsSession session)
    {
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
                "## 🎰 Daily Slots\n" +
                "**Solution:**\n" +
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
                        .WithStyle(ButtonStyle.Secondary)
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

            buffRow.WithButton(
                new ButtonBuilder()
                    .WithLabel($"{buffState.UsesRemaining}/{buffState.Definition.UseCount}")
                    .WithCustomId(
                        $"slotsdaily:use:{session.UserId}:{session.SessionId}:{buffState.Definition.Id}")
                    .WithStyle(hasUses ? ButtonStyle.Primary : ButtonStyle.Secondary)
                    .WithEmote(new Emoji(buffState.Definition.Emoji))
                    .WithDisabled(!hasUses));
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
        builder.Append($"**5-in-a-row:** symbol value ×{FiveInARowMultiplier}");
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
        public HashSet<string> SelectedBuffIds { get; } = [];

        public bool Started { get; private set; }
        public DateOnly SolutionDate { get; private set; }
        public int[,] SolutionSymbolIndexes { get; private set; } = new int[BoardSize, BoardSize];
        public bool[,] RevealedSolutionCells { get; } = new bool[BoardSize, BoardSize];
        public int[,] SlotSymbolIndexes { get; } = CreateEmptySlotBoard();
        public List<DailyBuffState> Buffs { get; } = [];
        public HashSet<string> PaidPaylinesThisSpin { get; } = [];

        public long Balance { get; set; }
        public int SpinsRemaining { get; set; } = MaxSpins;
        public bool HasSpunAtLeastOnce { get; set; }
        public string StatusMessage { get; set; } = "🎰 Spin slots to begin.";
        public string? PendingTargetBuffId { get; set; }
        public int GuaranteedSolutionHitsNextSpin { get; set; }
        public bool DoublePayoutNextSpin { get; set; }
        public bool JackpotBoostPending { get; set; }
        public bool PerfectSpinNextSpin { get; set; }

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
            StatusMessage = "🎰 Spin slots to begin.";
            PendingTargetBuffId = null;
            GuaranteedSolutionHitsNextSpin = 0;
            DoublePayoutNextSpin = false;
            JackpotBoostPending = false;
            PerfectSpinNextSpin = false;
            PaidPaylinesThisSpin.Clear();

            Buffs.Clear();

            foreach (DailyBuffDefinition buff in GetSelectedBuffDefinitions(this))
            {
                Buffs.Add(new DailyBuffState(buff, buff.UseCount));
            }

            Started = true;
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
