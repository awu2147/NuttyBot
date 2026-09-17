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
        private static readonly SlotGame Slots = new();
        private static readonly HttpClient Http = new();
        private static readonly ulong[] GuildIds =
        {
            150436633748045824, //reign_of_prophecy
            472949270857777152, //nutty
            901550346785128458  //xo
        };
        private const ulong DestinationGuildId = 472949270857777152; //nutty
        private const ulong OwnerUserId = 150069097554509825; //mocktail
        private const ulong YoinkAnnouncementChannelId = 1550204364583608452; //nutty/dev
        //private const ulong YoinkAnnouncementChannelId = 472949270857777154; //nutty/dev
        private const string YoinkEmote = "<:evil_cat_smirk:1549875953914740846>";

        public static async Task Main()
        {
            // Start the HTTP listener in parallel.
            // _ = InteractionHttpServer.RunAsync();

            var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Set the DISCORD_BOT_TOKEN environment variable before starting the bot.");
            }

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

            _client.MessageCommandExecuted += command => DispatchInteractionAsync(() => OnMessageCommand(command), "message command");
            _client.SelectMenuExecuted += component => DispatchInteractionAsync(() => OnSelectMenu(component), "select menu");
            _client.SlashCommandExecuted += command => DispatchInteractionAsync(() => OnSlashCommand(command), "slash command");
            _client.ButtonExecuted += component => DispatchInteractionAsync(() => Slots.HandleButtonAsync(component), "button");

            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();

            await Task.Delay(Timeout.Infinite);
        }

        private static Task DispatchInteractionAsync(Func<Task> handler, string interactionType)
        {
            _ = RunInteractionSafelyAsync(handler, interactionType);

            // Release Discord.Net's gateway task immediately.
            return Task.CompletedTask;
        }

        private static async Task RunInteractionSafelyAsync(Func<Task> handler, string interactionType)
        {
            try
            {
                await handler();
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Unhandled {interactionType} error: {ex}");
            }
        }

        private static async Task OnReady()
        {
            await EnsureGlobalCommandsRegistered();
            Slots.Start();
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

            if (existingCommands.Any(x => x.Name == "Steal Reaction"))
                return;

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

                var slotCommand = SlotGame.CreateCommand();

                if (!existingCommands.Any(x => x.Name == "addemoji"))
                {
                    await guild.CreateApplicationCommandAsync(addEmojiCommand.Build());
                }

                if (!existingCommands.Any(x => x.Name == "slots"))
                {
                    await guild.CreateApplicationCommandAsync(slotCommand.Build());
                }

                var oldSlotCommand = existingCommands.FirstOrDefault(x => x.Name == "rollslots");

                if (oldSlotCommand != null)
                {
                    await oldSlotCommand.DeleteAsync();

                    Console.WriteLine(
                        $"Removed /rollslots from {guild.Name}.");
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

                case "slots":
                    await Slots.HandleSlashCommandAsync(command);
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
