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

    /// <summary>Tool call ID — set when Role is "tool" to tie a tool result back to the call.</summary>
    public string? ToolCallId { get; set; }

    /// <summary>Tool calls requested by the assistant. Only present when Role is "assistant" and the model chose to call tools.</summary>
    public List<AiToolCallDto>? ToolCalls { get; set; }
}

/// <summary>One tool call from the model (function name + JSON arguments).</summary>
public class AiToolCallDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "{}";
}

/// <summary>Tool definition sent with the request so the model knows what functions are available.</summary>
public class AiToolDefinitionDto
{
    public string Type { get; set; } = "function";
    public AiFunctionDefinitionDto Function { get; set; } = new();
}

public class AiFunctionDefinitionDto
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>JSON Schema object describing the function parameters.</summary>
    public object? Parameters { get; set; }
}

public class AiCompletionRequest
{
    public List<AiChatMessageDto> Messages { get; set; } = [];
    public int? MaxOutputTokens { get; set; }
    public bool ResponseFormatJson { get; set; }

    /// <summary>Tool definitions — when present, the model may respond with tool_calls instead of text.</summary>
    public List<AiToolDefinitionDto>? Tools { get; set; }

    /// <summary>
    /// Override the global model for this single request. Uses the same
    /// numeric mapping as scoring_weight_overrides 'openai_model':
    ///   0=gpt-4.1-mini, 1=gpt-4.1, 4=gpt-5.6-luna, 5=gpt-5.6-terra, 6=gpt-5.6-sol
    /// Null = use the global default from DB.
    /// </summary>
    public int? ModelOverride { get; set; }
}

public class AiCompletionResult
{
    public string Text { get; set; } = string.Empty;

    /// <summary>Non-null when the model chose to call tools instead of producing text.</summary>
    public List<AiToolCallDto>? ToolCalls { get; set; }

    /// <summary>"stop" for normal text, "tool_calls" when the model wants tool results.</summary>
    public string FinishReason { get; set; } = "stop";
}
