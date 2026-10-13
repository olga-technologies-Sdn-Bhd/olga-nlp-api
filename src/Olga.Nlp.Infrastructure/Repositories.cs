using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Olga.Nlp.Application;
using Olga.Nlp.Domain;
using Pgvector;

namespace Olga.Nlp.Infrastructure;

public static class EmbeddingBinary
{
    public static Vector Serialize(float[] values) => new(values);

    public static float[] Deserialize(Vector vector) => vector.ToArray();
}

public sealed class IntentRepository(NlpDbContext db) : IIntentRepository
{
    public async Task UpsertProcessingAsync(Intent intent, string? expectedETag, CancellationToken ct)
    {
        var row = await db.Intents.SingleOrDefaultAsync(x => x.IntentId == intent.IntentId, ct);
        if (row is null)
        {
            row = new NlpIntentRow { IntentId = intent.IntentId, CreatedAt = intent.CreatedAt };
            db.Intents.Add(row);
        }
        else
        {
            if (row.MemberId != intent.MemberId) throw new DomainConflictException("INTENT_OWNER_MISMATCH");
            if (string.IsNullOrWhiteSpace(expectedETag)) throw new DomainConflictException("IF_MATCH_REQUIRED");
            if (!ETagMatches(row, expectedETag)) throw new DomainConflictException("RESOURCE_VERSION_CONFLICT");
        }

        row.MemberId = intent.MemberId;
        row.ContextId = intent.ContextId;
        row.IntentType = intent.Type.ToString().ToUpperInvariant();
        row.OriginalText = intent.OriginalText;
        row.NormalizedText = intent.NormalizedText;
        row.NormalizedHash = intent.NormalizedHash;
        row.LanguageCode = intent.LanguageCode;
        row.ContainsPii = intent.ContainsPii;
        row.Status = "PROCESSING";
        row.ExpiresAt = intent.ExpiresAt;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.PreprocessingVersion = intent.PreprocessingVersion;
        row.Category = intent.Category;
        row.Industry = intent.Industry;
        row.Geography = intent.Geography;

        var existingEmbeddings = await db.Embeddings.Where(x => x.IntentId == intent.IntentId && x.Status == "ACTIVE").ToListAsync(ct);
        foreach (var embedding in existingEmbeddings) embedding.Status = "SUPERSEDED";
        AddOutboxEvent(db, "INTENT", intent.IntentId, "NlpIntentNormalized.v1", new { intent_id = intent.IntentId, member_id = intent.MemberId, context_id = intent.ContextId, intent_type = row.IntentType, status = row.Status, preprocessing_version = row.PreprocessingVersion });
        await db.SaveChangesAsync(ct);
    }

    public async Task<Intent?> GetAsync(string memberId, string intentId, CancellationToken ct)
    {
        var row = await db.Intents.AsNoTracking().SingleOrDefaultAsync(x => x.MemberId == memberId && x.IntentId == intentId, ct);
        return row is null ? null : await MapAsync(row, null, ct);
    }

    public async Task<Intent> MarkReadyAsync(string intentId, string normalizedText, string normalizedHash, float[] embedding, string modelVersion, string preprocessingVersion, CancellationToken ct)
    {
        if (db.Database.IsRelational() && embedding.Length != 1536) throw new ArgumentException("EMBEDDING_DIMENSION_MISMATCH");
        var row = await db.Intents.SingleAsync(x => x.IntentId == intentId, ct);
        if (!string.Equals(row.NormalizedHash, normalizedHash, StringComparison.Ordinal)) throw new DomainConflictException("INTENT_CHANGED_DURING_EMBEDDING");

        row.NormalizedText = normalizedText;
        row.Status = "MATCH_READY";
        row.PreprocessingVersion = preprocessingVersion;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        var stored = await db.Embeddings.SingleOrDefaultAsync(x => x.IntentId == intentId && x.ModelVersion == modelVersion, ct);
        if (stored is null)
        {
            stored = new NlpEmbeddingRow { IntentId = intentId, ModelVersion = modelVersion };
            db.Embeddings.Add(stored);
        }
        stored.Dimensions = embedding.Length;
        stored.NormalizedHash = normalizedHash;
        stored.Embedding = EmbeddingBinary.Serialize(embedding);
        stored.Status = "ACTIVE";
        stored.CreatedAt = DateTimeOffset.UtcNow;
        AddOutboxEvent(db, "INTENT", intentId, "NlpIntentMatchReady.v1", new { intent_id = intentId, member_id = row.MemberId, context_id = row.ContextId, intent_type = row.IntentType, status = row.Status, model_version = modelVersion, preprocessing_version = preprocessingVersion });
        await db.SaveChangesAsync(ct);
        return await MapAsync(row, stored, ct) ?? throw new InvalidOperationException("INTENT_MAPPING_FAILED");
    }

