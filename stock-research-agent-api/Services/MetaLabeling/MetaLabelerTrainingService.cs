using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ML;
using Microsoft.ML.Data;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.MetaLabeling;

/// <summary>
/// Trains the meta-labeler model using ML.NET FastTree (a gradient boosted
/// decision tree family — the algorithm the industry defaults to for tabular
/// financial data).
///
/// Flow:
///   1. Load rows from meta_labeler_training_data (features_json → float[]).
///   2. 80/20 train/test split.
///   3. Train FastTree binary classifier.
///   4. Evaluate on test set.
///   5. Save .zip artifact to disk, register version in meta_labeler_models,
///      flip is_active off on the old one and on for the new one.
///
/// Model artifacts are written to config["META_LABELER_MODELS_DIR"], defaulting
/// to {ContentRoot}/meta_labeler_models/. Filename convention:
///   meta_labeler_v{version}.zip
/// </summary>
public class MetaLabelerTrainingService
{
    private const string DataTable = "meta_labeler_training_data";
    private const string ModelsTable = "meta_labeler_models";
    private const int MinRowsRequired = 100;

    private readonly SupabaseClient _db;
    private readonly MetaLabelerFeatureExtractor _features;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<MetaLabelerTrainingService> _logger;

    public MetaLabelerTrainingService(
        SupabaseClient db,
        MetaLabelerFeatureExtractor features,
        IConfiguration config,
        IWebHostEnvironment env,
        ILogger<MetaLabelerTrainingService> logger)
    {
        _db = db;
        _features = features;
        _config = config;
        _env = env;
        _logger = logger;
    }

    public record TrainingResult
    {
        public bool Success { get; init; }
        public int? Version { get; init; }
        public string? ArtifactPath { get; init; }
        public int TrainRows { get; init; }
        public int TestRows { get; init; }
        public int PositiveLabels { get; init; }
        public int NegativeLabels { get; init; }
        public double? Auc { get; init; }
        public double? Accuracy { get; init; }
        public double? F1 { get; init; }
        public double? Precision { get; init; }
        public double? Recall { get; init; }
        public List<KeyValuePair<string, double>>? TopFeatures { get; init; }
        public string? Error { get; init; }
    }

    public async Task<TrainingResult> TrainAsync()
    {
        _logger.LogInformation("[meta-labeler] Starting training run");

        var rows = await LoadTrainingRowsAsync();
        if (rows.Count < MinRowsRequired)
        {
            var msg = $"Not enough training data: {rows.Count} rows (need at least {MinRowsRequired})";
            _logger.LogWarning("[meta-labeler] {Msg}", msg);
            return new TrainingResult { Success = false, Error = msg };
        }

        var featureCount = _features.FeatureCount;
        var pos = rows.Count(r => r.Label);
        var neg = rows.Count - pos;
        _logger.LogInformation("[meta-labeler] Loaded {N} rows ({Pos} wins / {Neg} losses)", rows.Count, pos, neg);

        // ── ML.NET pipeline ──
        var ml = new MLContext(seed: 1337);

        // Schema definition — vector size known only at runtime via _features.FeatureCount.
        var schema = SchemaDefinition.Create(typeof(TrainingRow));
        schema[nameof(TrainingRow.Features)].ColumnType =
            new VectorDataViewType(NumberDataViewType.Single, featureCount);

        var dataView = ml.Data.LoadFromEnumerable(rows, schema);
        var split = ml.Data.TrainTestSplit(dataView, testFraction: 0.20, seed: 1337);

        var pipeline = ml.BinaryClassification.Trainers.FastTree(
            labelColumnName: nameof(TrainingRow.Label),
            featureColumnName: nameof(TrainingRow.Features),
            numberOfLeaves: 20,
            numberOfTrees: 100,
            minimumExampleCountPerLeaf: 10,
            learningRate: 0.1);

        _logger.LogInformation("[meta-labeler] Training FastTree — {Trees} trees × {Leaves} leaves", 100, 20);
        var model = pipeline.Fit(split.TrainSet);

        // ── Evaluation ──
        var predictions = model.Transform(split.TestSet);
        var metrics = ml.BinaryClassification.Evaluate(
            predictions,
            labelColumnName: nameof(TrainingRow.Label));

        _logger.LogInformation(
            "[meta-labeler] Test AUC={Auc:F3} Acc={Acc:F3} F1={F1:F3} P={P:F3} R={R:F3}",
            metrics.AreaUnderRocCurve, metrics.Accuracy, metrics.F1Score,
            metrics.PositivePrecision, metrics.PositiveRecall);

        // Feature importance from the trained tree (top 10)
        var topFeatures = ExtractFeatureImportance(rows, model, ml);

        // ── Persist artifact + registry row ──
        var version = await GetNextVersionAsync();
        var (artifactPath, sizeBytes) = SaveArtifact(ml, model, split.TrainSet.Schema, version);

        int trainCount = 0, testCount = 0;
        foreach (var _ in ml.Data.CreateEnumerable<TrainingRow>(split.TrainSet, reuseRowObject: true)) trainCount++;
        foreach (var _ in ml.Data.CreateEnumerable<TrainingRow>(split.TestSet, reuseRowObject: true)) testCount++;

        await RegisterModelAsync(new
        {
            version,
            training_row_count = trainCount,
            positive_label_count = pos,
            negative_label_count = neg,
            test_row_count = testCount,
            test_accuracy = metrics.Accuracy,
            test_auc = metrics.AreaUnderRocCurve,
            test_f1 = metrics.F1Score,
            test_precision_at_50 = metrics.PositivePrecision,
            test_recall_at_50 = metrics.PositiveRecall,
            feature_count = featureCount,
            feature_names_json = JsonSerializer.Serialize(MetaLabelerFeatureExtractor.FeatureNames),
            top_features_json = JsonSerializer.Serialize(topFeatures),
            artifact_path = artifactPath,
            artifact_size_bytes = sizeBytes,
            trainer = "FastTree",
            hyperparameters_json = JsonSerializer.Serialize(new
            {
                numberOfLeaves = 20,
                numberOfTrees = 100,
                minimumExampleCountPerLeaf = 10,
                learningRate = 0.1,
                featureExtractorVersion = MetaLabelerFeatureExtractor.FeatureVersion,
            }),
            is_active = true,
            notes = $"Trained on {rows.Count} labeled predictions",
        });

        await DeactivateOtherModelsAsync(version);

        return new TrainingResult
        {
            Success = true,
            Version = version,
            ArtifactPath = artifactPath,
            TrainRows = trainCount,
            TestRows = testCount,
            PositiveLabels = pos,
            NegativeLabels = neg,
            Auc = metrics.AreaUnderRocCurve,
            Accuracy = metrics.Accuracy,
            F1 = metrics.F1Score,
            Precision = metrics.PositivePrecision,
            Recall = metrics.PositiveRecall,
            TopFeatures = topFeatures,
        };
    }

