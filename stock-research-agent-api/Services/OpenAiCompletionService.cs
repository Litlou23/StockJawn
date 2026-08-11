using System.Collections.Concurrent;
using System.Text.Json;
using OpenAI.Chat;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services;

public interface IOpenAiCompletionService
{
    bool IsConfigured { get; }
    Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The only place in this project that talks to OpenAI. Holds the API key
/// (read from configuration/environment, never hardcoded) and does nothing
/// else — no business logic, no app data. The caller (Next.js) is
/// responsible for building the messages it wants sent.
///
/// The model is resolved at runtime from scoring_weight_overrides
/// (signal_name = 'openai_model'). Change it in the DB and the next
/// request picks it up — no redeploy needed. Numeric mapping:
///   0 = gpt-4.1-mini
///   1 = gpt-4.1
///   2 = gpt-4o
///   3 = gpt-4o-mini
///   4 = gpt-5.6-luna   (recommended — cheapest, newest gen)
///   5 = gpt-5.6-terra
///   6 = gpt-5.6-sol
/// </summary>
public class OpenAiCompletionService : IOpenAiCompletionService
{
    private static readonly Dictionary<int, string> ModelMap = new()
    {
        { 0, "gpt-4.1-mini" },
        { 1, "gpt-4.1" },
        { 2, "gpt-4o" },
        { 3, "gpt-4o-mini" },
        { 4, "gpt-5.6-luna" },
        { 5, "gpt-5.6-terra" },
        { 6, "gpt-5.6-sol" },
    };

    private const string DefaultModel = "gpt-5.6-luna";
    private const int CacheMinutes = 5;

    private readonly string? _apiKey;
    private readonly bool _configured;
    private readonly SupabaseClient _db;
    private readonly ILogger<OpenAiCompletionService> _logger;

    // Cache: model name + expiry so we don't hit DB every call
    private readonly ConcurrentDictionary<string, (string Model, DateTime Expiry)> _modelCache = new();
    // Cache ChatClient instances per model string to avoid re-creating them
    private readonly ConcurrentDictionary<string, ChatClient> _clientCache = new();

    public bool IsConfigured => _configured;

    public OpenAiCompletionService(
        IConfiguration configuration,
        SupabaseClient db,
        ILogger<OpenAiCompletionService> logger)
    {
        _db = db;
        _logger = logger;
        _apiKey = configuration["OPENAI_API_KEY"];

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning("[openai] OPENAI_API_KEY not set -- AI completions unavailable");
            _configured = false;
            return;
        }

        _configured = true;
    }

    public async Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken)
    {
        if (!_configured || _apiKey is null)
        {
            return new AiCompletionResult { Text = "[OpenAI not configured — OPENAI_API_KEY is missing]" };
        }

        // Per-request model override for high-stakes decisions (e.g., Terra for
        // portfolio entry/exit) while keeping Luna for bulk predictions.
        var model = request.ModelOverride is { } overrideKey && ModelMap.TryGetValue(overrideKey, out var overrideModel)
            ? overrideModel
            : await ResolveModelAsync();
        var client = _clientCache.GetOrAdd(model, m => new ChatClient(m, _apiKey));

        var messages = request.Messages.Select(ToChatMessage).ToList();

        var options = new ChatCompletionOptions();
        if (request.MaxOutputTokens is { } maxTokens)
        {
            options.MaxOutputTokenCount = maxTokens;
        }
        if (request.ResponseFormatJson)
        {
            options.ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat();
        }

        // Add tool definitions if provided
        if (request.Tools is { Count: > 0 })
        {
            foreach (var tool in request.Tools)
            {
                var paramsJson = tool.Function.Parameters is not null
                    ? BinaryData.FromString(JsonSerializer.Serialize(tool.Function.Parameters))
                    : BinaryData.FromString("{}");

                options.Tools.Add(ChatTool.CreateFunctionTool(
                    tool.Function.Name,
                    tool.Function.Description,
                    paramsJson));
            }
        }

        ChatCompletion completion = await client.CompleteChatAsync(messages, options, cancellationToken);

        // Check if the model wants to call tools
        if (completion.FinishReason == ChatFinishReason.ToolCalls && completion.ToolCalls.Count > 0)
        {
            var toolCalls = completion.ToolCalls.Select(tc => new AiToolCallDto
            {
                Id = tc.Id,
                Name = tc.FunctionName,
                Arguments = tc.FunctionArguments?.ToString() ?? "{}",
            }).ToList();

            return new AiCompletionResult
            {
                Text = "",
                ToolCalls = toolCalls,
                FinishReason = "tool_calls",
            };
        }

        var text = completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;
        return new AiCompletionResult
        {
            Text = text ?? "",
            FinishReason = "stop",
        };
    }

    /// <summary>
    /// Reads the model from scoring_weight_overrides (cached for 5 min).
    /// signal_name = 'openai_model', effective_weight maps to ModelMap.
    /// </summary>
    private async Task<string> ResolveModelAsync()
    {
        const string cacheKey = "openai_model";

        if (_modelCache.TryGetValue(cacheKey, out var cached) && cached.Expiry > DateTime.UtcNow)
            return cached.Model;

        try
        {
            var row = await _db.SelectSingleAsync(
                "scoring_weight_overrides",
                "signal_name=eq.openai_model");

            var model = DefaultModel;
            if (row is not null)
            {
                var weight = row["effective_weight"]?.GetValue<double>() ?? 1.0;
                var key = (int)Math.Round(weight);
                if (ModelMap.TryGetValue(key, out var mapped))
                    model = mapped;
            }

            _modelCache[cacheKey] = (model, DateTime.UtcNow.AddMinutes(CacheMinutes));
            _logger.LogDebug("[openai] Resolved model: {Model}", model);
            return model;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[openai] Failed to read model from DB, using default {Model}", DefaultModel);
            _modelCache[cacheKey] = (DefaultModel, DateTime.UtcNow.AddMinutes(1)); // shorter cache on error
            return DefaultModel;
        }
    }

    private static ChatMessage ToChatMessage(AiChatMessageDto dto)
    {
        var role = dto.Role.ToLowerInvariant();

        // Tool result message
        if (role == "tool" && dto.ToolCallId is not null)
        {
            return new ToolChatMessage(dto.ToolCallId, dto.Content);
        }

        // Assistant message with tool calls
        if (role == "assistant" && dto.ToolCalls is { Count: > 0 })
        {
            var toolCalls = dto.ToolCalls.Select(tc =>
                ChatToolCall.CreateFunctionToolCall(
                    tc.Id,
                    tc.Name,
                    BinaryData.FromString(tc.Arguments))).ToList();

            return new AssistantChatMessage(toolCalls);
        }

        return role switch
        {
            "system" => new SystemChatMessage(dto.Content),
            "assistant" => new AssistantChatMessage(dto.Content),
            _ => new UserChatMessage(dto.Content),
        };
    }
}