    private async Task<Intent?> MapAsync(NlpIntentRow row, NlpEmbeddingRow? knownEmbedding, CancellationToken ct)
    {
        var embedding = knownEmbedding ?? await db.Embeddings.AsNoTracking()
            .Where(x => x.IntentId == row.IntentId && x.Status == "ACTIVE")
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return Map(row, embedding);
    }

    internal static Intent Map(NlpIntentRow row, NlpEmbeddingRow? embedding) => new(
        row.IntentId, row.MemberId, row.ContextId, Enum.Parse<IntentType>(row.IntentType, true),
        row.OriginalText, row.NormalizedText, row.ExpiresAt, ParseStatus(row.Status),
        embedding is null ? null : EmbeddingBinary.Deserialize(embedding.Embedding), embedding?.ModelVersion,
        row.PreprocessingVersion, row.Category, row.Industry, row.Geography, row.NormalizedHash,
        row.LanguageCode, row.ContainsPii, row.CreatedAt, row.UpdatedAt, CreateETag(row));

    private static IntentStatus ParseStatus(string status) => status switch
    {
        "MATCH_READY" => IntentStatus.MatchReady,
        "FAILED" => IntentStatus.Failed,
        "INACTIVE" => IntentStatus.Inactive,
        _ => IntentStatus.Processing
    };

    private static string CreateETag(NlpIntentRow row) => row.RowVersion > 0
        ? $"\"{row.RowVersion:x}\""
        : $"\"{row.UpdatedAt.UtcTicks:x}\"";

    private static bool ETagMatches(NlpIntentRow row, string expected) =>
        string.Equals(CreateETag(row), expected.Trim(), StringComparison.Ordinal);

    internal static void AddOutboxEvent(NlpDbContext context, string aggregateType, string aggregateId, string eventType, object payload)
    {
        context.OutboxEvents.Add(new OutboxEventRow
        {
            OutboxEventId = Guid.NewGuid().ToString("N"), AggregateType = aggregateType,
            AggregateId = aggregateId, EventType = eventType,
            PayloadJson = JsonSerializer.Serialize(payload), OccurredAt = DateTimeOffset.UtcNow
        });
    }
}

public sealed class InlineIntentProcessingDispatcher(IEmbeddingProvider embeddings, IIntentRepository intents) : IIntentProcessingDispatcher
{
    public string? ModelVersion => embeddings.ModelVersion;

    public async Task<Intent?> DispatchAsync(Intent intent, CancellationToken ct)
    {
        var vector = await embeddings.EmbedAsync(intent.NormalizedText, ct);
        return await intents.MarkReadyAsync(intent.IntentId, intent.NormalizedText, intent.NormalizedHash, vector, embeddings.ModelVersion, intent.PreprocessingVersion, ct);
    }
}

public sealed class QueuedIntentProcessingDispatcher(NlpDbContext db) : IIntentProcessingDispatcher
{
    public string? ModelVersion => null;

    public async Task<Intent?> DispatchAsync(Intent intent, CancellationToken ct)
    {
        var active = await db.ProcessingJobs.AnyAsync(x => x.IntentId == intent.IntentId && (x.Status == "PENDING" || x.Status == "RUNNING"), ct);
        if (!active)
        {
            var now = DateTimeOffset.UtcNow;
            db.ProcessingJobs.Add(new NlpProcessingJobRow
            {
                JobId = Guid.NewGuid().ToString("N"), IntentId = intent.IntentId, JobType = "EMBED",
                Status = "PENDING", AvailableAt = now, CreatedAt = now, UpdatedAt = now
            });
            await db.SaveChangesAsync(ct);
        }
        return null;
    }
}

public sealed class CandidateRepository(NlpDbContext db) : ICandidateRepository
{
    public async Task<MemberIntents?> GetRequesterIntentsAsync(string memberId, string requestedIntentId, string contextId, CancellationToken ct)
    {
        if (db.Database.IsRelational()) return await GetRelationalRequesterAsync(memberId, requestedIntentId, contextId, ct);
        var rows = await ReadyRows(memberId, contextId, ct);
        var requestedWant = rows.FirstOrDefault(x => x.IntentId == requestedIntentId && x.IntentType == "WANT");
        if (requestedWant is null) return null;
        var mapped = await MapMemberAsync(memberId, rows, requestedWant.IntentId, null, ct);
        if (mapped?.Want is null) return null;
        return mapped.Offer?.ModelVersion == mapped.Want.ModelVersion ? mapped : mapped with { Offer = null };
    }

