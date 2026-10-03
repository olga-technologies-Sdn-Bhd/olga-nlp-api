using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Olga.Nlp.Contracts;
using Olga.Nlp.Domain;

namespace Olga.Nlp.Application;

public sealed class IntentService(IIntentRepository intents, ITextNormalizer normalizer, IIntentProcessingDispatcher dispatcher) : IIntentService
{
    private const string PreprocessingVersion = "normalizer-v1";

    public async Task<IntentResponse> SaveAsync(string memberId, IntentUpsertRequest request, string? expectedETag, CancellationToken ct)
    {
        if (!Enum.TryParse<IntentType>(request.IntentType, true, out var type)) throw new ArgumentException("INTENT_TYPE_INVALID");
        if (string.IsNullOrWhiteSpace(request.IntentId) || request.IntentId.Length > 64 ||
            string.IsNullOrWhiteSpace(request.ContextId)) throw new ArgumentException("INTENT_IDENTITY_INVALID");
        if (request.ExpiresAt <= DateTimeOffset.UtcNow) throw new ArgumentException("INTENT_EXPIRY_INVALID");

        var normalized = normalizer.Normalize(request.Text, request.Language);
        var now = DateTimeOffset.UtcNow;
        var pending = new Intent(
            request.IntentId, memberId, request.ContextId, type, request.Text, normalized.Value,
            request.ExpiresAt, IntentStatus.Processing, null, null, PreprocessingVersion,
            request.Category, request.Industry, request.Geography, normalized.Hash,
            normalized.Language, normalized.ContainsPii, now, now, string.Empty);

        await intents.UpsertProcessingAsync(pending, expectedETag, ct);
        var processed = await dispatcher.DispatchAsync(pending, ct);
        var saved = processed ?? await intents.GetAsync(memberId, request.IntentId, ct) ?? pending;

        return new IntentResponse(
            saved.IntentId,
            ToStorageStatus(saved.Status),
            saved.ModelVersion ?? dispatcher.ModelVersion ?? string.Empty,
            saved.PreprocessingVersion,
            saved.NormalizedHash,
            saved.LanguageCode,
            saved.ContainsPii,
            saved.UpdatedAt,
            saved.ETag);
    }

    public async Task<IntentDetailResponse?> GetAsync(string memberId, string intentId, CancellationToken ct)
    {
        var value = await intents.GetAsync(memberId, intentId, ct);
        return value is null ? null : new IntentDetailResponse(
            value.IntentId, value.ContextId, value.Type.ToString().ToUpperInvariant(), value.OriginalText,
            value.NormalizedText, value.NormalizedHash, value.LanguageCode, value.ContainsPii,
            ToStorageStatus(value.Status), value.ExpiresAt, value.PreprocessingVersion, value.ModelVersion,
            value.Category, value.Industry, value.Geography, value.CreatedAt, value.UpdatedAt, value.ETag);
    }

    private static string ToStorageStatus(IntentStatus status) => status switch
    {
        IntentStatus.MatchReady => "MATCH_READY",
        IntentStatus.Processing => "PROCESSING",
        IntentStatus.Failed => "FAILED",
        _ => "INACTIVE"
    };
}

