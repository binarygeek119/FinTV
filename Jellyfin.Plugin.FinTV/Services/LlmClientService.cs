using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.FinTV.Configuration;
using Jellyfin.Plugin.FinTV.Domain;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

public class LlmClientService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LlmClientService> _logger;

    public LlmClientService(IHttpClientFactory httpClientFactory, ILogger<LlmClientService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> CompleteJsonAsync(
        AiProvider provider,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        var settings = Plugin.Instance?.Configuration.Ai ?? new AiSettings();
        var (apiKey, model, baseUrl) = ResolveProvider(provider, settings);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"{provider} API key is not configured.");
        }

        var payload = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            response_format = new { type = "json_object" },
            temperature = 0.4
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var client = _httpClientFactory.CreateClient(nameof(LlmClientService));
        client.Timeout = TimeSpan.FromSeconds(120);

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("LLM request failed ({Status}): {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"LLM request failed ({(int)response.StatusCode}).");
        }

        var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);
        var content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("LLM returned an empty response.");
        }

        return content;
    }

    public async Task TestConnectionAsync(AiProvider provider, CancellationToken cancellationToken = default)
    {
        var content = await CompleteJsonAsync(
            provider,
            "You are a connectivity test. Reply with JSON {\"ok\":true}.",
            "Reply now.",
            cancellationToken);

        if (!content.Contains("ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unexpected test response from LLM provider.");
        }
    }

    private static (string ApiKey, string Model, string BaseUrl) ResolveProvider(AiProvider provider, AiSettings settings)
    {
        return provider switch
        {
            AiProvider.Venice => (
                settings.VeniceApiKey ?? string.Empty,
                string.IsNullOrWhiteSpace(settings.VeniceModel) ? "gpt-4o-mini" : settings.VeniceModel,
                "https://api.venice.ai/api/v1"),
            _ => (
                settings.OpenAiApiKey ?? string.Empty,
                string.IsNullOrWhiteSpace(settings.OpenAiModel) ? "gpt-4o-mini" : settings.OpenAiModel,
                "https://api.openai.com/v1")
        };
    }

    private sealed class ChatCompletionResponse
    {
        public List<ChatChoice>? Choices { get; set; }
    }

    private sealed class ChatChoice
    {
        public ChatMessage? Message { get; set; }
    }

    private sealed class ChatMessage
    {
        public string? Content { get; set; }
    }
}