    // ── Helpers ──────────────────────────────────────────────────

    private async Task<List<TrainingRow>> LoadTrainingRowsAsync()
    {
        var rows = new List<TrainingRow>();
        // MVP: single 10k limit. If we outgrow this, add PostgREST offset
        // support to SupabaseClient and cursor-paginate here.
        var page = await _db.SelectAsync(DataTable,
            order: "prediction_created_at.asc",
            limit: 10000);

        foreach (var r in page)
        {
            try
            {
                var featuresJson = r["features_json"]?.ToString();
                if (string.IsNullOrWhiteSpace(featuresJson)) continue;

                var features = JsonSerializer.Deserialize<float[]>(featuresJson);
                if (features is null || features.Length != _features.FeatureCount) continue;

                var label = r["label"]?.GetValue<int>() == 1;
                rows.Add(new TrainingRow { Features = features, Label = label });
            }
            catch { /* skip malformed row */ }
        }

        return rows;
    }

    private (string path, long size) SaveArtifact(MLContext ml, ITransformer model, DataViewSchema schema, int version)
    {
        var dir = _config["META_LABELER_MODELS_DIR"]
            ?? Path.Combine(_env.ContentRootPath, "meta_labeler_models");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"meta_labeler_v{version}.zip");
        ml.Model.Save(model, schema, path);
        var size = new FileInfo(path).Length;
        _logger.LogInformation("[meta-labeler] Saved artifact v{V} → {Path} ({Size} bytes)", version, path, size);
        return (path, size);
    }

    private async Task<int> GetNextVersionAsync()
    {
        var rows = await _db.SelectAsync(ModelsTable,
            order: "version.desc", limit: 1);
        if (rows.Count == 0) return 1;
        var v = rows[0]["version"]?.GetValue<int>() ?? 0;
        return v + 1;
    }

    private async Task RegisterModelAsync(object row)
    {
        await _db.InsertAsync(ModelsTable, new[] { row });
    }

    private async Task DeactivateOtherModelsAsync(int keepVersion)
    {
        // Partial unique index on (is_active) where is_active = true — flip old
        // rows before inserting the new one. Insert happens first inside
        // RegisterModelAsync, so we run a compensating update here.
        await _db.UpdateAsync(ModelsTable,
            $"version=neq.{keepVersion}",
            new { is_active = false });
    }

    /// <summary>
    /// Feature importance placeholder. ML.NET 4.0's
    /// BinaryClassificationCatalog.PermutationFeatureImportance requires
    /// a specific ISingleFeaturePredictionTransformer signature that our
    /// pipeline (fit via IEstimator&lt;ITransformer&gt;) doesn't expose cleanly.
    /// Returning an empty list keeps top_features_json non-null but empty
    /// — a UI hint that this metric wasn't computed. Wire up properly if
    /// interpretability becomes important.
    /// </summary>
    private List<KeyValuePair<string, double>> ExtractFeatureImportance(
        List<TrainingRow> rows, ITransformer model, MLContext ml)
        => new();

    // ── ML.NET row schemas ───────────────────────────────────────

    public class TrainingRow
    {
        // ColumnType overridden at runtime — see SchemaDefinition above.
        public float[] Features { get; set; } = [];
        public bool Label { get; set; }
    }
}