public sealed class MatchingService(
    ICandidateRepository candidates,
    IMatchRequestRepository requests,
    IMatchResultRepository results,
    IRankingConfigRepository rankingConfigs,
    ITextNormalizer normalizer,
    IEmbeddingProvider embeddings,
    IReciprocalScorer scorer,
    IMatchRanker ranker) : IMatchingService
{
    public async Task<MatchSearchResponse> SearchAsync(string requesterId, MatchSearchRequest request, CancellationToken ct)
    {
        var language = request.Options?.Language ?? "en";
        var requestHash = ComputeRequestHash(requesterId, request, language);
        var execution = new MatchExecution(
            request.RequestId, requestHash, requesterId, request.IntentId, request.ContextId, language,
            request.Limit, JsonSerializer.Serialize(request.Options), MatchExecutionStatus.Processing,
            CreatedAt: DateTimeOffset.UtcNow);

        var start = await requests.TryStartAsync(execution, ct);
        if (start.Disposition == MatchStartDisposition.Conflict) throw new DomainConflictException("IDEMPOTENCY_KEY_REUSED");
        if (start.Disposition is MatchStartDisposition.Replay or MatchStartDisposition.InProgress)
            return await ToSearchResponseAsync(start.Execution, ct);

        try
        {
            var requester = await candidates.GetRequesterIntentsAsync(requesterId, request.IntentId, request.ContextId, ct)
                ?? throw new DomainNotFoundException("REQUESTER_INTENT_NOT_READY");
            var requesterWant = requester.Want?.Embedding ?? throw new DomainNotFoundException("REQUESTER_WANT_NOT_READY");
            var modelVersion = requester.Want.ModelVersion ?? throw new DomainNotFoundException("REQUESTER_MODEL_NOT_READY");
            var pool = await candidates.GetEligibleCandidatesAsync(requesterId, request.ContextId, modelVersion, Math.Clamp(request.Limit * 20, 50, 200), ct);

            var scored = pool
                .Where(candidate => request.Options?.RequireReciprocal != true || candidate.Want?.Embedding is not null && requester.Offer?.Embedding is not null)
                .Select(candidate =>
                {
                    var candidateOffer = candidate.Offer.Embedding ?? throw new DomainNotFoundException("CANDIDATE_EMBEDDING_NOT_READY");
                    return (candidate, scorer.Score(requesterWant, candidateOffer, candidate.Want?.Embedding, requester.Offer?.Embedding));
                }).ToArray();

            var config = await rankingConfigs.GetActiveAsync(ct);
            if (request.Options?.Threshold is { } threshold)
            {
                if (threshold is < 0 or > 1) throw new ArgumentException("MATCH_THRESHOLD_INVALID");
                config = config with { Threshold = threshold };
            }

            var ranked = ranker.Rank(requester, scored, config, Math.Clamp(request.Limit, 3, 7));
            var saved = await results.SaveAsync(request.RequestId, requesterId, ranked, modelVersion, requester.Want.PreprocessingVersion, config.Version, ct);
            await requests.CompleteAsync(request.RequestId, requester.Want.PreprocessingVersion, modelVersion, config.Version, config.Threshold, pool.Count, ct);

            return ToSearchResponse(execution with
            {
                Status = MatchExecutionStatus.Completed,
                PreprocessingVersion = requester.Want.PreprocessingVersion,
                ModelVersion = modelVersion,
                RankingVersion = config.Version,
                RankingThreshold = config.Threshold,
                CandidateCount = pool.Count,
                CompletedAt = DateTimeOffset.UtcNow
            }, saved);
        }
        catch (Exception exception)
        {
            await requests.FailAsync(request.RequestId, SafeErrorCode(exception), ct);
            throw;
        }
    }

    public async Task<MatchRequestStatusResponse?> GetAsync(string requesterId, string requestId, CancellationToken ct)
    {
        var execution = await requests.GetAsync(requestId, requesterId, ct);
        if (execution is null) return null;
        var storedResults = execution.Status == MatchExecutionStatus.Completed
            ? await requests.GetResultsAsync(requestId, ct)
            : Array.Empty<RankedMatch>();

        return new MatchRequestStatusResponse(
            execution.RequestId, execution.Status.ToString().ToUpperInvariant(), ToResponses(storedResults),
            execution.ModelVersion, execution.PreprocessingVersion, execution.RankingVersion,
            execution.RankingThreshold, execution.CandidateCount, execution.CreatedAt,
            execution.CompletedAt, execution.ErrorCode);
    }

    public async Task<PairScore> ScorePairAsync(ScorePairRequest request, CancellationToken ct)
    {
        var requesterWant = await embeddings.EmbedAsync(normalizer.Normalize(request.RequesterWant).Value, ct);
        var candidateOffer = await embeddings.EmbedAsync(normalizer.Normalize(request.CandidateOffer).Value, ct);
        var candidateWant = await embeddings.EmbedAsync(normalizer.Normalize(request.CandidateWant).Value, ct);
        var requesterOffer = await embeddings.EmbedAsync(normalizer.Normalize(request.RequesterOffer).Value, ct);
        return scorer.Score(requesterWant, candidateOffer, candidateWant, requesterOffer);
    }

    private async Task<MatchSearchResponse> ToSearchResponseAsync(MatchExecution execution, CancellationToken ct)
    {
        var storedResults = execution.Status == MatchExecutionStatus.Completed
            ? await requests.GetResultsAsync(execution.RequestId, ct)
            : Array.Empty<RankedMatch>();
        return ToSearchResponse(execution, storedResults);
    }

    private static MatchSearchResponse ToSearchResponse(MatchExecution execution, IReadOnlyList<RankedMatch> matches) => new(
        execution.RequestId, ToResponses(matches), execution.ModelVersion ?? string.Empty,
        execution.PreprocessingVersion ?? string.Empty, execution.RankingVersion ?? string.Empty,
        execution.Status.ToString().ToUpperInvariant(), execution.RankingThreshold,
        execution.CandidateCount, execution.CompletedAt);

    private static IReadOnlyList<MatchResponse> ToResponses(IReadOnlyList<RankedMatch> matches) => matches
        .Select(x => new MatchResponse(x.MemberId, x.Score, x.Label, x.ReasonCodes, x.ReasonText,
            x.MatchResultId, x.Rank, x.SemanticScore, x.ReciprocalScore)).ToArray();

    private static string ComputeRequestHash(string requesterId, MatchSearchRequest request, string language)
    {
        var canonical = string.Join('|', requesterId, request.IntentId, request.ContextId, request.Limit,
            request.Options?.Threshold?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            language.ToLowerInvariant(), request.Options?.RequireReciprocal == true ? "1" : "0");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string SafeErrorCode(Exception exception) => exception switch
    {
        DomainConflictException conflict => conflict.Code,
        DomainNotFoundException notFound => notFound.Code,
        ArgumentException argument => argument.Message,
        _ => "MATCH_EXECUTION_FAILED"
    };
}

public sealed class FeedbackService(IFeedbackRepository feedback, IPiiChecker pii) : IFeedbackService
{
    private static readonly HashSet<string> Labels = new(StringComparer.OrdinalIgnoreCase) { "USEFUL", "NOT_USEFUL", "INAPPROPRIATE" };

    public async Task<FeedbackResponse> SaveAsync(long matchResultId, string requesterId, FeedbackCreateRequest request, CancellationToken ct)
    {
        Validate(request.Label, request.ReasonCode, request.Reason);
        var saved = await feedback.SaveAsync(matchResultId, requesterId, request.Label.ToUpperInvariant(), request.ReasonCode?.ToUpperInvariant(), request.Reason, request.SupersedesFeedbackId, ct);
        return new(saved.FeedbackId, saved.MatchResultId, saved.Label, saved.CreatedAt);
    }

    public async Task<FeedbackResponse> SaveLegacyAsync(string requesterId, FeedbackRequest request, CancellationToken ct)
    {
        Validate(request.Label, null, request.Reason);
        var saved = await feedback.SaveLegacyAsync(request.RequestId, requesterId, request.CandidateId, request.Label.ToUpperInvariant(), request.Reason, ct);
        return new(saved.FeedbackId, saved.MatchResultId, saved.Label, saved.CreatedAt);
    }

    private void Validate(string label, string? reasonCode, string? reason)
    {
        if (!Labels.Contains(label)) throw new ArgumentException("FEEDBACK_LABEL_INVALID");
        if (reasonCode?.Length > 64 || reason?.Length > 1000) throw new ArgumentException("FEEDBACK_REASON_INVALID");
        if (!string.IsNullOrWhiteSpace(reason) && pii.ContainsPii(reason)) throw new ArgumentException("FEEDBACK_PII_DETECTED");
    }
}
