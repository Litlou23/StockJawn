using System.Net;
using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Services.Broker;

namespace StockResearchAgent.Api.Controllers;

// One-time Robinhood login for the executor. The job secret only ever travels in the x-job-secret header;
// the login URL this returns holds nothing secret (PKCE state + challenge), and the callback is protected by that state.
[ApiController]
public class RobinhoodOAuthController : ControllerBase
{
    private readonly RobinhoodOAuthService _oauth;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RobinhoodOAuthController> _logger;

    public RobinhoodOAuthController(RobinhoodOAuthService oauth, IConfiguration configuration, ILogger<RobinhoodOAuthController> logger)
    {
        _oauth = oauth;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("api/robinhood/oauth/login-url")]
    public async Task<IActionResult> LoginUrl(CancellationToken ct)
    {
        if (!SecretMatches(Request.Headers["x-job-secret"].FirstOrDefault()))
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        try
        {
            var fallbackRedirect = $"https://{Request.Host}/api/robinhood/oauth/callback";
            return Ok(new { url = await _oauth.BuildLoginUrlAsync(fallbackRedirect, ct), expiresInMinutes = 10 });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[robinhood-oauth] Could not start login");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("api/robinhood/oauth/callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(error))
            return Page("Robinhood login was not completed", $"{error}: {errorDescription}", ok: false);
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return Page("Robinhood login failed", "Missing code or state", ok: false);

        try
        {
            await _oauth.CompleteLoginAsync(code, state, ct);
            return Page("Robinhood connected", "StockJawn saved the login. You can close this tab.", ok: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[robinhood-oauth] Callback failed");
            return Page("Robinhood login failed", ex.Message, ok: false);
        }
    }

    [HttpGet("api/robinhood/oauth/status")]
    public async Task<IActionResult> Status()
    {
        if (!SecretMatches(Request.Headers["x-job-secret"].FirstOrDefault()))
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        return Ok(await _oauth.GetStatusAsync());
    }

    private bool SecretMatches(string? provided)
    {
        var expected = _configuration["JOB_RUN_SECRET"];
        return !string.IsNullOrWhiteSpace(expected) && provided == expected;
    }

    private ContentResult Page(string title, string message, bool ok) => new()
    {
        ContentType = "text/html; charset=utf-8",
        StatusCode = ok ? 200 : 400,
        Content = $"""
            <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{WebUtility.HtmlEncode(title)}</title></head>
            <body style="font-family:system-ui;background:#09090b;color:#fafafa;display:flex;min-height:100vh;align-items:center;justify-content:center;margin:0;padding:16px">
            <div style="max-width:420px;text-align:center"><h1 style="color:{(ok ? "#22c55e" : "#ef4444")};font-size:22px">{WebUtility.HtmlEncode(title)}</h1>
            <p style="color:#a1a1aa">{WebUtility.HtmlEncode(message)}</p></div></body></html>
            """,
    };
}
