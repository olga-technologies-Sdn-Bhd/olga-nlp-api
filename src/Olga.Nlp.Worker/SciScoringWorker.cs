using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;

namespace Olga.Nlp.Worker;

public sealed class SciScoringWorker(NpgsqlDataSource dataSource, ILogger<SciScoringWorker> logger) : BackgroundService
{
    private const int BatchSize = 200;
    private const int MaxAttempts = 5;
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
                logger.LogError(exception, "SCI v2 scoring cycle failed");
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
                      WHERE c.scoring_config_id='SCI_GLOBAL_V2' AND c.status='ACTIVE'
                        AND c.effective_from <= CURRENT_TIMESTAMP
                        AND (c.effective_to IS NULL OR c.effective_to > CURRENT_TIMESTAMP)
                  )
                ORDER BY j.available_at, j.sci_scoring_job_pk
                LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            UPDATE nlp.sci_scoring_job j
               SET status='RUNNING', locked_until=CURRENT_TIMESTAMP + interval '2 minutes',
                   attempt_count=attempt_count + 1, error_code=NULL, updated_at=CURRENT_TIMESTAMP
              FROM next_job n
             WHERE j.sci_scoring_job_pk=n.sci_scoring_job_pk
            RETURNING j.job_id, j.owner_member_id, j.venture_pk, j.after_link_pk, j.processed_count, j.attempt_count
            """, connection, transaction);
        SciJob? result = null;
        await using (var reader = await command.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) result = new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt32(4), reader.GetInt32(5));
        await transaction.CommitAsync(ct);
        return result;
    }

    private async Task LogBlockedQueueAsync(CancellationToken ct)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        if (checkedAt < _nextConfigurationHealthCheckAt) return;
        _nextConfigurationHealthCheckAt = checkedAt.Add(ConfigurationHealthCheckInterval);

        await using var command = dataSource.CreateCommand("""
            SELECT EXISTS (
                       SELECT 1 FROM nlp.sci_scoring_job
                       WHERE status IN ('PENDING','FAILED','RUNNING') AND available_at <= CURRENT_TIMESTAMP
                         AND (locked_until IS NULL OR locked_until < CURRENT_TIMESTAMP)),
                   EXISTS (
                       SELECT 1 FROM nlp.sci_scoring_config
                       WHERE scoring_config_id='SCI_GLOBAL_V2' AND status='ACTIVE'
                         AND effective_from <= CURRENT_TIMESTAMP
                         AND (effective_to IS NULL OR effective_to > CURRENT_TIMESTAMP))
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct) && reader.GetBoolean(0) && !reader.GetBoolean(1))
            logger.LogError("SCI scoring queue is blocked: approved SCI_GLOBAL_V2 is not active");
    }

    private async Task ProcessAsync(SciJob job, CancellationToken ct)
    {
        try
        {
            var config = await ReadConfigurationAsync(ct);
            var candidates = await ReadCandidatesAsync(job, ct);
            foreach (var candidate in candidates.Where(x => x.ForwardDistance is not null))
            {
                var forward = NormalizeSimilarity(candidate.ForwardDistance!.Value, config.CosineFloor, config.CosineSpan);
                var reverse = candidate.ReverseDistance is null ? (double?)null : NormalizeSimilarity(candidate.ReverseDistance.Value, config.CosineFloor, config.CosineSpan);
                var semantic = reverse is null ? forward : Math.Max(forward, reverse.Value);
                var complementarity = reverse is null ? forward : (forward + reverse.Value) / 2d;
                var recency = Recency(candidate, config, DateTimeOffset.UtcNow);
                await ApplyAsync(job, candidate, config, semantic, complementarity, recency, ct);
            }

            var more = candidates.Count == BatchSize;
            var nextPk = candidates.Count == 0 ? job.AfterLinkPk : candidates[^1].ContactLinkPk;
            if (more)
                await UpdateJobAsync(job, "PENDING", nextPk, job.ProcessedCount + candidates.Count, null, DateTimeOffset.UtcNow, ct);
            else
                await CompleteJobAsync(job, nextPk, job.ProcessedCount + candidates.Count, config, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var dead = job.AttemptCount >= MaxAttempts;
            await UpdateJobAsync(job, dead ? "DEAD" : "FAILED", job.AfterLinkPk, job.ProcessedCount, ErrorCode(exception),
                dead ? null : DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 10 * Math.Pow(2, job.AttemptCount - 1))), ct);
            logger.LogWarning(exception, "SCI v2 job {JobId} failed on attempt {AttemptCount}", job.JobId, job.AttemptCount);
        }
    }

    private async Task<SciConfiguration> ReadConfigurationAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT scoring_config_id, scorer_version, cosine_floor, cosine_span,
                   recency_full_days, recency_decay_days, estimated_recency
            FROM nlp.sci_scoring_config
            WHERE scoring_config_id='SCI_GLOBAL_V2' AND status='ACTIVE'
              AND effective_from <= CURRENT_TIMESTAMP AND (effective_to IS NULL OR effective_to > CURRENT_TIMESTAMP)
            LIMIT 1
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("SCI_V2_ACTIVE_CONFIGURATION_MISSING");
        return new(reader.GetString(0), reader.GetString(1), Double(reader, 2), Double(reader, 3),
            reader.GetInt16(4), reader.GetInt16(5), Double(reader, 6));
    }

    private async Task<List<SciCandidate>> ReadCandidatesAsync(SciJob job, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT * FROM nlp.get_sci_v2_scoring_candidates(@owner_member_id,@after_link_pk,@max_rows)");
        Add(command, "owner_member_id", job.OwnerMemberId);
        command.Parameters.AddWithValue("after_link_pk", job.AfterLinkPk);
        command.Parameters.AddWithValue("max_rows", BatchSize);
        var rows = new List<SciCandidate>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new(
                reader.GetInt64(0), reader.GetString(1), NullableString(reader, 2), reader.GetString(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetDouble(9), reader.IsDBNull(10) ? null : reader.GetDouble(10),
                reader.GetBoolean(11), reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                reader.GetString(13), reader.GetString(14)));
        return rows;
    }

    private async Task ApplyAsync(SciJob job, SciCandidate candidate, SciConfiguration config, double semantic, double complementarity, double recency, CancellationToken ct)
    {
        var stableKey = $"{job.JobId}:{candidate.ContactLinkPk}";
        var explanation = JsonSerializer.Serialize(new
        {
            algorithm = "SCI_V2",
            lifecycle = candidate.LifecycleState,
            semantic_component = semantic,
            complementarity_component = complementarity,
            recency_component = recency,
            estimated = candidate.IsEstimated
        });
        await using var command = dataSource.CreateCommand("""
            SELECT * FROM nlp.apply_sci_link_score_v2(
              @score_run_id,@contact_link_pk,@venture_pk,@owner_intent_set_pk,@contact_intent_set_pk,
              @scoring_config_id,@semantic_component,@complementarity_component,@recency_component,
              @is_estimated,@explanation_json,@model_version,@preprocessing_version,@scorer_version,
              @cause_event_id,@scored_at,true)
            """);
        Add(command, "score_run_id", StableId("run", stableKey));
        command.Parameters.AddWithValue("contact_link_pk", candidate.ContactLinkPk);
        command.Parameters.AddWithValue("venture_pk", candidate.OwnerVenturePk);
        command.Parameters.AddWithValue("owner_intent_set_pk", candidate.OwnerIntentSetPk);
        command.Parameters.Add("contact_intent_set_pk", NpgsqlDbType.Bigint).Value = candidate.ContactIntentSetPk is null ? DBNull.Value : candidate.ContactIntentSetPk.Value;
        Add(command, "scoring_config_id", config.Id);
        command.Parameters.Add("semantic_component", NpgsqlDbType.Numeric).Value = (decimal)semantic;
        command.Parameters.Add("complementarity_component", NpgsqlDbType.Numeric).Value = (decimal)complementarity;
        command.Parameters.Add("recency_component", NpgsqlDbType.Numeric).Value = (decimal)recency;
        command.Parameters.AddWithValue("is_estimated", candidate.IsEstimated);
        command.Parameters.Add("explanation_json", NpgsqlDbType.Jsonb).Value = explanation;
        Add(command, "model_version", candidate.ModelVersion);
        Add(command, "preprocessing_version", candidate.PreprocessingVersion);
        Add(command, "scorer_version", config.ScorerVersion);
        Add(command, "cause_event_id", job.JobId);
        command.Parameters.AddWithValue("scored_at", DateTimeOffset.UtcNow);
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
        Add(command, "status", status);
        command.Parameters.AddWithValue("after_link_pk", afterPk);
        command.Parameters.AddWithValue("processed_count", processed);
        Add(command, "error_code", error);
        command.Parameters.Add("available_at", NpgsqlDbType.TimestampTz).Value = availableAt is null ? DBNull.Value : availableAt.Value;
        Add(command, "job_id", job.JobId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task CompleteJobAsync(SciJob job,long afterPk,int processed,SciConfiguration config,CancellationToken ct)
    {
        await using var command=dataSource.CreateCommand("""
            WITH completed AS (
              UPDATE nlp.sci_scoring_job SET status='SUCCEEDED',after_link_pk=@after_link_pk,
                processed_count=@processed_count,error_code=NULL,locked_until=NULL,updated_at=CURRENT_TIMESTAMP
              WHERE job_id=@job_id
              RETURNING job_id,owner_member_id,venture_pk,processed_count
            )
            INSERT INTO ops.outbox_event(outbox_event_id,aggregate_type,aggregate_id,event_type,payload_json)
            SELECT 'sci_done_'||substr(md5(job_id),1,32),'SCI_MEMBER',owner_member_id,'SciScoringCompleted.v1',
              jsonb_build_object('job_id',job_id,'owner_member_id',owner_member_id,'venture_pk',venture_pk,
                'processed_count',processed_count,'scorer_version',@scorer_version,'scoring_config_id',@scoring_config_id)
            FROM completed ON CONFLICT(outbox_event_id) DO NOTHING
            """);
        command.Parameters.AddWithValue("after_link_pk",afterPk);command.Parameters.AddWithValue("processed_count",processed);
        Add(command,"job_id",job.JobId);Add(command,"scorer_version",config.ScorerVersion);Add(command,"scoring_config_id",config.Id);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static double NormalizeSimilarity(double cosineDistance, double floor, double span) =>
        Math.Clamp(((1d - cosineDistance) - floor) / span, 0d, 1d);

    private static double Recency(SciCandidate candidate, SciConfiguration config, DateTimeOffset now)
    {
        if (candidate.IsEstimated) return config.EstimatedRecency;
        if (candidate.ContactIntentUpdatedAt is null) return 0d;
        var age = Math.Max(0d, (now - candidate.ContactIntentUpdatedAt.Value).TotalDays);
        if (age <= config.RecencyFullDays) return 1d;
        if (age >= config.RecencyDecayDays) return 0d;
        return 1d - ((age - config.RecencyFullDays) / (config.RecencyDecayDays - config.RecencyFullDays));
    }

    private static string StableId(string prefix, string value) =>
        $"{prefix}_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32]}";

    private static string ErrorCode(Exception exception)
    {
        var value = (exception is PostgresException postgres ? $"PG_{postgres.SqlState}" : exception.Message).Replace(' ', '_');
        return string.IsNullOrWhiteSpace(value) ? "SCI_SCORING_FAILED" : value[..Math.Min(64, value.Length)];
    }

    private static double Double(NpgsqlDataReader reader, int ordinal) => Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    private static string? NullableString(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static void Add(NpgsqlCommand command, string name, string? value) => command.Parameters.Add(name, NpgsqlDbType.Varchar).Value = value is null ? DBNull.Value : value;

    private sealed record SciJob(string JobId, string OwnerMemberId, long VenturePk, long AfterLinkPk, int ProcessedCount, int AttemptCount);
    private sealed record SciConfiguration(string Id, string ScorerVersion, double CosineFloor, double CosineSpan, short RecencyFullDays, short RecencyDecayDays, double EstimatedRecency);
    private sealed record SciCandidate(
        long ContactLinkPk, string LinkId, string? ContactMemberId, string LifecycleState,
        long OwnerVenturePk, long OwnerIntentSetPk, long? ContactIntentSetPk,
        string OwnerStructuredIntentJson, string? ContactStructuredIntentJson,
        double? ForwardDistance, double? ReverseDistance, bool IsEstimated,
        DateTimeOffset? ContactIntentUpdatedAt, string ModelVersion, string PreprocessingVersion);
}
