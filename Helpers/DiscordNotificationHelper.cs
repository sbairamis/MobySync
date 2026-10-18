using MobySync.Interfaces;

namespace MobySync.Helpers;

public class DiscordNotificationHelper : INotificationHelper
{
    private readonly int _successColor = 3066993;
    private readonly int _infoColor = 3447003;
    private readonly int _errorColor = 15158332;

    // Discord embed limits: https://discord.com/developers/docs/resources/message#embed-object-embed-limits
    private const int MaxFieldValueLength = 1024;
    private const int MaxDescriptionLength = 4096;
    private const int MaxFields = 25;
    private const int MaxEmbedLength = 6000;
    public bool IsConfigured { get; }
    public string ProviderName { get; }
    public string WebhookUrl { get; } = string.Empty;
    
    private readonly ILogger<DiscordNotificationHelper> _logger;
    
    private IHttpClientFactory _httpClientFactory;

    public DiscordNotificationHelper(ILogger<DiscordNotificationHelper> logger, IHttpClientFactory httpClientFactory)
    {
        var providedUrl = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL") ?? string.Empty;
        
        _logger =  logger;

        ProviderName = "Discord";
        
        _httpClientFactory = httpClientFactory;

        if (!string.IsNullOrWhiteSpace(providedUrl))
        {
            IsConfigured = true;
            
            WebhookUrl =  providedUrl;
        }
        else
        {
            _logger.LogWarning("Discord webhook url was not provided");
        }
    }

    public async Task SendSummary(Models.UpdateSummary summary)
    {
        object payload;

        if (!summary.HasChanges)
        {
            var description = Truncate(summary.UpToDate.Any()
                ? $"All containers are up to date.\n{string.Join(", ", summary.UpToDate.Select(n => $"`{n}`"))}"
                : "No containers were found to monitor.", MaxDescriptionLength);

            var noChangeFields = new List<(string Name, string Value)>();

            AddFields(noChangeFields, "⏭️ Skipped", summary.Skipped.Select(s => $"• **{s.ContainerName}** (`{s.ImageName}`): {s.ErrorMessage}"));

            var footerText = $"Total duration: {summary.TotalDuration:mm\\:ss}";
            const string noChangeTitle = "Update Cycle Complete";

            payload = new
            {
                username = "MobySync",
                embeds = new[]
                {
                    new
                    {
                        title = noChangeTitle,
                        description,
                        color = _infoColor,
                        fields = ToEmbedFields(noChangeFields, noChangeTitle.Length + description.Length + footerText.Length),
                        footer = new { text = footerText },
                        timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                    }
                }
            };
        }
        else
        {
            var fields = new List<(string Name, string Value)>();

            AddFields(fields, "✅ Successful Updates", summary.Successes.Select(s => $"• **{s.ContainerName}**: `{s.NewTag}`"));
            AddFields(fields, "⚠️ Rollbacks", summary.Rollbacks.Select(r => $"• **{r.ContainerName}**: {r.ErrorMessage}"));
            AddFields(fields, "❌ Failed Pulls", summary.FailedPulls.Select(f => $"• **{f.ContainerName}**: {f.ErrorMessage}"));
            AddFields(fields, "⏭️ Skipped", summary.Skipped.Select(s => $"• **{s.ContainerName}** (`{s.ImageName}`): {s.ErrorMessage}"));

            var footerText = $"Total duration: {summary.TotalDuration:mm\\:ss}";
            const string title = "Update Cycle Summary";

            payload = new
            {
                username = "MobySync",
                embeds = new[]
                {
                    new
                    {
                        title,
                        color = summary.Rollbacks.Any() || summary.FailedPulls.Any() ? _errorColor : _successColor,
                        fields = ToEmbedFields(fields, title.Length + footerText.Length),
                        footer = new { text = footerText },
                        timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                    }
                }
            };
        }

        await PostToDiscordAsync(payload);
    }

    public async Task SendUpdateStarted()
    {
        var payload = new
        {
            username = "MobySync",
            embeds = new[]
            {
                new
                {
                    title = "Update Cycle Starting",
                    description = "Checking all containers for new images...",
                    color = _infoColor,
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                }
            }
        };

        await PostToDiscordAsync(payload);
    }

    public async Task SendStartupTest()
    {
        var payload = new
        {
            username = "MobySync",
            embeds = new[]
            {
                new
                {
                    title = "MobySync Started",
                    description = "Webhook is configured and working.",
                    color = _successColor,
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                }
            }
        };

        await PostToDiscordAsync(payload);
    }

    /// <summary>
    /// Splits the lines into as many fields as needed so no field value exceeds Discord's limit.
    /// </summary>
    private static void AddFields(List<(string Name, string Value)> fields, string name, IEnumerable<string> lines)
    {
        var current = new System.Text.StringBuilder();
        var fieldName = name;

        foreach (var rawLine in lines)
        {
            var line = Truncate(rawLine, MaxFieldValueLength);

            if (current.Length > 0 && current.Length + 1 + line.Length > MaxFieldValueLength)
            {
                fields.Add((fieldName, current.ToString()));
                current.Clear();
                fieldName = $"{name} (cont.)";
            }

            if (current.Length > 0)
                current.Append('\n');

            current.Append(line);
        }

        if (current.Length > 0)
            fields.Add((fieldName, current.ToString()));
    }

    /// <summary>
    /// Keeps fields within Discord's field-count and total embed length limits.
    /// </summary>
    private static object[] ToEmbedFields(List<(string Name, string Value)> fields, int usedLength)
    {
        const string omittedName = "…";
        const int omittedReserve = 64;

        var result = new List<object>();
        var budget = MaxEmbedLength - usedLength - omittedReserve;

        for (var i = 0; i < fields.Count; i++)
        {
            var (name, value) = fields[i];
            var length = name.Length + value.Length;

            if ((result.Count >= MaxFields - 1 && i < fields.Count - 1) || length > budget)
            {
                result.Add(new { name = omittedName, value = $"{fields.Count - i} more section(s) omitted (Discord size limit). Check the logs.", inline = false });
                break;
            }

            budget -= length;
            result.Add(new { name, value, inline = false });
        }

        return result.ToArray();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    private async Task PostToDiscordAsync(object payload)
    {
        if (!IsConfigured) 
            return;

        var httpClient = _httpClientFactory.CreateClient();
        
        try
        {
            var response = await httpClient.PostAsJsonAsync(WebhookUrl, payload);

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to send Discord notification. Status Code: {StatusCode}. Response: {Body}", 
                    response.StatusCode, responseBody);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A network error occurred while attempting to reach Discord.");
        }
    }
}