using Discord.Rest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System.Text;
using System.Text.Json;

namespace NuttyBot
{
    public static class InteractionHttpServer
    {
        private const string DiscordPublicKey = "b69ac557c909a33e038ba18abc414211933910c7f80c6aa27b9cf32390a9353f";

        public static async Task RunAsync()
        {
            var builder = WebApplication.CreateBuilder();

            builder.WebHost.UseUrls("http://localhost:5000");

            var app = builder.Build();

            var restClient = new DiscordRestClient();

            app.MapPost("/discord", async context =>
            {
                string? signature =
                    context.Request.Headers["X-Signature-Ed25519"];

                string? timestamp =
                    context.Request.Headers["X-Signature-Timestamp"];

                using var reader = new StreamReader(
                    context.Request.Body,
                    Encoding.UTF8);

                string body = await reader.ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(signature) ||
                    string.IsNullOrWhiteSpace(timestamp) ||
                    !restClient.IsValidHttpInteraction(
                        DiscordPublicKey,
                        signature,
                        timestamp,
                        body))
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                Console.WriteLine();
                Console.WriteLine("===== RAW DISCORD INTERACTION =====");
                Console.WriteLine(body);
                Console.WriteLine("===================================");
                Console.WriteLine();

                using var json = JsonDocument.Parse(body);

                int type = json.RootElement
                    .GetProperty("type")
                    .GetInt32();

                // Discord endpoint verification
                if (type == 1)
                {
                    context.Response.ContentType = "application/json";

                    await context.Response.WriteAsync(
                        """{"type":1}""");

                    return;
                }

                // Temporary diagnostic response
                context.Response.ContentType = "application/json";

                await context.Response.WriteAsync(
                    """
                {
                    "type": 4,
                    "data": {
                        "content": "Captured interaction.",
                        "flags": 64
                    }
                }
                """);
            });

            await app.RunAsync();
        }
    }
}