    public async Task<IReadOnlyList<Candidate>> GetEligibleCandidatesAsync(string requesterId, string contextId, string modelVersion, int maxRows, CancellationToken ct)
    {
        maxRows = Math.Clamp(maxRows, 50, 200);
        if (db.Database.IsRelational()) return await GetRelationalCandidatesAsync(requesterId, contextId, modelVersion, maxRows, ct);
        var requesterRows = await ReadyRows(requesterId, contextId, ct);
        var requester = await MapMemberAsync(requesterId, requesterRows, null, modelVersion, ct);
        if (requester?.Want is null) return Array.Empty<Candidate>();

        var eligibleIds = db.Database.IsRelational()
            ? await db.EligibilityProjection.AsNoTracking().Where(x => x.ContextId == contextId && x.MemberId != requesterId && x.IsLive && x.IsVisible && x.HasConsent && !x.IsSuspended && !x.IsDeleted).Select(x => x.MemberId).Take(maxRows * 2).ToListAsync(ct)
            : await db.MemberEligibility.AsNoTracking().Where(x => x.ContextId == contextId && x.MemberId != requesterId && x.IsLive && x.IsVisible && x.HasConsent && !x.IsSuspended && !x.IsDeleted).Select(x => x.MemberId).Take(maxRows * 2).ToListAsync(ct);

        var excludedIds = db.Database.IsRelational()
            ? await ExcludedFromProjection(requesterId, contextId, eligibleIds, ct)
            : await db.MemberRelationships.AsNoTracking().Where(x => x.ContextId == contextId && ((x.MemberId == requesterId && eligibleIds.Contains(x.OtherMemberId)) || (x.OtherMemberId == requesterId && eligibleIds.Contains(x.MemberId))) && (x.IsBlocked || x.IsConnected)).Select(x => x.MemberId == requesterId ? x.OtherMemberId : x.MemberId).ToListAsync(ct);
        eligibleIds = eligibleIds.Except(excludedIds, StringComparer.Ordinal).ToList();

        var now = DateTimeOffset.UtcNow;
        var suppressedMembers = await db.MatchSuppressions.AsNoTracking()
            .Where(x => x.MemberId != null && eligibleIds.Contains(x.MemberId) && (x.ContextId == null || x.ContextId == contextId) && x.StartsAt <= now && (x.EndsAt == null || x.EndsAt > now))
            .Select(x => x.MemberId!).ToListAsync(ct);
        eligibleIds = eligibleIds.Except(suppressedMembers, StringComparer.Ordinal).ToList();

        var rows = await db.Intents.AsNoTracking()
            .Where(x => eligibleIds.Contains(x.MemberId) && x.ContextId == contextId && x.Status == "MATCH_READY" && x.ExpiresAt > now)
            .OrderByDescending(x => x.UpdatedAt).Take(maxRows * 4).ToListAsync(ct);

        var suppressedIntentIds = await db.MatchSuppressions.AsNoTracking()
            .Where(x => x.IntentId != null && (x.ContextId == null || x.ContextId == contextId) && x.StartsAt <= now && (x.EndsAt == null || x.EndsAt > now))
            .Select(x => x.IntentId!).ToListAsync(ct);

        var candidates = new List<Candidate>();
        foreach (var group in rows.GroupBy(x => x.MemberId))
        {
            var member = await MapMemberAsync(group.Key, group.ToList(), null, modelVersion, ct);
            if (member?.Offer is null || suppressedIntentIds.Contains(member.Offer.IntentId)) continue;
            candidates.Add(new Candidate(
                group.Key, member.Offer, member.Want,
                Compatible(requester.Want.Category, member.Offer.Category),
                Compatible(requester.Want.Industry, member.Offer.Industry),
                Compatible(requester.Want.Geography, member.Offer.Geography),
                Freshness(group.Max(x => x.UpdatedAt))));
            if (candidates.Count >= maxRows) break;
        }
        return candidates;
    }

    private async Task<MemberIntents?> GetRelationalRequesterAsync(string memberId, string requestedIntentId, string contextId, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand("SELECT * FROM nlp.get_requester_intent(@p_member_id, @p_intent_id, @p_context_id)", connection);
            AddVarchar(command, "p_member_id", memberId);
            AddVarchar(command, "p_intent_id", requestedIntentId);
            AddVarchar(command, "p_context_id", contextId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || !string.Equals(reader.GetString(3), "WANT", StringComparison.Ordinal)) return null;
            var projectedWant = MapFunctionIntent(reader);
            await reader.CloseAsync();

            var wantRow = await db.Intents.AsNoTracking().SingleAsync(x => x.MemberId == memberId && x.IntentId == requestedIntentId && x.ContextId == contextId, ct);
            var latestWantId = await db.Intents.AsNoTracking()
                .Where(x => x.MemberId == memberId && x.ContextId == contextId && x.IntentType == "WANT" && x.Status == "MATCH_READY" && x.ExpiresAt > DateTimeOffset.UtcNow)
                .OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.IntentId).Select(x => x.IntentId).FirstAsync(ct);
            // get_eligible_candidates uses the latest requester WANT; fail closed instead of
            // ranking a pool selected with a different intent embedding.
            if (!string.Equals(latestWantId, requestedIntentId, StringComparison.Ordinal)) return null;
            var wantEmbedding = await db.Embeddings.AsNoTracking().SingleAsync(x => x.IntentId == requestedIntentId && x.ModelVersion == projectedWant.ModelVersion && x.Status == "ACTIVE" && x.NormalizedHash == wantRow.NormalizedHash, ct);
            var want = IntentRepository.Map(wantRow, wantEmbedding);

