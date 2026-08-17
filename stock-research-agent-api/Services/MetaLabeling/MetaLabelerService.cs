using System.Text.Json;
using Microsoft.ML;
using Microsoft.ML.Data;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.MetaLabeling;

/// <summary>
/// Meta-labeler inference. Loads the active trained model at startup (or on
/// demand via ReloadAsync) and scores individual ScoringBreakdown inputs.
///
/// Output is a probability (0.0–1.0) that the primary scoring engine's
/// prediction will hit take-profit before stop-loss + time barrier.
///
/// Registered as singleton. Thread-safe reads via ThreadStatic-cached
/// prediction engines (ML.NET requirement).
/// </summary>
public class MetaLabelerService
{
    private const string ModelsTable = "meta_labeler_models";

    private readonly SupabaseClient _db;
    private readonly MetaLabelerFeatureExtractor _features;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<MetaLabelerService> _logger;

    private readonly MLContext _ml = new(seed: 1337);
    private ITransformer? _model;
    private DataViewSchema? _modelSchema;
    private int? _activeVersion;
    private int _featureVersionSnapshot;

    // Enforcement threshold cache — refreshed lazily every 60 seconds so
    // toggling the row in scoring_weight_overrides takes effect without a
    // process restart, but hot-path scoring doesn't hit the DB per candidate.
    private double? _cachedThreshold;
    private DateTimeOffset _thresholdExpiresAt = DateTimeOffset.MinValue;
    private readonly TimeSpan _thresholdTtl = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _thresholdLock = new(1, 1);

    // Thread-static prediction engine pool — ML.NET's PredictionEngine is not
    // thread-safe, so each thread gets its own instance.
    [ThreadStatic]
    private static PredictionEngine<MetaLabelerTrainingService.TrainingRow, MetaLabelerPrediction>? _engine;

    public MetaLabelerService(
        SupabaseClient db,
        MetaLabelerFeatureExtractor features,
        IConfiguration config,
        IWebHostEnvironment env,
        ILogger<MetaLabelerService> logger)
    {
        _db = db;
        _features = features;
        _config = config;
        _env = env;
        _logger = logger;
    }

    public bool IsReady => _model is not null;
    public int? ActiveVersion => _activeVersion;

    /// <summary>
    /// Score a candidate. Returns null if no model is loaded (advisory mode
    /// callers should treat null as "no opinion" and fall back to their
    /// existing gate).
    /// </summary>
    public float? Score(ScoringBreakdown breakdown, PredictionCandidate? prediction = null, int? daysUntilEarnings = null)
    {
        if (_model is null || _modelSchema is null) return null;

        try
        {
            _engine ??= _ml.Model.CreatePredictionEngine<MetaLabelerTrainingService.TrainingRow, MetaLabelerPrediction>(_model);
            var input = new MetaLabelerTrainingService.TrainingRow
            {
                Features = _features.Extract(breakdown, prediction, daysUntilEarnings),
                Label = false, // unused at inference
            };
            var result = _engine.Predict(input);
            return result.Probability;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[meta-labeler] Score failed — returning null (advisory)");
            return null;
        }
    }

    /// <summary>
    /// Read the enforcement threshold from scoring_weight_overrides. Cached
    /// for 60 s. Returns null when no active override exists — callers should
    /// treat that as "advisory only, don't gate."
    /// </summary>
    public async Task<double?> GetEnforcementThresholdAsync()
    {
        if (_cachedThreshold is not null && DateTimeOffset.UtcNow < _thresholdExpiresAt)
            return _cachedThreshold;

        await _thresholdLock.WaitAsync();
        try
        {
            if (_cachedThreshold is not null && DateTimeOffset.UtcNow < _thresholdExpiresAt)
                return _cachedThreshold;

            var row = await _db.SelectSingleAsync("scoring_weight_overrides",
                "signal_name=eq.meta_labeler_enforce_threshold&status=eq.active");

            double? threshold = null;
            if (row is not null)
            {
                var w = row["effective_weight"];
                if (w is not null && w.GetValueKind() != System.Text.Json.JsonValueKind.Null)
                {
                    var val = w.GetValue<double>();
                    if (val > 0 && val <= 1) threshold = val;
                }
            }

            _cachedThreshold = threshold;
            _thresholdExpiresAt = DateTimeOffset.UtcNow.Add(_thresholdTtl);
            return threshold;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[meta-labeler] Failed to read enforcement threshold — defaulting to advisory");
            return null;
        }
        finally { _thresholdLock.Release(); }
    }

