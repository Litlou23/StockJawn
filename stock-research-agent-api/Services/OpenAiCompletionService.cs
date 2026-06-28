using OpenAI.Chat;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services;

public interface IOpenAiCompletionService
{
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
    private readonly ChatClient _chatClient;

    public OpenAiCompletionService(IConfiguration configuration)
    {
        var apiKey = configuration["OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "OPENAI_API_KEY is not configured. Set it as an environment variable or via dotnet user-secrets.");
        }

        var model = configuration["OPENAI_MODEL"];
        if (string.IsNullOrWhiteSpace(model))
        {
            model = "gpt-4.1-mini";
        }

        _chatClient = new ChatClient(model, apiKey);
    }

    public async Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken)
    {
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

        ChatCompletion completion = await _chatClient.CompleteChatAsync(messages, options, cancellationToken);
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