            var offerRow = await db.Intents.AsNoTracking()
                .Where(x => x.MemberId == memberId && x.ContextId == contextId && x.IntentType == "OFFER" && x.Status == "MATCH_READY" && x.ExpiresAt > DateTimeOffset.UtcNow)
                .OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync(ct);
            Intent? offer = null;
            if (offerRow is not null)
            {
                var embedding = await db.Embeddings.AsNoTracking().Where(x => x.IntentId == offerRow.IntentId && x.ModelVersion == want.ModelVersion && x.Status == "ACTIVE" && x.NormalizedHash == offerRow.NormalizedHash).FirstOrDefaultAsync(ct);
                if (embedding is not null) offer = IntentRepository.Map(offerRow, embedding);
            }
            return new MemberIntents(memberId, offer, want);
        }
        finally { if (shouldClose) await connection.CloseAsync(); }
    }

    private async Task<IReadOnlyList<Candidate>> GetRelationalCandidatesAsync(string requesterId, string contextId, string modelVersion, int maxRows, CancellationToken ct)
    {
        var offers = new List<Intent>();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand("SELECT * FROM nlp.get_eligible_candidates(@p_requester_id, @p_context_id, @p_max_rows)", connection);
            AddVarchar(command, "p_requester_id", requesterId);
            AddVarchar(command, "p_context_id", contextId);
            command.Parameters.Add(new NpgsqlParameter("p_max_rows", NpgsqlDbType.Integer) { Value = maxRows });
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var offer = MapFunctionIntent(reader);
                if (string.Equals(offer.ModelVersion, modelVersion, StringComparison.Ordinal)) offers.Add(offer);
            }
        }
        finally { if (shouldClose) await connection.CloseAsync(); }

        if (offers.Count == 0) return [];
        var requesterWant = await db.Intents.AsNoTracking()
            .Where(x => x.MemberId == requesterId && x.ContextId == contextId && x.IntentType == "WANT" && x.Status == "MATCH_READY" && x.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(x => x.UpdatedAt).FirstAsync(ct);
        var memberIds = offers.Select(x => x.MemberId).Distinct().ToArray();
        var wantRows = await db.Intents.AsNoTracking()
            .Where(x => memberIds.Contains(x.MemberId) && x.ContextId == contextId && x.IntentType == "WANT" && x.Status == "MATCH_READY" && x.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(x => x.UpdatedAt).ToListAsync(ct);
        var wantIds = wantRows.Select(x => x.IntentId).ToArray();
        var wantEmbeddings = await db.Embeddings.AsNoTracking().Where(x => wantIds.Contains(x.IntentId) && x.ModelVersion == modelVersion && x.Status == "ACTIVE").ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return offers.Select(offer =>
        {
            var wantRow = wantRows.FirstOrDefault(x => x.MemberId == offer.MemberId);
            var wantEmbedding = wantRow is null ? null : wantEmbeddings.FirstOrDefault(x => x.IntentId == wantRow.IntentId && x.NormalizedHash == wantRow.NormalizedHash);
            var want = wantRow is not null && wantEmbedding is not null ? IntentRepository.Map(wantRow, wantEmbedding) : null;
            return new Candidate(
                offer.MemberId, offer, want,
                Compatible(requesterWant.Category, offer.Category),
                Compatible(requesterWant.Industry, offer.Industry),
                Compatible(requesterWant.Geography, offer.Geography),
                Math.Clamp(1 - (now - offer.UpdatedAt).TotalDays / 90d, 0, 1));
        }).ToArray();
    }

    private static Intent MapFunctionIntent(DbDataReader reader)
    {
        var updatedAt = reader.GetFieldValue<DateTimeOffset>(8);
        return new Intent(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), Enum.Parse<IntentType>(reader.GetString(3), true),
            reader.GetString(4), reader.GetString(4), DateTimeOffset.MaxValue, IntentStatus.MatchReady,
            reader.GetFieldValue<Vector>(12).ToArray(), reader.GetString(9), "normalizer-v1",
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(11).Trim(), "en", false,
            updatedAt, updatedAt, string.Empty);
    }

    private static void AddVarchar(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Varchar) { Size = 64, Value = value });

    private async Task<List<string>> ExcludedFromProjection(string requesterId, string contextId, List<string> eligibleIds, CancellationToken ct) =>
        await db.RelationshipProjection.AsNoTracking()
            .Where(x => x.ContextId == contextId && ((x.MemberId == requesterId && eligibleIds.Contains(x.OtherMemberId)) || (x.OtherMemberId == requesterId && eligibleIds.Contains(x.MemberId))) && (x.IsBlocked || x.IsConnected))
            .Select(x => x.MemberId == requesterId ? x.OtherMemberId : x.MemberId).ToListAsync(ct);

    private async Task<List<NlpIntentRow>> ReadyRows(string memberId, string contextId, CancellationToken ct) =>
        await db.Intents.AsNoTracking().Where(x => x.MemberId == memberId && x.ContextId == contextId && x.Status == "MATCH_READY" && x.ExpiresAt > DateTimeOffset.UtcNow).ToListAsync(ct);

    private async Task<MemberIntents?> MapMemberAsync(string memberId, IReadOnlyCollection<NlpIntentRow> rows, string? requestedWantIntentId, string? requiredModelVersion, CancellationToken ct)
    {
        if (rows.Count == 0) return null;
        var ids = rows.Select(x => x.IntentId).ToArray();
        var embeddings = await db.Embeddings.AsNoTracking()
            .Where(x => ids.Contains(x.IntentId) && x.Status == "ACTIVE" && (requiredModelVersion == null || x.ModelVersion == requiredModelVersion))
            .OrderByDescending(x => x.CreatedAt).ToListAsync(ct);

        Intent? Map(string type)
        {
            var matchingRows = rows.Where(x => x.IntentType == type);
            if (type == "WANT" && requestedWantIntentId is not null) matchingRows = matchingRows.Where(x => x.IntentId == requestedWantIntentId);
            var row = matchingRows.OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
            if (row is null) return null;
            var embedding = embeddings.FirstOrDefault(x => x.IntentId == row.IntentId);
            return embedding is null ? null : IntentRepository.Map(row, embedding);
        }

        return new MemberIntents(memberId, Map("OFFER"), Map("WANT"));
    }

    private static double Compatible(string? left, string? right) => !string.IsNullOrWhiteSpace(left) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    private static double Freshness(DateTimeOffset updatedAt) => Math.Clamp(1 - (DateTimeOffset.UtcNow - updatedAt).TotalDays / 90d, 0, 1);
}

