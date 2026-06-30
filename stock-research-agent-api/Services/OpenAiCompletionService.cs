using OpenAI.Chat;
using StockResearchAgent.Api.Models;

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
/// </summary>
public class OpenAiCompletionService : IOpenAiCompletionService
{
    private readonly ChatClient? _chatClient;
    private readonly bool _configured;
    private readonly ILogger<OpenAiCompletionService> _logger;

    public bool IsConfigured => _configured;

    public OpenAiCompletionService(IConfiguration configuration, ILogger<OpenAiCompletionService> logger)
    {
        _logger = logger;
        var apiKey = configuration["OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("[openai] OPENAI_API_KEY not set -- AI completions unavailable");
            _configured = false;
            return;
        }

        var model = configuration["OPENAI_MODEL"];
        if (string.IsNullOrWhiteSpace(model))
        {
            model = "gpt-4.1-mini";
        }

        _chatClient = new ChatClient(model, apiKey);
        _configured = true;
    }

    public async Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken)
    {
        if (!_configured || _chatClient is null)
        {
            return new AiCompletionResult { Text = "[OpenAI not configured — OPENAI_API_KEY is missing]" };
        }

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

        ChatCompletion completion = await _chatClient!.CompleteChatAsync(messages, options, cancellationToken);
        var text = completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;

        return new AiCompletionResult { Text = text };
    }

    private static ChatMessage ToChatMessage(AiChatMessageDto dto) => dto.Role.ToLowerInvariant() switch
    {
        "system" => new SystemChatMessage(dto.Content),
        "assistant" => new AssistantChatMessage(dto.Content),
        _ => new UserChatMessage(dto.Content),
    };
}
