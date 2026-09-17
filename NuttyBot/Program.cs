using Discord;
using Discord.WebSocket;
using NuttyBot;
using System.Collections;
using System.Reflection;

namespace NuttyBot
{
    class Program
    {
        private static DiscordSocketClient _client = null!;
        private static readonly HttpClient Http = new();
        private static readonly ulong[] GuildIds =
        {
            150436633748045824, //reign_of_prophecy
            472949270857777152, //nutty
            901550346785128458  //xo
        };
        private const ulong DestinationGuildId = 472949270857777152; //nutty
        private const ulong OwnerUserId = 150069097554509825; //mocktail
        private const ulong YoinkAnnouncementChannelId = 472949270857777154; //nutty/general
        private const string YoinkEmote = "<:evil_cat_smirk:1549875953914740846>";

        private static readonly string[] SlotSymbols =
        {
            "🍒",
            "🍋",
            "🍊",
            "🍇",
            "⭐"
        };

        public static async Task Main()
        {
            // Start the HTTP listener in parallel.
            // _ = InteractionHttpServer.RunAsync();

            var token = "MTU0OTgzMTk2NTA3NzkzMDA1NQ.GymayH.cn37oqN4L0qyfzNYnKYgzFs7Sp20b5WRknnnNk";

            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds |
                                 GatewayIntents.GuildMessageReactions |
                                 GatewayIntents.DirectMessageReactions
            });

            _client.Log += message =>
            {
                Console.WriteLine(message);
                return Task.CompletedTask;
            };

            _client.Ready += OnReady;
            _client.JoinedGuild += OnJoinedGuild;
            _client.SlashCommandExecuted += OnSlashCommand;
            _client.MessageCommandExecuted += OnMessageCommand;
            _client.SelectMenuExecuted += OnSelectMenu;
            _client.ButtonExecuted += OnButtonExecuted;

            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();

