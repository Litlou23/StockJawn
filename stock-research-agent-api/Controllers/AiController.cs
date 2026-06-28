using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services;

namespace StockResearchAgent.Api.Controllers;

[ApiController]
[Route("api/ai")]
public class AiController : ControllerBase
{
    private readonly IOpenAiCompletionService _completionService;
    private readonly ILogger<AiController> _logger;

    public AiController(IOpenAiCompletionService completionService, ILogger<AiController> logger)
    {
        _completionService = completionService;
        _logger = logger;
    }

    /// <summary>
    /// Forwards a fully-built message list to OpenAI and returns the raw
    /// completion text. Called only by the Next.js server (never from a
    /// browser) — this app holds the OpenAI API key, so it must not be
    /// exposed publicly without its own auth in front of it.
    /// </summary>
    [HttpPost("complete")]
    public async Task<ActionResult<AiCompletionResult>> Complete(
        [FromBody] AiCompletionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Messages.Count == 0)
        {
            return BadRequest(new { error = "messages must contain at least one entry" });
        }

        try
        {
            var result = await _completionService.CompleteAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI completion call failed");
            return StatusCode(502, new { error = "AI provider call failed" });
        }
    }
}