public sealed class RankingConfigRepository(NlpDbContext db) : IRankingConfigRepository
{
    public async Task<RankingConfig> GetActiveAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var row = await db.RankingConfigs.AsNoTracking().Where(x => x.ActiveFrom <= now && (x.ActiveTo == null || x.ActiveTo > now)).OrderByDescending(x => x.ActiveFrom).FirstOrDefaultAsync(ct);
        return row is null
            ? new RankingConfig("ranking-v1")
            : new RankingConfig(row.RankingVersion, (double)row.SemanticWeight, (double)row.CategoryWeight, (double)row.IndustryWeight, (double)row.GeographyWeight, (double)row.FreshnessWeight, (double)row.Threshold, (double)row.EventWeight, row.ConfigJson);
    }
}

public sealed class MatchRequestRepository(NlpDbContext db) : IMatchRequestRepository
{
    public async Task<MatchStartResult> TryStartAsync(MatchExecution execution, CancellationToken ct)
    {
        var existing = await db.MatchRequests.SingleOrDefaultAsync(x => x.RequestId == execution.RequestId, ct);
        if (existing is not null)
        {
            var mapped = Map(existing);
            if (!string.Equals(existing.RequestHash, execution.RequestHash, StringComparison.Ordinal) || existing.RequesterId != execution.RequesterId)
                return new MatchStartResult(MatchStartDisposition.Conflict, mapped);
            return new MatchStartResult(existing.Status == "PROCESSING" ? MatchStartDisposition.InProgress : MatchStartDisposition.Replay, mapped);
        }

        var now = DateTimeOffset.UtcNow;
        db.MatchRequests.Add(new NlpMatchRequestRow
        {
            RequestId = execution.RequestId, RequestHash = execution.RequestHash, RequesterId = execution.RequesterId,
            IntentId = execution.IntentId, ContextId = execution.ContextId, LanguageCode = execution.LanguageCode,
            RequestedLimit = checked((short)execution.RequestedLimit), RequestOptionsJson = execution.RequestOptionsJson,
            Status = "PROCESSING", CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync(ct);
        return new MatchStartResult(MatchStartDisposition.Started, execution with { CreatedAt = now });
    }

    public async Task<MatchExecution?> GetAsync(string requestId, string requesterId, CancellationToken ct)
    {
        var row = await db.MatchRequests.AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == requestId && x.RequesterId == requesterId, ct);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<RankedMatch>> GetResultsAsync(string requestId, CancellationToken ct)
    {
        var rows = await db.MatchResults.AsNoTracking().Where(x => x.RequestId == requestId && x.PolicyStatus == "ELIGIBLE").OrderBy(x => x.Rank).ToListAsync(ct);
        return rows.Select(MapResult).ToArray();
    }

    public async Task CompleteAsync(string requestId, string preprocessingVersion, string modelVersion, string rankingVersion, double threshold, int candidateCount, CancellationToken ct)
    {
        var row = await db.MatchRequests.SingleAsync(x => x.RequestId == requestId, ct);
        // save_match_results updates this row directly and advances its trigger-managed row_version.
        if (db.Database.IsRelational())
            await db.Entry(row).ReloadAsync(ct);
        row.Status = "COMPLETED";
        row.PreprocessingVersion = preprocessingVersion;
        row.ModelVersion = modelVersion;
        row.RankingVersion = rankingVersion;
        row.RankingThreshold = (decimal)threshold;
        row.CandidateCount = candidateCount;
        row.CompletedAt = DateTimeOffset.UtcNow;
        row.UpdatedAt = row.CompletedAt.Value;
        row.ErrorCode = null;
        IntentRepository.AddOutboxEvent(db, "MATCH_REQUEST", requestId, "NlpMatchRequestCompleted.v1", new { request_id = requestId, candidate_count = candidateCount, model_version = modelVersion, preprocessing_version = preprocessingVersion, ranking_version = rankingVersion });
        await db.SaveChangesAsync(ct);
    }

    public async Task FailAsync(string requestId, string errorCode, CancellationToken ct)
    {
        var row = await db.MatchRequests.SingleOrDefaultAsync(x => x.RequestId == requestId, ct);
        if (row is null || row.Status != "PROCESSING") return;
        row.Status = "FAILED";
        row.ErrorCode = errorCode[..Math.Min(errorCode.Length, 64)];
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    internal static RankedMatch MapResult(NlpMatchResultRow row) => new(
        row.CandidateId, (double)row.FinalScore, row.Label, DeserializeCodes(row.ReasonCodes), row.ReasonText,
        (double)row.SemanticScore, (double?)row.ReciprocalScore, row.Rank, row.MatchResultId);

    private static MatchExecution Map(NlpMatchRequestRow row) => new(
        row.RequestId, row.RequestHash, row.RequesterId, row.IntentId, row.ContextId, row.LanguageCode,
        row.RequestedLimit, row.RequestOptionsJson, ParseStatus(row.Status), row.PreprocessingVersion,
        row.ModelVersion, row.RankingVersion, (double?)row.RankingThreshold, row.CandidateCount,
        row.CreatedAt, row.CompletedAt, row.ErrorCode);

    private static MatchExecutionStatus ParseStatus(string status) => status switch
    {
        "COMPLETED" => MatchExecutionStatus.Completed,
        "FAILED" => MatchExecutionStatus.Failed,
        _ => MatchExecutionStatus.Processing
    };

    private static IReadOnlyList<string> DeserializeCodes(string value)
    {
        try { return JsonSerializer.Deserialize<string[]>(value) ?? []; }
        catch (JsonException) { return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); }
    }
}

public sealed class MatchResultRepository(NlpDbContext db) : IMatchResultRepository
{
    public async Task<IReadOnlyList<RankedMatch>> SaveAsync(string requestId, string requesterId, IReadOnlyList<RankedMatch> matches, string modelVersion, string preprocessingVersion, string rankingVersion, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            var payload = JsonSerializer.Serialize(matches.Select((match, index) => new
            {
                candidate_id = match.MemberId,
                rank = index + 1,
                semantic_score = match.SemanticScore,
                reciprocal_score = match.ReciprocalScore,
                final_score = match.Score,
                label = match.Label.ToUpperInvariant(),
                reason_codes = match.ReasonCodes,
                reason_text = match.ReasonText,
                model_version = modelVersion,
                preprocessing_version = preprocessingVersion,
                ranking_version = rankingVersion,
                policy_status = "ELIGIBLE"
            }));
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var shouldClose = connection.State != ConnectionState.Open;
            if (shouldClose) await connection.OpenAsync(ct);
            try
            {
                await using var command = new NpgsqlCommand("SELECT nlp.save_match_results(@p_request_id, @p_requester_id, @p_results)", connection);
                command.Parameters.Add(new NpgsqlParameter("p_request_id", NpgsqlDbType.Varchar) { Size = 64, Value = requestId });
                command.Parameters.Add(new NpgsqlParameter("p_requester_id", NpgsqlDbType.Varchar) { Size = 64, Value = requesterId });
                command.Parameters.Add(new NpgsqlParameter("p_results", NpgsqlDbType.Jsonb) { Value = payload });
                await command.ExecuteNonQueryAsync(ct);
            }
            finally { if (shouldClose) await connection.CloseAsync(); }
            var persisted = await db.MatchResults.AsNoTracking().Where(x => x.RequestId == requestId).OrderBy(x => x.Rank).ToListAsync(ct);
            return persisted.Select(MatchRequestRepository.MapResult).ToArray();
        }

        async Task<IReadOnlyList<RankedMatch>> PersistAsync()
        {
            var existing = await db.MatchResults.AsNoTracking().Where(x => x.RequestId == requestId).OrderBy(x => x.Rank).ToListAsync(ct);
            if (existing.Count > 0) return existing.Select(MatchRequestRepository.MapResult).ToArray();

            var now = DateTimeOffset.UtcNow;
            var rows = matches.Select((match, index) => new NlpMatchResultRow
            {
                RequestId = requestId, RequesterId = requesterId, CandidateId = match.MemberId,
                Rank = checked((short)(index + 1)), SemanticScore = (decimal)match.SemanticScore,
                ReciprocalScore = (decimal?)match.ReciprocalScore, FinalScore = (decimal)match.Score,
                Label = match.Label.ToUpperInvariant(), ReasonCodes = JsonSerializer.Serialize(match.ReasonCodes),
                ReasonText = match.ReasonText, ModelVersion = modelVersion,
                PreprocessingVersion = preprocessingVersion, RankingVersion = rankingVersion,
                PolicyStatus = "ELIGIBLE", CreatedAt = now
            }).ToArray();
            db.MatchResults.AddRange(rows);
            await db.SaveChangesAsync(ct);
            return rows.Select(MatchRequestRepository.MapResult).ToArray();
        }

        return await PersistAsync();
    }
}

public sealed class FeedbackRepository(NlpDbContext db) : IFeedbackRepository
{
    public async Task<FeedbackRecord> SaveAsync(long matchResultId, string requesterId, string label, string? reasonCode, string? reason, long? supersedesFeedbackId, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await using var command = new NpgsqlCommand("SELECT nlp.save_feedback(@p_match_result_id, @p_requester_id, @p_label, @p_reason_code, @p_reason, @p_supersedes_feedback_id)", connection, (NpgsqlTransaction)transaction.GetDbTransaction());
            command.Parameters.Add(new NpgsqlParameter("p_match_result_id", NpgsqlDbType.Bigint) { Value = matchResultId });
            command.Parameters.Add(new NpgsqlParameter("p_requester_id", NpgsqlDbType.Varchar) { Size = 64, Value = requesterId });
            command.Parameters.Add(new NpgsqlParameter("p_label", NpgsqlDbType.Varchar) { Size = 64, Value = label });
            command.Parameters.Add(new NpgsqlParameter("p_reason_code", NpgsqlDbType.Varchar) { Size = 64, Value = reasonCode is null ? DBNull.Value : reasonCode });
            command.Parameters.Add(new NpgsqlParameter("p_reason", NpgsqlDbType.Varchar) { Size = 1000, Value = reason is null ? DBNull.Value : reason });
            command.Parameters.Add(new NpgsqlParameter("p_supersedes_feedback_id", NpgsqlDbType.Bigint) { Value = supersedesFeedbackId is null ? DBNull.Value : supersedesFeedbackId.Value });
            var feedbackId = Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
            var stored = await db.Feedback.AsNoTracking().SingleAsync(x => x.FeedbackId == feedbackId, ct);
            IntentRepository.AddOutboxEvent(db, "MATCH_RESULT", matchResultId.ToString(System.Globalization.CultureInfo.InvariantCulture), "NlpFeedbackRecorded.v1", new { match_result_id = matchResultId, request_id = stored.RequestId, requester_id = requesterId, candidate_id = stored.CandidateId, label, reason_code = reasonCode });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new FeedbackRecord(stored.FeedbackId, stored.MatchResultId, stored.RequesterId, stored.Label, stored.CreatedAt);
        }

