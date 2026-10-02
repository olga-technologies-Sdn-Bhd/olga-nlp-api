namespace Olga.Nlp.Contracts;

public sealed record MatchSearchRequest(string RequestId, string IntentId, string ContextId, int Limit = 7, MatchOptions? Options = null);
public sealed record MatchOptions(double? Threshold = null, string? Language = null, bool RequireReciprocal = false);

public sealed record MatchSearchResponse(
    string RequestId,
    IReadOnlyList<MatchResponse> Matches,
    string ModelVersion,
    string PreprocessingVersion,
    string RankingVersion,
    string Status = "COMPLETED",
    double? AppliedThreshold = null,
    int? CandidateCount = null,
    DateTimeOffset? CompletedAt = null);

public sealed record MatchResponse(
    string MemberId,
    double Score,
    string Label,
    IReadOnlyList<string> ReasonCodes,
    string ReasonText,
    long? MatchResultId = null,
    int? Rank = null,
    double? SemanticScore = null,
    double? ReciprocalScore = null);

public sealed record IntentUpsertRequest(
    string IntentId,
    string ContextId,
    string IntentType,
    string Text,
    DateTimeOffset ExpiresAt,
    string? Category = null,
    string? Industry = null,
    string? Geography = null,
    string? Language = null);

public sealed record IntentResponse(
    string IntentId,
    string Status,
    string ModelVersion,
    string PreprocessingVersion,
    string? NormalizedHash = null,
    string? Language = null,
    bool ContainsPii = false,
    DateTimeOffset? UpdatedAt = null,
    string? ETag = null);

public sealed record IntentDetailResponse(
    string IntentId,
    string ContextId,
    string IntentType,
    string OriginalText,
    string NormalizedText,
    string NormalizedHash,
    string Language,
    bool ContainsPii,
    string Status,
    DateTimeOffset ExpiresAt,
    string PreprocessingVersion,
    string? ModelVersion,
    string? Category,
    string? Industry,
    string? Geography,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ETag);

public sealed record MatchRequestStatusResponse(
    string RequestId,
    string Status,
    IReadOnlyList<MatchResponse> Matches,
    string? ModelVersion,
    string? PreprocessingVersion,
    string? RankingVersion,
    double? AppliedThreshold,
    int? CandidateCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode = null);

public sealed record ScorePairRequest(string RequesterOffer, string RequesterWant, string CandidateOffer, string CandidateWant);

// Compatibility contract. New clients use POST /v1/matches/{matchResultId}/feedback.
public sealed record FeedbackRequest(string RequestId, string CandidateId, string Label, string? Reason);
public sealed record FeedbackCreateRequest(string Label, string? ReasonCode = null, string? Reason = null, long? SupersedesFeedbackId = null);
public sealed record FeedbackResponse(long FeedbackId, long MatchResultId, string Label, DateTimeOffset CreatedAt);

public sealed record EvaluationRunRequest(string DatasetId);
public sealed record EvaluationRunResponse(
    string EvaluationRunId,
    string DatasetId,
    string ModelVersion,
    string RankingVersion,
    string Status,
    IReadOnlyDictionary<string, double>? Metrics,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record NormalizeRequest(string Text, string? Language);
public sealed record NormalizeResponse(string NormalizedText, string Language, string Hash, bool ContainsPii);
public sealed record ApiError(
    string Code,
    string Message,
    string CorrelationId,
    IReadOnlyDictionary<string, string[]>? FieldErrors = null,
    string? StackTrace = null);