    /// <summary>
    /// Sync convenience wrapper — non-blocking, returns whatever's cached.
    /// Callers that need a fresh read must use GetEnforcementThresholdAsync.
    /// </summary>
    public double? GetCachedEnforcementThreshold() => _cachedThreshold;

    /// <summary>
    /// Load the active model from disk. Called at startup (via IHostedService)
    /// and after each training run to swap in the new artifact without a
    /// process restart.
    /// </summary>
    public async Task<bool> ReloadAsync()
    {
        try
        {
            var activeRow = await _db.SelectSingleAsync(ModelsTable, "is_active=eq.true");
            if (activeRow is null)
            {
                _logger.LogInformation("[meta-labeler] No active model in registry — inference disabled");
                _model = null;
                _modelSchema = null;
                _activeVersion = null;
                return false;
            }

            var artifactPath = activeRow["artifact_path"]?.ToString();
            var version = activeRow["version"]?.GetValue<int>();
            if (artifactPath is null || version is null)
            {
                _logger.LogWarning("[meta-labeler] Active model row missing artifact_path/version");
                return false;
            }

            // Feature layout guard — models trained with an older extractor
            // version should not be scored with a newer extractor.
            var hyperparams = activeRow["hyperparameters_json"]?.ToString();
            if (!string.IsNullOrWhiteSpace(hyperparams))
            {
                try
                {
                    var hp = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(hyperparams);
                    if (hp is not null && hp.TryGetValue("featureExtractorVersion", out var fv))
                    {
                        var trainedVersion = fv.GetInt32();
                        if (trainedVersion != MetaLabelerFeatureExtractor.FeatureVersion)
                        {
                            _logger.LogWarning(
                                "[meta-labeler] Feature version mismatch: model v{ModelV} trained against extractor v{Trained}, current extractor is v{Current}. Refusing to load.",
                                version, trainedVersion, MetaLabelerFeatureExtractor.FeatureVersion);
                            _model = null;
                            _modelSchema = null;
                            _activeVersion = null;
                            return false;
                        }
                    }
                }
                catch { /* if we can't parse hyperparams, load anyway */ }
            }

            // Support both absolute paths and relative-to-content-root paths.
            var resolvedPath = Path.IsPathRooted(artifactPath)
                ? artifactPath
                : Path.Combine(_env.ContentRootPath, artifactPath);

            if (!File.Exists(resolvedPath))
            {
                _logger.LogWarning("[meta-labeler] Artifact file not found at {Path}", resolvedPath);
                return false;
            }

            var model = _ml.Model.Load(resolvedPath, out var schema);
            _model = model;
            _modelSchema = schema;
            _activeVersion = version;
            _featureVersionSnapshot = MetaLabelerFeatureExtractor.FeatureVersion;
            // Force the thread-static engine cache to rebuild against the new model
            _engine = null;

            _logger.LogInformation("[meta-labeler] Loaded active model v{V} from {Path}", version, resolvedPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[meta-labeler] ReloadAsync failed");
            return false;
        }
    }

    // ── ML.NET output schema ─────────────────────────────────────

    public class MetaLabelerPrediction
    {
        [ColumnName("PredictedLabel")]
        public bool PredictedLabel { get; set; }

        [ColumnName("Score")]
        public float Score { get; set; }

        [ColumnName("Probability")]
        public float Probability { get; set; }
    }
}

/// <summary>
/// Loads the active meta-labeler model at app startup so the first live
/// prediction doesn't pay the deserialization cost. Fire-and-forget — inference
/// is advisory, so a load failure is logged but not fatal.
/// </summary>
public class MetaLabelerLoaderHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MetaLabelerLoaderHostedService> _logger;

    public MetaLabelerLoaderHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<MetaLabelerLoaderHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<MetaLabelerService>();
            await svc.ReloadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[meta-labeler] Startup load failed (advisory — will retry on demand)");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