        var match = await db.MatchResults.AsNoTracking().SingleOrDefaultAsync(x => x.MatchResultId == matchResultId && x.RequesterId == requesterId, ct)
            ?? throw new DomainNotFoundException("MATCH_RESULT_NOT_FOUND");

        if (supersedesFeedbackId is { } previousId)
        {
            var previous = await db.Feedback.AsNoTracking().SingleOrDefaultAsync(x => x.FeedbackId == previousId, ct)
                ?? throw new DomainNotFoundException("FEEDBACK_NOT_FOUND");
            if (previous.MatchResultId != matchResultId || previous.RequesterId != requesterId) throw new DomainConflictException("FEEDBACK_SUPERSESSION_INVALID");
            if (await db.Feedback.AnyAsync(x => x.SupersedesFeedbackId == previousId, ct)) throw new DomainConflictException("FEEDBACK_ALREADY_SUPERSEDED");
        }
        else if (await db.Feedback.AnyAsync(x => x.MatchResultId == matchResultId && x.RequesterId == requesterId && x.SupersedesFeedbackId == null, ct))
        {
            throw new DomainConflictException("FEEDBACK_ALREADY_EXISTS");
        }

        var row = new NlpFeedbackRow
        {
            SupersedesFeedbackId = supersedesFeedbackId, MatchResultId = matchResultId,
            RequestId = match.RequestId, RequesterId = requesterId, CandidateId = match.CandidateId,
            Label = label, ReasonCode = reasonCode, Reason = reason, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Feedback.Add(row);
        IntentRepository.AddOutboxEvent(db, "MATCH_RESULT", matchResultId.ToString(System.Globalization.CultureInfo.InvariantCulture), "NlpFeedbackRecorded.v1", new { match_result_id = matchResultId, request_id = match.RequestId, requester_id = requesterId, candidate_id = match.CandidateId, label, reason_code = reasonCode });
        await db.SaveChangesAsync(ct);
        return new FeedbackRecord(row.FeedbackId, row.MatchResultId, row.RequesterId, row.Label, row.CreatedAt);
    }

