namespace StockResearchAgent.Api.Models;

/// <summary>
/// A single chat message in OpenAI's role/content shape. The Next.js app
/// builds the full message list (system prompt + serialized app context +
/// chat history + the user's message) — this API never builds prompts or
/// reads app/business data itself. It only forwards messages to OpenAI.
/// </summary>
public class AiChatMessageDto
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
}

public class AiCompletionRequest
{
    public List<AiChatMessageDto> Messages { get; set; } = [];
    public int? MaxOutputTokens { get; set; }
    public bool ResponseFormatJson { get; set; }
}

public class AiCompletionResult
{
    public string Text { get; set; } = string.Empty;
}
