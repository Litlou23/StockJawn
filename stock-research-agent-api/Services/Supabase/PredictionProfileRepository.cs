using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Supabase;

/// <summary>
/// CRUD repository for prediction profiles and their weight configurations.
/// Profiles are named weight sets that flow through the existing ScoringEngine.
/// </summary>
public class PredictionProfileRepository
{
    private readonly SupabaseClient _db;
    private readonly ILogger<PredictionProfileRepository> _logger;

    public PredictionProfileRepository(SupabaseClient db, ILogger<PredictionProfileRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Profile CRUD
    // -----------------------------------------------------------------------

    public async Task<List<PredictionProfile>> GetAllProfilesAsync()
    {
        var rows = await _db.SelectAsync("prediction_profiles",
            order: "created_at.asc");
        return rows.Select(MapProfile).ToList();
    }

    public async Task<List<PredictionProfile>> GetEnabledProfilesAsync()
    {
        var rows = await _db.SelectAsync("prediction_profiles",
            filter: "is_enabled=eq.true", order: "created_at.asc");
        return rows.Select(MapProfile).ToList();
    }

    public async Task<PredictionProfile?> GetProfileByIdAsync(string id)
    {
        var row = await _db.SelectSingleAsync("prediction_profiles", $"id=eq.{id}");
        return row is not null ? MapProfile(row) : null;
    }

    public async Task<PredictionProfile?> GetChampionProfileAsync()
    {
        var row = await _db.SelectSingleAsync("prediction_profiles", "role=eq.champion");
        return row is not null ? MapProfile(row) : null;
    }

    public async Task<PredictionProfile?> CreateProfileAsync(
        string profileName, string? description, ProfileRole role, bool learningEnabled,
        string? hypothesis = null, ExperimentStatus? experimentStatus = null)
    {
        var status = experimentStatus ?? (role == ProfileRole.champion ? ExperimentStatus.active : ExperimentStatus.draft);
        var rows = await _db.InsertAsync("prediction_profiles", new[]
        {
            new
            {
                profile_name = profileName,
                description,
                role = role.ToString(),
                is_enabled = status == ExperimentStatus.testing,
                learning_enabled = learningEnabled,
                experiment_status = status.ToString(),
                hypothesis,
            }
        });
        return rows.Count > 0 ? MapProfile(rows[0]) : null;
    }

    public async Task<bool> UpdateProfileAsync(string id, string profileName, string? description, bool isEnabled, bool learningEnabled,
        string? hypothesis = null, ExperimentStatus? experimentStatus = null)
    {
        var patch = new Dictionary<string, object?>
        {
            ["profile_name"] = profileName,
            ["description"] = description,
            ["is_enabled"] = isEnabled,
            ["learning_enabled"] = learningEnabled,
            ["updated_at"] = DateTimeOffset.UtcNow.ToString("o"),
        };
        if (hypothesis is not null) patch["hypothesis"] = hypothesis;
        if (experimentStatus.HasValue) patch["experiment_status"] = experimentStatus.Value.ToString();
        return await _db.UpdateAsync("prediction_profiles", $"id=eq.{id}", patch);
    }

    /// <summary>
    /// Promote a challenger to champion. The current champion becomes a challenger
    /// with experiment_status = completed.
    /// </summary>
    public async Task<bool> PromoteToChampionAsync(string challengerId)
    {
        var challenger = await GetProfileByIdAsync(challengerId);
        if (challenger is null || challenger.Role != ProfileRole.challenger)
        {
            _logger.LogWarning("[profile-repo] Cannot promote non-challenger {Id}", challengerId);
            return false;
        }

        var currentChampion = await GetChampionProfileAsync();
        if (currentChampion is null)
        {
            _logger.LogError("[profile-repo] No current champion found — cannot promote");
            return false;
        }

        // Demote current champion → challenger + completed
        var demoted = await _db.UpdateAsync("prediction_profiles", $"id=eq.{currentChampion.Id}", new
        {
            role = ProfileRole.challenger.ToString(),
            experiment_status = ExperimentStatus.completed.ToString(),
            is_enabled = false,
            updated_at = DateTimeOffset.UtcNow.ToString("o"),
        });
        if (!demoted)
        {
            _logger.LogError("[profile-repo] Failed to demote current champion {Id}", currentChampion.Id);
            return false;
        }

        // Promote challenger → champion + active
        var promoted = await _db.UpdateAsync("prediction_profiles", $"id=eq.{challengerId}", new
        {
            role = ProfileRole.champion.ToString(),
            experiment_status = ExperimentStatus.active.ToString(),
            is_enabled = true,
            updated_at = DateTimeOffset.UtcNow.ToString("o"),
        });
        if (!promoted)
        {
            // Attempt to rollback the demotion
            await _db.UpdateAsync("prediction_profiles", $"id=eq.{currentChampion.Id}", new
            {
                role = ProfileRole.champion.ToString(),
                experiment_status = ExperimentStatus.active.ToString(),
                is_enabled = true,
                updated_at = DateTimeOffset.UtcNow.ToString("o"),
            });
            _logger.LogError("[profile-repo] Failed to promote challenger {Id}, rolled back demotion", challengerId);
            return false;
        }

        _logger.LogInformation("[profile-repo] Promoted {ChallengerId} to champion, demoted {OldChampionId}", challengerId, currentChampion.Id);
        return true;
    }

    /// <summary>Archive a profile — hides it from active views.</summary>
    public async Task<bool> ArchiveProfileAsync(string id)
    {
        var profile = await GetProfileByIdAsync(id);
        if (profile is null) return false;
        if (profile.Role == ProfileRole.champion)
        {
            _logger.LogWarning("[profile-repo] Cannot archive the champion profile");
            return false;
        }

        return await _db.UpdateAsync("prediction_profiles", $"id=eq.{id}", new
        {
            experiment_status = ExperimentStatus.archived.ToString(),
            is_enabled = false,
            updated_at = DateTimeOffset.UtcNow.ToString("o"),
        });
    }

    public async Task<bool> DeleteProfileAsync(string id)
    {
        // Safety: never delete the champion profile
        var profile = await GetProfileByIdAsync(id);
        if (profile is null || profile.Role == ProfileRole.champion)
        {
            _logger.LogWarning("[profile-repo] Attempted to delete champion or nonexistent profile {Id}", id);
            return false;
        }
        return await _db.DeleteAsync("prediction_profiles", $"id=eq.{id}");
    }

    // -----------------------------------------------------------------------
    // Profile Configs (weight overrides per profile)
    // -----------------------------------------------------------------------

    public async Task<List<PredictionProfileConfig>> GetProfileConfigsAsync(string profileId)
    {
        var rows = await _db.SelectAsync("prediction_profile_configs",
            filter: $"profile_id=eq.{profileId}");
        return rows.Select(MapConfig).ToList();
    }

    public async Task<Dictionary<string, double>> GetProfileWeightsAsync(string profileId)
    {
        var configs = await GetProfileConfigsAsync(profileId);
        return configs.ToDictionary(c => c.ConfigKey, c => c.ConfigValue);
    }

    public async Task<bool> SetProfileConfigAsync(string profileId, string configKey, double configValue)
    {
        // Upsert: unique constraint on (profile_id, config_key)
        return await _db.UpsertAsync("prediction_profile_configs", new[]
        {
            new
            {
                profile_id = profileId,
                config_key = configKey,
                config_value = configValue,
            }
        }, onConflict: "profile_id,config_key");
    }

    public async Task<bool> SetProfileConfigsAsync(string profileId, Dictionary<string, double> weights)
    {
        // Delete existing configs and insert fresh set
        await _db.DeleteAsync("prediction_profile_configs", $"profile_id=eq.{profileId}");

        if (weights.Count == 0) return true;

        var rows = weights.Select(kv => new
        {
            profile_id = profileId,
            config_key = kv.Key,
            config_value = kv.Value,
        }).ToArray();

        await _db.InsertAsync("prediction_profile_configs", rows, returnRows: false);
        return true;
    }

    // -----------------------------------------------------------------------
    // Clone
    // -----------------------------------------------------------------------

    public async Task<PredictionProfile?> CloneProfileAsync(string sourceProfileId, string newName, string? description = null, string? hypothesis = null)
    {
        var source = await GetProfileByIdAsync(sourceProfileId);
        if (source is null) return null;

        var newProfile = await CreateProfileAsync(
            newName,
            description ?? $"Cloned from {source.ProfileName}",
            ProfileRole.challenger,
            learningEnabled: false,
            hypothesis: hypothesis);

        if (newProfile is null) return null;

        // Copy weight configs
        var sourceConfigs = await GetProfileConfigsAsync(sourceProfileId);
        if (sourceConfigs.Count > 0)
        {
            var weights = sourceConfigs.ToDictionary(c => c.ConfigKey, c => c.ConfigValue);
            await SetProfileConfigsAsync(newProfile.Id, weights);
        }

        return newProfile;
    }

    // -----------------------------------------------------------------------
    // Mappers
    // -----------------------------------------------------------------------

    private static PredictionProfile MapProfile(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        ProfileName = r["profile_name"]?.ToString() ?? "",
        Description = r["description"]?.ToString(),
        Role = Enum.TryParse<ProfileRole>(r["role"]?.ToString(), out var role) ? role : ProfileRole.challenger,
        IsEnabled = r["is_enabled"]?.GetValue<bool>() ?? true,
        LearningEnabled = r["learning_enabled"]?.GetValue<bool>() ?? false,
        ExperimentStatus = Enum.TryParse<ExperimentStatus>(r["experiment_status"]?.ToString(), out var es) ? es : ExperimentStatus.active,
        Hypothesis = r["hypothesis"]?.ToString(),
        CreatedAt = GetDateTimeOffset(r, "created_at"),
        UpdatedAt = GetDateTimeOffset(r, "updated_at"),
    };

    private static PredictionProfileConfig MapConfig(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        ProfileId = r["profile_id"]?.ToString() ?? "",
        ConfigKey = r["config_key"]?.ToString() ?? "",
        ConfigValue = GetDouble(r, "config_value"),
        Description = r["description"]?.ToString(),
        CreatedAt = GetDateTimeOffset(r, "created_at"),
        UpdatedAt = GetDateTimeOffset(r, "updated_at"),
    };

    private static double GetDouble(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return 0;
        if (node is JsonValue jv && jv.TryGetValue<double>(out var d)) return d;
        return double.TryParse(node.ToString(), out var parsed) ? parsed : 0;
    }

    private static DateTimeOffset GetDateTimeOffset(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return DateTimeOffset.MinValue;
        return DateTimeOffset.TryParse(node.ToString(), out var dt) ? dt : DateTimeOffset.MinValue;
    }
}
