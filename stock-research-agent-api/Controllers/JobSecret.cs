using System.Security.Cryptography;
using System.Text;

namespace StockResearchAgent.Api.Controllers;

// Header only: a secret in a URL ends up in logs and browser history.
internal static class JobSecret
{
    public static bool Matches(HttpRequest request, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return false;
        var provided = request.Headers["x-job-secret"].FirstOrDefault() ?? "";
        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