            await Task.Delay(Timeout.Infinite);
        }

        private static async Task OnReady()
        {
            await EnsureGlobalCommandsRegistered();
            StartSlotCleanupTimer();
            // Handles guilds the bot was already installed in
            // before the program started.
            foreach (var guild in _client.Guilds)
            {
                await EnsureCommandsRegistered(guild);
            }
        }

        private static async Task OnJoinedGuild(SocketGuild guild)
        {
            // Handles the bot being installed into a new guild
            // while the program is running.
            await EnsureCommandsRegistered(guild);
        }

        private static async Task EnsureGlobalCommandsRegistered()
        {
            var existingCommands =
                await _client.GetGlobalApplicationCommandsAsync();

            //if (existingCommands.Any(x => x.Name == "Steal Reaction"))
            //    return;

            var command = new MessageCommandBuilder()
                .WithName("Steal Reaction")
                .WithIntegrationTypes(
                    ApplicationIntegrationType.UserInstall)
                .WithContextTypes(
                    InteractionContextType.Guild,
                    InteractionContextType.BotDm,
                    InteractionContextType.PrivateChannel);

            await _client.CreateGlobalApplicationCommandAsync(
                command.Build());

            Console.WriteLine(
                "Registered user-install-only Steal Reaction.");
        }

        private static async Task EnsureCommandsRegistered(SocketGuild guild)
        {
            try
            {
                var existingCommands = await guild.GetApplicationCommandsAsync();

                var addEmojiCommand = new SlashCommandBuilder()
                    .WithName("addemoji")
                    .WithDescription("Add an emoji to this server from a URL")
                    .AddOption(
                        "name",
                        ApplicationCommandOptionType.String,
                        "Name for the emoji",
                        isRequired: true)
                    .AddOption(
                        "url",
                        ApplicationCommandOptionType.String,
                        "Image URL",
                        isRequired: true);

                var slotCommand = new SlashCommandBuilder()
                    .WithName("rollslots")
                    .WithDescription("Roll the slot machine!");

                if (!existingCommands.Any(x => x.Name == "addemoji"))
                {
                    await guild.CreateApplicationCommandAsync(addEmojiCommand.Build());
                }

                if (!existingCommands.Any(x => x.Name == "rollslots"))
                {
                    await guild.CreateApplicationCommandAsync(slotCommand.Build());
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Failed to register commands in {guild.Name}: {ex.Message}");
            }
        }

        private static async Task OnSlashCommand(SocketSlashCommand command)
        {
            switch (command.CommandName)
            {
                case "addemoji":
                    await HandleAddEmoji(command);
                    break;

                case "rollslots":
                    await HandleRollSlots(command);
                    break;
            }
        }

        private static async Task HandleAddEmoji(SocketSlashCommand command)
        {
            if (command.CommandName != "addemoji")
                return;

            await command.DeferAsync(ephemeral: true);

            try
            {
                var guild = (command.Channel as SocketGuildChannel)?.Guild;

                if (guild == null)
                {
                    await command.FollowupAsync(
                        "This command must be used in a server.",
                        ephemeral: true);
                    return;
                }

                string name = command.Data.Options
                    .First(x => x.Name == "name")
                    .Value.ToString()!;

                string urlText = command.Data.Options
                    .First(x => x.Name == "url")
                    .Value.ToString()!;

                if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) ||
                    url.Scheme != Uri.UriSchemeHttps)
                {
                    await command.FollowupAsync(
                        "Invalid HTTPS URL.",
                        ephemeral: true);
                    return;
                }

                byte[] data = await Http.GetByteArrayAsync(url);

                // Discord guild emoji upload limit.
                if (data.Length > 256 * 1024)
                {
                    await command.FollowupAsync(
                        $"Image is too large ({data.Length / 1024} KiB). Discord allows 256 KiB.",
                        ephemeral: true);
                    return;
                }

                using var stream = new MemoryStream(data);
                using var image = new Image(stream);

                var emote = await guild.CreateEmoteAsync(name, image);

                await command.FollowupAsync(
                    $"Added {emote} as `{name}`.",
                    ephemeral: true);
            }
            catch (Exception ex)
            {
                await command.FollowupAsync(
                    $"Failed: `{ex.Message}`",
                    ephemeral: true);
            }
        }

        private const int SlotMachineCount = 5;
        private static readonly TimeSpan SlotSessionTimeout = TimeSpan.FromSeconds(30);
        private static readonly object SlotLock = new();
        private static readonly Dictionary<ulong, List<SlotMachine>> SlotMachinesByGuild = new();
        private static System.Threading.Timer? _slotCleanupTimer;
        private static readonly Dictionary<(ulong GuildId, ulong UserId), string> ActiveSlotLobbies = new();

        private class SlotMachine
        {
            public int Number { get; init; }

            // Current session
            public ulong? OwnerUserId { get; set; }
            public string? SessionId { get; set; }
            public DateTimeOffset LastInteractionUtc { get; set; }

            // Lifetime stats for this bot session
            public int TotalRolls { get; set; }
            public int TotalWins { get; set; }

            public bool IsClaimed => OwnerUserId.HasValue;
        }

        private static string CreateSlotLobby(ulong guildId, ulong userId)
        {
            string lobbyId = Guid.NewGuid().ToString("N");

            ActiveSlotLobbies[(guildId, userId)] = lobbyId;

            return lobbyId;
        }

        private static bool IsCurrentLobby(ulong guildId, ulong userId, string lobbyId)
        {
            return ActiveSlotLobbies.TryGetValue(
                       (guildId, userId),
                       out string? currentLobbyId)
                   && currentLobbyId == lobbyId;
        }

        private static void CloseSlotLobby(ulong guildId, ulong userId)
        {
            ActiveSlotLobbies.Remove((guildId, userId));
        }

        private static void ReleaseUserSlotMachine(ulong guildId, ulong userId)
        {
            var machines = GetSlotMachines(guildId);

            var machine = machines.FirstOrDefault(
                x => x.OwnerUserId == userId);

            if (machine != null)
                ReleaseSlotMachine(machine);
        }

        private static string BuildSlotLobbyContent(ulong guildId)
        {
            return
                "🎰 **Nutty Slots Lobby** 🎰\n\n" +
                "Choose a slot machine:";
        }

        private static MessageComponent BuildSlotLobbyButtons(ulong guildId, ulong userId, string lobbyId)
        {
            var machines = GetSlotMachines(guildId);

            var builder = new ComponentBuilder();

            foreach (var machine in machines)
            {
                string statusEmoji = machine.IsClaimed
                    ? "🔒"
                    : "🟢";

                var button = new ButtonBuilder()
                    .WithLabel($"🎰 #{machine.Number} {statusEmoji}")
                    .WithCustomId(
                        $"slots:claim:{machine.Number}:{userId}:{lobbyId}")
                    .WithStyle(
                        machine.IsClaimed
                            ? ButtonStyle.Secondary
                            : ButtonStyle.Primary)
                    .WithDisabled(machine.IsClaimed);

                builder.WithButton(button);
            }

            builder.WithButton(
                "Refresh Lobby",
                $"slots:refresh:{userId}:{lobbyId}",
                ButtonStyle.Secondary,
                emote: new Emoji("🔄"));

            return builder.Build();
        }

        private static List<SlotMachine> GetSlotMachines(ulong guildId)
        {
            if (!SlotMachinesByGuild.TryGetValue(guildId, out var machines))
            {
                machines = Enumerable
                    .Range(1, SlotMachineCount)
                    .Select(number => new SlotMachine
                    {
                        Number = number
                    })
                    .ToList();

                SlotMachinesByGuild[guildId] = machines;
            }

            return machines;
        }

        private static bool TryClaimSlotMachine(
            ulong guildId,
            ulong userId,
            int machineNumber,
            out SlotMachine? machine)
        {
            machine = GetSlotMachines(guildId)
                .FirstOrDefault(x => x.Number == machineNumber);

            if (machine == null)
                return false;

            if (machine.IsClaimed)
                return false;

            machine.OwnerUserId = userId;
            machine.SessionId = Guid.NewGuid().ToString("N");
            machine.LastInteractionUtc = DateTimeOffset.UtcNow;

            return true;
        }

        private static string RollSlotMachine(SlotMachine machine)
        {
            string[,] slots = new string[3, 3];

            for (int row = 0; row < 3; row++)
            {
                for (int column = 0; column < 3; column++)
                {
                    slots[row, column] =
                        SlotSymbols[Random.Shared.Next(SlotSymbols.Length)];
                }
            }

            bool winner =
                slots[1, 0] == slots[1, 1] &&
                slots[1, 1] == slots[1, 2];

            machine.TotalRolls++;

            if (winner)
                machine.TotalWins++;

            machine.LastInteractionUtc = DateTimeOffset.UtcNow;

            string machineDisplay =
                $"┌───────────┐\n" +
                $"│ {slots[0, 0]}  {slots[0, 1]}  {slots[0, 2]} │\n" +
                $"│ {slots[1, 0]}  {slots[1, 1]}  {slots[1, 2]} │ ⬅️\n" +
                $"│ {slots[2, 0]}  {slots[2, 1]}  {slots[2, 2]} │\n" +
                $"└───────────┘";

            string result = winner
                ? "🎉 **JACKPOT!** 🎉"
                : "Better luck next time!";

            return
                $"🎰 **Slot Machine #{machine.Number}** 🎰\n\n" +
                $"{machineDisplay}\n\n" +
                $"{result}\n\n" +
                $"**Machine Stats:** {machine.TotalRolls} rolls • {machine.TotalWins} wins";
        }

        private static MessageComponent BuildSlotButtons(SlotMachine machine)
        {
            return new ComponentBuilder()
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
        }

        private static async Task HandleRollSlots(SocketSlashCommand command)
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

            string lobbyId;
            string content;
            MessageComponent components;

            lock (SlotLock)
            {
                ExpireSlotSessions();

                // Safety feature:
                // running /rollslots abandons your current machine.
                ReleaseUserSlotMachine(guildId, userId);

                // Also invalidates any previous lobby.
                lobbyId = CreateSlotLobby(guildId, userId);

                content = BuildSlotLobbyContent(guildId);

                components = BuildSlotLobbyButtons(
                    guildId,
                    userId,
                    lobbyId);
            }

            await command.RespondAsync(
                content,
                components: components);
        }

        private static async Task OnButtonExecuted(SocketMessageComponent component)
        {
            if (!component.Data.CustomId.StartsWith("slots:"))
                return;

            if (!component.GuildId.HasValue)
                return;

            string[] parts =
                component.Data.CustomId.Split(':');

            if (parts.Length < 2)
                return;

            switch (parts[1])
            {
                case "claim":
                    await HandleSlotClaim(component, parts);
                    break;

                case "roll":
                    await HandleSlotRoll(component, parts);
                    break;

                case "leave":
                    await HandleSlotLeave(component, parts);
                    break;

                case "lobby":
                    await HandleReturnToLobby(component, parts);
                    break;

                case "refresh":
                    await HandleSlotLobbyRefresh(component, parts);
                    break;
            }
        }

        private static async Task HandleSlotClaim(
    SocketMessageComponent component,
    string[] parts)
        {
            if (parts.Length != 5)
                return;

            ulong guildId = component.GuildId!.Value;

            if (!int.TryParse(parts[2], out int machineNumber))
                return;

            if (!ulong.TryParse(parts[3], out ulong lobbyUserId))
                return;

            string lobbyId = parts[4];

            // Prevent another user from using somebody else's lobby.
            if (component.User.Id != lobbyUserId)
            {
                await component.RespondAsync(
                    "This isn't your slot lobby!",
                    ephemeral: true);

                return;
            }

            string? content = null;
            MessageComponent? components = null;

            bool oldLobby = false;
            bool machineTaken = false;

            lock (SlotLock)
            {
                ExpireSlotSessions();

                if (!IsCurrentLobby(
                        guildId,
                        component.User.Id,
                        lobbyId))
                {
                    oldLobby = true;
                }
                else if (!TryClaimSlotMachine(
                             guildId,
                             component.User.Id,
                             machineNumber,
                             out SlotMachine? machine))
                {
                    machineTaken = true;

                    // Refresh the lobby because its state
                    // may have changed since it was displayed.
                    content = BuildSlotLobbyContent(guildId);

                    components = BuildSlotLobbyButtons(
                        guildId,
                        component.User.Id,
                        lobbyId);
                }
                else
                {
                    CloseSlotLobby(
                        guildId,
                        component.User.Id);

                    // Immediately perform the first roll.
                    content = RollSlotMachine(machine!);

                    components = BuildSlotButtons(machine!);
                }
            }

            if (oldLobby)
            {
                await component.RespondAsync(
                    "That slot lobby is no longer active. Use `/rollslots` again.",
                    ephemeral: true);

                return;
            }

            if (machineTaken)
            {
                await component.UpdateAsync(message =>
                {
                    message.Content = content;
                    message.Components = components;
                });

                return;
            }

            await component.UpdateAsync(message =>
            {
                message.Content = content;
                message.Components = components;
            });
        }

        private static async Task HandleSlotRoll(
    SocketMessageComponent component,
    string[] parts)
        {
            if (parts.Length != 4)
                return;

            if (!int.TryParse(parts[2], out int machineNumber))
                return;

            string sessionId = parts[3];

            ulong guildId = component.GuildId!.Value;

            string? content = null;
            MessageComponent? components = null;

            bool invalidSession = false;

            lock (SlotLock)
            {
                ExpireSlotSessions();

                var machine = GetSlotMachines(guildId)
                    .FirstOrDefault(
                        x => x.Number == machineNumber);

                if (machine == null ||
                    machine.OwnerUserId != component.User.Id ||
                    machine.SessionId != sessionId)
                {
                    invalidSession = true;
                }
                else
                {
                    content = RollSlotMachine(machine);
                    components = BuildSlotButtons(machine);
                }
            }

            if (invalidSession)
            {
                await component.RespondAsync(
                    "That slot machine session has expired.",
                    ephemeral: true);

                return;
            }

            await component.UpdateAsync(message =>
            {
                message.Content = content;
                message.Components = components;
            });
        }

        private static async Task HandleSlotLeave(
    SocketMessageComponent component,
    string[] parts)
        {
            if (parts.Length != 4)
                return;

            if (!int.TryParse(parts[2], out int machineNumber))
                return;

            string sessionId = parts[3];

            ulong guildId = component.GuildId!.Value;

            string? content = null;
            bool invalidSession = false;

            lock (SlotLock)
            {
                ExpireSlotSessions();

                var machine = GetSlotMachines(guildId)
                    .FirstOrDefault(
                        x => x.Number == machineNumber);

                if (machine == null ||
                    machine.OwnerUserId != component.User.Id ||
                    machine.SessionId != sessionId)
                {
                    invalidSession = true;
                }
                else
                {
                    content =
                        $"🎰 **Slot Machine #{machine.Number}** is now free.\n\n" +
                        $"**Machine Stats:** " +
                        $"{machine.TotalRolls} rolls • " +
                        $"{machine.TotalWins} wins";

                    ReleaseSlotMachine(machine);
                }
            }

            if (invalidSession)
            {
                await component.RespondAsync(
                    "That slot machine session has expired.",
                    ephemeral: true);

                return;
            }

            await component.UpdateAsync(message =>
            {
                message.Content = content;
                message.Components =
                    new ComponentBuilder().Build();
            });
        }

        private static async Task HandleReturnToLobby(
    SocketMessageComponent component,
    string[] parts)
        {
            if (parts.Length != 4)
                return;

            if (!int.TryParse(parts[2], out int machineNumber))
                return;

            string sessionId = parts[3];

            ulong guildId = component.GuildId!.Value;
            ulong userId = component.User.Id;

            string? content = null;
            MessageComponent? components = null;

            bool invalidSession = false;

            lock (SlotLock)
            {
                ExpireSlotSessions();

                var machine = GetSlotMachines(guildId)
                    .FirstOrDefault(
                        x => x.Number == machineNumber);

                if (machine == null ||
                    machine.OwnerUserId != userId ||
                    machine.SessionId != sessionId)
                {
                    invalidSession = true;
                }
                else
                {
                    // Release their current machine.
                    ReleaseSlotMachine(machine);

                    // Create a completely new lobby session.
                    string lobbyId =
                        CreateSlotLobby(guildId, userId);

                    content =
                        BuildSlotLobbyContent(guildId);

                    components =
                        BuildSlotLobbyButtons(
                            guildId,
                            userId,
                            lobbyId);
                }
            }

            if (invalidSession)
            {
                await component.RespondAsync(
                    "That slot machine session has expired.",
                    ephemeral: true);

                return;
            }

            await component.UpdateAsync(message =>
            {
                message.Content = content;
                message.Components = components;
            });
        }

        private static async Task HandleSlotLobbyRefresh(
    SocketMessageComponent component,
    string[] parts)
        {
            if (parts.Length != 4)
                return;

            ulong guildId = component.GuildId!.Value;

            if (!ulong.TryParse(parts[2], out ulong lobbyUserId))
                return;

            string lobbyId = parts[3];

            // Don't let somebody else interact with your lobby.
            if (component.User.Id != lobbyUserId)
            {
                await component.RespondAsync(
                    "This isn't your slot lobby!",
                    ephemeral: true);

                return;
            }

            string? content = null;
            MessageComponent? components = null;
            bool invalidLobby = false;

            lock (SlotLock)
            {
                // This is important because machines that have been
                // idle for 30+ seconds should be freed before refreshing.
                ExpireSlotSessions();

                if (!IsCurrentLobby(
                        guildId,
                        component.User.Id,
                        lobbyId))
                {
                    invalidLobby = true;
                }
                else
                {
                    content = BuildSlotLobbyContent(guildId);

                    components = BuildSlotLobbyButtons(
                        guildId,
                        component.User.Id,
                        lobbyId);
                }
            }

            if (invalidLobby)
            {
                await component.RespondAsync(
                    "That slot lobby is no longer active. Use `/rollslots` again.",
                    ephemeral: true);

                return;
            }

            await component.UpdateAsync(message =>
            {
                message.Content = content;
                message.Components = components;
            });
        }

        private static void ReleaseSlotMachine(SlotMachine machine)
        {
            machine.OwnerUserId = null;
            machine.SessionId = null;
            machine.LastInteractionUtc = default;
        }

        private static void ExpireSlotSessions()
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var machines in SlotMachinesByGuild.Values)
            {
                foreach (var machine in machines)
                {
                    if (!machine.IsClaimed)
                        continue;

                    if (now - machine.LastInteractionUtc >= SlotSessionTimeout)
                    {
                        ReleaseSlotMachine(machine);
                    }
                }
            }
        }

        private static void StartSlotCleanupTimer()
        {
            _slotCleanupTimer ??= new System.Threading.Timer(
                _ =>
                {
                    lock (SlotLock)
                    {
                        ExpireSlotSessions();
                    }
                },
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));
        }

        private static async Task OnMessageCommand(
        SocketMessageCommand command)
        {
            if (command.CommandName != "Steal Reaction")
                return;

            await command.DeferAsync(ephemeral: true);

            try
            {
                if (command.User.Id != OwnerUserId)
                {
                    await command.FollowupAsync(
                        "This command is private.",
                        ephemeral: true);

                    return;
                }

                var reactions = GetCustomReactions(command);

                Console.WriteLine(
                    $"Found {reactions.Count} custom reaction(s)");

                foreach (var reaction in reactions)
                {
                    Console.WriteLine(
                        $"{reaction.Name} | " +
                        $"{reaction.Id} | " +
                        $"Animated={reaction.Animated} | " +
                        $"Count={reaction.Count}");
                }

                if (reactions.Count == 0)
                {
                    await command.FollowupAsync(
                        "This message has no custom emoji reactions.",
                        ephemeral: true);

                    return;
                }

                // If there's only one, steal it immediately.
                if (reactions.Count == 1)
                {
                    var reaction = reactions[0];

                    var emote = new Emote(
                        reaction.Id,
                        reaction.Name,
                        reaction.Animated);

                    var added =
                        await CopyEmoteToDestination(emote);

                    await command.FollowupAsync(
                        $"Added {added} as `:{added.Name}:`.",
                        ephemeral: true);

                    return;
                }

                // Multiple custom reactions: give yourself a private selector.
                var menu = new SelectMenuBuilder()
                    .WithCustomId("steal-reaction-select")
                    .WithPlaceholder("Choose a reaction to steal")
                    .WithMinValues(1)
                    .WithMaxValues(1);

                foreach (var reaction in reactions.Take(25))
                {
                    string value =
                        $"{reaction.Id}|" +
                        $"{(reaction.Animated ? 1 : 0)}|" +
                        $"{reaction.Name}";

                    menu.AddOption(
                        reaction.Name,
                        value,
                        $"{reaction.Count} reaction(s)");
                }

                var components = new ComponentBuilder()
                    .WithSelectMenu(menu)
                    .Build();

                await command.FollowupAsync(
                    "Which reaction do you want to add?",
                    components: components,
                    ephemeral: true);
            }
            catch (Exception ex)
            {
                await command.FollowupAsync(
                    $"Failed: `{ex.Message}`",
                    ephemeral: true);
            }
        }

        private static async Task OnSelectMenu(
        SocketMessageComponent component)
        {
            if (component.Data.CustomId != "steal-reaction-select")
                return;

            if (component.User.Id != OwnerUserId)
            {
                await component.RespondAsync(
                    "This command is private.",
                    ephemeral: true);

                return;
            }

            await component.DeferAsync(ephemeral: true);

            try
            {
                string value = component.Data.Values.First();

                string[] parts = value.Split('|');

                ulong id = ulong.Parse(parts[0]);
                bool animated = parts[1] == "1";
                string name = parts[2];

                var emote = new Emote(
                    id,
                    name,
                    animated);

                var added =
                    await CopyEmoteToDestination(emote);

                await component.FollowupAsync(
                    $"Added {added} as `:{added.Name}:`.",
                    ephemeral: true);
            }
            catch (Exception ex)
            {
                await component.FollowupAsync(
                    $"Failed: `{ex.Message}`",
                    ephemeral: true);
            }
        }

        private static async Task<GuildEmote> CopyEmoteToDestination(
        Emote source)
        {
            var guild = _client.GetGuild(DestinationGuildId);

            if (guild == null)
                throw new Exception(
                    "Destination server is unavailable.");

            byte[] data =
                await Http.GetByteArrayAsync(source.Url);

            if (data.Length > 256 * 1024)
                throw new Exception(
                    $"Emoji is too large ({data.Length / 1024} KiB).");

            string name = source.Name;

            using var stream = new MemoryStream(data);
            using var image = new Discord.Image(stream);

            var added = await guild.CreateEmoteAsync(
                name,
                image);

            await PostYoinkAnnouncement(guild, added);

            return added;
        }

        private static async Task PostYoinkAnnouncement(SocketGuild guild, GuildEmote emote)
        {
            var channel =
                guild.GetTextChannel(YoinkAnnouncementChannelId);

            if (channel == null)
            {
                Console.WriteLine(
                    $"Could not find yoink announcement channel " +
                    $"{YoinkAnnouncementChannelId}");

                return;
            }

            await channel.SendMessageAsync(
                $"{emote} `{emote.Name}` has been YOINKED and added to the server {YoinkEmote}.");
        }

        private record ReactionInfo(ulong Id, string Name, bool Animated, int Count);

        private static List<ReactionInfo> GetCustomReactions(
        SocketMessageCommand command)
        {
            var result = new List<ReactionInfo>();

            // This is Discord.Net's underlying API interaction-data object.
            object rawData = ((SocketInteraction)command).Data;

            // rawData.Resolved is Optional<ApplicationCommandInteractionDataResolved>
            object? resolved = GetOptionalProperty(rawData, "Resolved");

            if (resolved == null)
                return result;

            // resolved.Messages is Optional<Dictionary<string, Message>>
            object? messagesObject = GetOptionalProperty(resolved, "Messages");

            if (messagesObject is not IDictionary messages)
                return result;

            string targetMessageId = command.Data.Message.Id.ToString();

            if (!messages.Contains(targetMessageId))
                return result;

            object? rawMessage = messages[targetMessageId];

            if (rawMessage == null)
                return result;

            // message.Reactions is Optional<Reaction[]>
            object? reactionsObject =
                GetOptionalProperty(rawMessage, "Reactions");

            if (reactionsObject is not IEnumerable reactions)
                return result;

            foreach (object reaction in reactions)
            {
                object? emoji = GetProperty(reaction, "Emoji");

                if (emoji == null)
                    continue;

                // Custom emoji IDs are non-null.
                // Unicode reactions have null ID, so ignore those.
                object? idObject = GetProperty(emoji, "Id");

                if (idObject is not ulong id)
                    continue;

                // Preserve the original Discord emoji name.
                // If Discord no longer supplies a name, generate one from the ID.
                string name = GetProperty(emoji, "Name") as string ?? $"emoji_{id}";

                object? animatedObject = GetOptionalProperty(emoji, "Animated");

                bool animated = animatedObject is bool value && value;

                int count = GetProperty(reaction, "Count") is int reactionCount ? reactionCount : 0;

                result.Add(new ReactionInfo(
                    id,
                    name,
                    animated,
                    count));
            }

            return result;
        }

        private static object? GetProperty(
        object? obj,
        string propertyName)
        {
            if (obj == null)
                return null;

            var property = obj.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic);

            return property?.GetValue(obj);
        }

        private static object? GetOptionalProperty(
            object? obj,
            string propertyName)
        {
            object? value = GetProperty(obj, propertyName);

            if (value == null)
                return null;

            Type type = value.GetType();

            // Is this Discord.Optional<T>?
            if (type.IsGenericType &&
                type.GetGenericTypeDefinition().FullName
                    == "Discord.Optional`1")
            {
                var isSpecifiedProperty =
                    type.GetProperty("IsSpecified");

                bool isSpecified =
                    isSpecifiedProperty != null &&
                    (bool)isSpecifiedProperty.GetValue(value)!;

                if (!isSpecified)
                    return null;

                var valueProperty =
                    type.GetProperty("Value");

                return valueProperty?.GetValue(value);
            }

            return value;
        }
    }
}