using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;

namespace Olga.Nlp.Worker;

public sealed class SciScoringWorker(NpgsqlDataSource dataSource, ILogger<SciScoringWorker> logger) : BackgroundService
{
    private const int BatchSize = 200;
    private const int MaxAttempts = 5;
    private const double RecencyWindowDays = 90d;
    private const double NeutralTaxonomyScore = 0.5d;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConfigurationHealthCheckInterval = TimeSpan.FromMinutes(5);
    private DateTimeOffset _nextConfigurationHealthCheckAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await TryLeaseAsync(stoppingToken);
                if (job is null)
                {
                    await LogBlockedQueueAsync(stoppingToken);
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }
                await ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "SCI scoring cycle failed");
                await Task.Delay(FailureDelay, stoppingToken);
            }
        }
    }

    private async Task<SciJob?> TryLeaseAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using var command = new NpgsqlCommand("""
            WITH next_job AS (
                SELECT j.sci_scoring_job_pk
                FROM nlp.sci_scoring_job j
                WHERE j.status IN ('PENDING','FAILED','RUNNING')
                  AND j.available_at <= CURRENT_TIMESTAMP
                  AND (j.locked_until IS NULL OR j.locked_until < CURRENT_TIMESTAMP)
                  AND EXISTS (
                      SELECT 1 FROM nlp.sci_scoring_config c
                      JOIN nlp.sci_calibration k ON k.sci_scoring_config_pk=c.sci_scoring_config_pk
                      WHERE c.status='ACTIVE' AND c.effective_from <= CURRENT_TIMESTAMP
                        AND (c.effective_to IS NULL OR c.effective_to > CURRENT_TIMESTAMP)
                        AND k.status='ACTIVE' AND k.effective_from <= CURRENT_TIMESTAMP
                        AND (k.effective_to IS NULL OR k.effective_to > CURRENT_TIMESTAMP)
                        AND k.f50 IS NOT NULL AND k.f99 IS NOT NULL AND k.f99 > k.f50
                  )
                ORDER BY j.available_at, j.sci_scoring_job_pk
                LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            UPDATE nlp.sci_scoring_job j
               SET status='RUNNING', locked_until=CURRENT_TIMESTAMP + interval '2 minutes',
                   attempt_count=attempt_count + 1, error_code=NULL, updated_at=CURRENT_TIMESTAMP
              FROM next_job n, core.venture v
             WHERE j.sci_scoring_job_pk=n.sci_scoring_job_pk AND v.venture_pk=j.venture_pk
            RETURNING j.job_id, j.owner_member_id, v.venture_id, j.venture_pk, j.after_link_pk, j.processed_count, j.attempt_count
            """, connection, transaction);
        SciJob? result = null;
        await using (var reader = await command.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) result = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt32(5), reader.GetInt32(6));
        await transaction.CommitAsync(ct);
        return result;
    }

    private async Task LogBlockedQueueAsync(CancellationToken ct)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        if (checkedAt < _nextConfigurationHealthCheckAt) return;
        _nextConfigurationHealthCheckAt = checkedAt.Add(ConfigurationHealthCheckInterval);

        await using var command = dataSource.CreateCommand("""
            SELECT
              EXISTS (
                SELECT 1 FROM nlp.sci_scoring_job
                WHERE status IN ('PENDING','FAILED','RUNNING')
                  AND available_at <= CURRENT_TIMESTAMP
                  AND (locked_until IS NULL OR locked_until < CURRENT_TIMESTAMP)
              ),
              EXISTS (
                SELECT 1 FROM nlp.sci_scoring_config c
                JOIN nlp.sci_calibration k ON k.sci_scoring_config_pk=c.sci_scoring_config_pk
                WHERE c.status='ACTIVE' AND c.effective_from <= CURRENT_TIMESTAMP
                  AND (c.effective_to IS NULL OR c.effective_to > CURRENT_TIMESTAMP)
                  AND k.status='ACTIVE' AND k.effective_from <= CURRENT_TIMESTAMP
                  AND (k.effective_to IS NULL OR k.effective_to > CURRENT_TIMESTAMP)
                  AND k.f50 IS NOT NULL AND k.f99 IS NOT NULL AND k.f99 > k.f50
              )
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct) && reader.GetBoolean(0) && !reader.GetBoolean(1))
            logger.LogError("SCI scoring queue is blocked: no active, in-window calibration with valid f50/f99 is available");
    }

    private async Task ProcessAsync(SciJob job, CancellationToken ct)
    {
        try
        {
            var config = await ReadConfigurationAsync(ct);
            var candidates = await ReadCandidatesAsync(job, ct);
            foreach (var candidate in candidates)
            {
                var clientSemantic = Math.Clamp(1d - candidate.ClientDistance, 0d, 1d);
                var supplierSemantic = Math.Clamp(1d - candidate.SupplierDistance, 0d, 1d);
                var semantic = Math.Max(clientSemantic, supplierSemantic);
                var role = clientSemantic >= supplierSemantic ? "CLIENT" : "SUPPLIER";
                var recency = Math.Clamp((candidate.ValidUntil - DateTimeOffset.UtcNow).TotalDays / RecencyWindowDays, 0d, 1d);
                var taxonomy = NeutralTaxonomyScore; // Neutral until the reviewed taxonomy mapper is configured.
                var raw = Math.Clamp(semantic * config.SemanticWeight + taxonomy * config.TaxonomyWeight + recency * config.RecencyWeight, 0d, 1d);
                var score = Calibrate(raw, config.F50, config.F99);
                var (competitor, qualified) = ReadFlags(candidate.StructuredIntentJson);
                await ApplyAsync(job, candidate, config, raw, score, role, competitor, qualified, ct);
            }

            var more = candidates.Count == BatchSize;
            var nextPk = candidates.Count == 0 ? job.AfterLinkPk : candidates[^1].ContactLinkPk;
            await UpdateJobAsync(job, more ? "PENDING" : "SUCCEEDED", nextPk, job.ProcessedCount + candidates.Count, null, more ? DateTimeOffset.UtcNow : null, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var dead = job.AttemptCount >= MaxAttempts;
            await UpdateJobAsync(job, dead ? "DEAD" : "FAILED", job.AfterLinkPk, job.ProcessedCount, ErrorCode(exception), dead ? null : DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 10 * Math.Pow(2, job.AttemptCount - 1))), ct);
            logger.LogWarning(exception, "SCI job {JobId} failed on attempt {AttemptCount}", job.JobId, job.AttemptCount);
        }
    }

    private async Task<SciConfiguration> ReadConfigurationAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT c.scoring_config_id, c.scorer_version, c.semantic_weight, c.taxonomy_weight, c.recency_weight, k.f50, k.f99
            FROM nlp.sci_scoring_config c
            JOIN nlp.sci_calibration k ON k.sci_scoring_config_pk=c.sci_scoring_config_pk
            WHERE c.status='ACTIVE' AND c.effective_from <= CURRENT_TIMESTAMP AND (c.effective_to IS NULL OR c.effective_to > CURRENT_TIMESTAMP)
              AND k.status='ACTIVE' AND k.effective_from <= CURRENT_TIMESTAMP AND (k.effective_to IS NULL OR k.effective_to > CURRENT_TIMESTAMP)
              AND k.f50 IS NOT NULL AND k.f99 IS NOT NULL AND k.f99 > k.f50
            ORDER BY c.effective_from DESC, k.effective_from DESC LIMIT 1
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("SCI_ACTIVE_CALIBRATION_MISSING");
        return new(reader.GetString(0), reader.GetString(1), Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture),
            Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture), Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture),
            Convert.ToDouble(reader.GetValue(5), CultureInfo.InvariantCulture), Convert.ToDouble(reader.GetValue(6), CultureInfo.InvariantCulture));
    }

    private async Task<List<SciCandidate>> ReadCandidatesAsync(SciJob job, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT * FROM nlp.get_sci_scoring_candidates(@owner_member_id,@venture_id,@after_link_pk,@max_rows)");
        Add(command, "owner_member_id", job.OwnerMemberId); Add(command, "venture_id", job.VentureId);
        command.Parameters.AddWithValue("after_link_pk", job.AfterLinkPk); command.Parameters.AddWithValue("max_rows", BatchSize);
        var rows = new List<SciCandidate>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(new(
            reader.GetInt64(0), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(7),
            reader.GetDouble(12), reader.GetDouble(13), reader.GetString(14), reader.GetFieldValue<DateTimeOffset>(15), reader.GetString(16), reader.GetString(17)));
        return rows;
    }

    private async Task ApplyAsync(SciJob job, SciCandidate candidate, SciConfiguration config, double raw, double score, string role, bool competitor, bool qualified, CancellationToken ct)
    {
        var stableKey = $"{job.JobId}:{candidate.ContactLinkPk}";
        await using var command = dataSource.CreateCommand("""
            SELECT * FROM nlp.apply_sci_link_score(
              @score_id,@contact_link_pk,@venture_pk,@contact_venture_pk,@owner_intent_set_pk,@contact_intent_set_pk,
              @scoring_config_id,@raw_fit,@calibrated_score,@relationship_role,@competitor_overlay,@qualification_met,
              @model_version,@preprocessing_version,@scorer_version,@scored_at,@outbox_event_id)
            """);
        Add(command, "score_id", StableId("score", stableKey)); command.Parameters.AddWithValue("contact_link_pk", candidate.ContactLinkPk);
        command.Parameters.AddWithValue("venture_pk", candidate.OwnerVenturePk); command.Parameters.AddWithValue("contact_venture_pk", candidate.ContactVenturePk);
        command.Parameters.AddWithValue("owner_intent_set_pk", candidate.OwnerIntentSetPk); command.Parameters.AddWithValue("contact_intent_set_pk", candidate.ContactIntentSetPk);
        Add(command, "scoring_config_id", config.Id); command.Parameters.Add("raw_fit", NpgsqlDbType.Numeric).Value = (decimal)raw;
        command.Parameters.Add("calibrated_score", NpgsqlDbType.Numeric).Value = (decimal)score; Add(command, "relationship_role", role);
        command.Parameters.AddWithValue("competitor_overlay", competitor); command.Parameters.AddWithValue("qualification_met", qualified);
        Add(command, "model_version", candidate.ModelVersion); Add(command, "preprocessing_version", candidate.PreprocessingVersion); Add(command, "scorer_version", config.ScorerVersion);
        command.Parameters.AddWithValue("scored_at", DateTimeOffset.UtcNow); Add(command, "outbox_event_id", StableId("sci", stableKey));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task UpdateJobAsync(SciJob job, string status, long afterPk, int processed, string? error, DateTimeOffset? availableAt, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE nlp.sci_scoring_job SET status=@status, after_link_pk=@after_link_pk, processed_count=@processed_count,
              attempt_count=CASE WHEN @status IN ('PENDING','SUCCEEDED') THEN 0 ELSE attempt_count END,
              locked_until=NULL, error_code=@error_code, available_at=COALESCE(@available_at,available_at), updated_at=CURRENT_TIMESTAMP
            WHERE job_id=@job_id
            """);
        Add(command, "status", status); command.Parameters.AddWithValue("after_link_pk", afterPk); command.Parameters.AddWithValue("processed_count", processed);
        Add(command, "error_code", error); command.Parameters.Add("available_at", NpgsqlDbType.TimestampTz).Value = availableAt is null ? DBNull.Value : availableAt.Value; Add(command, "job_id", job.JobId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static double Calibrate(double raw, double f50, double f99) => raw <= f50
        ? Math.Clamp(raw / Math.Max(f50, 0.000001d) * 50d, 0d, 50d)
        : Math.Clamp(50d + ((raw - f50) / (f99 - f50) * 49d), 50d, 100d);

    private static (bool Competitor, bool Qualified) ReadFlags(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var competitor = root.TryGetProperty("competitor", out var competitorValue)
                && competitorValue.ValueKind == JsonValueKind.True;
            var qualified = !root.TryGetProperty("qualification_met", out var qualificationValue)
                || qualificationValue.ValueKind != JsonValueKind.False;
            return (competitor, qualified);
        }
        catch (JsonException)
        {
            // Malformed structured intent fails closed and receives the qualification cap.
            return (false, false);
        }
    }
    private static string StableId(string prefix, string value) => $"{prefix}_{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32]}";
    private static string ErrorCode(Exception exception)
    {
        var value = (exception is PostgresException postgres ? $"PG_{postgres.SqlState}" : exception.Message).Replace(' ', '_');
        return string.IsNullOrWhiteSpace(value) ? "SCI_SCORING_FAILED" : value[..Math.Min(64, value.Length)];
    }
    private static void Add(NpgsqlCommand command, string name, string? value) => command.Parameters.Add(name, NpgsqlDbType.Varchar).Value = value is null ? DBNull.Value : value;

    private sealed record SciJob(string JobId, string OwnerMemberId, string VentureId, long VenturePk, long AfterLinkPk, int ProcessedCount, int AttemptCount);
    private sealed record SciConfiguration(string Id, string ScorerVersion, double SemanticWeight, double TaxonomyWeight, double RecencyWeight, double F50, double F99);
    private sealed record SciCandidate(long ContactLinkPk, long OwnerVenturePk, long OwnerIntentSetPk, long ContactVenturePk, long ContactIntentSetPk, double ClientDistance, double SupplierDistance, string StructuredIntentJson, DateTimeOffset ValidUntil, string ModelVersion, string PreprocessingVersion);
}
