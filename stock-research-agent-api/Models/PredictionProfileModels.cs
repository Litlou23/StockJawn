using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

// ---------------------------------------------------------------------------
// Prediction Profile — a named weight configuration set
// ---------------------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProfileRole
{
    champion,
    challenger
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExperimentStatus
{
    active,    // champion's permanent state
    draft,     // challenger created but not yet running
    testing,   // challenger actively generating predictions
    completed, // experiment finished, results available
    archived   // retired from view
}

public record PredictionProfile
{
    public string Id { get; init; } = "";
    public string ProfileName { get; init; } = "";
    public string? Description { get; init; }
    public ProfileRole Role { get; init; } = ProfileRole.challenger;
    public bool IsEnabled { get; init; } = true;
    public bool LearningEnabled { get; init; }
    public ExperimentStatus ExperimentStatus { get; init; } = ExperimentStatus.active;
    public string? Hypothesis { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

// ---------------------------------------------------------------------------
// Prediction Profile Config — weight overrides for a profile
// ---------------------------------------------------------------------------

public record PredictionProfileConfig
{
    public string Id { get; init; } = "";
    public string ProfileId { get; init; } = "";
    public string ConfigKey { get; init; } = "";
    public double ConfigValue { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