    public async Task<FeedbackRecord> SaveLegacyAsync(string requestId, string requesterId, string candidateId, string label, string? reason, CancellationToken ct)
    {
        var result = await db.MatchResults.AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == requestId && x.RequesterId == requesterId && x.CandidateId == candidateId, ct)
            ?? throw new DomainNotFoundException("MATCH_RESULT_NOT_FOUND");
        return await SaveAsync(result.MatchResultId, requesterId, label, null, reason, null, ct);
    }
}

public sealed class EvaluationRepository(NlpDbContext db) : IEvaluationRepository
{
    public async Task<EvaluationExecution> TryStartAsync(string evaluationRunId, string datasetId, string modelVersion, string rankingVersion, CancellationToken ct)
    {
        var existing = await db.EvaluationRuns.SingleOrDefaultAsync(x => x.EvaluationRunId == evaluationRunId, ct);
        if (existing is not null)
        {
            if (existing.DatasetId != datasetId || existing.ModelVersion != modelVersion || existing.RankingVersion != rankingVersion)
                throw new DomainConflictException("IDEMPOTENCY_KEY_REUSED");
            return Map(existing);
        }

        var approved = await db.EvaluationDatasets.AsNoTracking().AnyAsync(x => x.DatasetId == datasetId && x.Status == "APPROVED", ct);
        if (!approved) throw new DomainNotFoundException("EVALUATION_DATASET_NOT_APPROVED");
        var row = new NlpEvaluationRunRow
        {
            EvaluationRunId = evaluationRunId, DatasetId = datasetId, ModelVersion = modelVersion,
            RankingVersion = rankingVersion, Status = "RUNNING", StartedAt = DateTimeOffset.UtcNow
        };
        db.EvaluationRuns.Add(row);
        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<IReadOnlyList<EvaluationSample>> GetApprovedSamplesAsync(string datasetId, CancellationToken ct)
    {
        var approved = await db.EvaluationDatasets.AsNoTracking().AnyAsync(x => x.DatasetId == datasetId && x.Status == "APPROVED", ct);
        if (!approved) return [];
        return await db.EvaluationPairs.AsNoTracking().Where(x => x.DatasetId == datasetId && x.Split == "TEST")
            .OrderBy(x => x.EvaluationPairId)
            .Select(x => new EvaluationSample(x.EvaluationPairId, x.RequesterIntentText, x.CandidateIntentText, x.GoldLabel))
            .ToListAsync(ct);
    }

    public async Task CompleteAsync(string evaluationRunId, string metricsJson, CancellationToken ct)
    {
        var row = await db.EvaluationRuns.SingleAsync(x => x.EvaluationRunId == evaluationRunId, ct);
        row.Status = "PASSED";
        row.MetricsJson = metricsJson;
        row.CompletedAt = DateTimeOffset.UtcNow;
        IntentRepository.AddOutboxEvent(db, "EVALUATION_RUN", evaluationRunId, "NlpEvaluationRunCompleted.v1", new { evaluation_run_id = evaluationRunId, dataset_id = row.DatasetId, model_version = row.ModelVersion, ranking_version = row.RankingVersion, status = row.Status });
        await db.SaveChangesAsync(ct);
    }

    public async Task FailAsync(string evaluationRunId, CancellationToken ct)
    {
        var row = await db.EvaluationRuns.SingleOrDefaultAsync(x => x.EvaluationRunId == evaluationRunId, ct);
        if (row is null || row.Status != "RUNNING") return;
        row.Status = "ERROR";
        row.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<EvaluationExecution?> GetAsync(string evaluationRunId, CancellationToken ct)
    {
        var row = await db.EvaluationRuns.AsNoTracking().SingleOrDefaultAsync(x => x.EvaluationRunId == evaluationRunId, ct);
        return row is null ? null : Map(row);
    }

    private static EvaluationExecution Map(NlpEvaluationRunRow row) => new(
        row.EvaluationRunId, row.DatasetId, row.ModelVersion, row.RankingVersion,
        row.Status, row.MetricsJson, row.StartedAt, row.CompletedAt);
}
