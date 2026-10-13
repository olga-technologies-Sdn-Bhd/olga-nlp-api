using Olga.Nlp.Contracts;
using Olga.Nlp.Domain;

namespace Olga.Nlp.Application;

public interface ITextNormalizer { NormalizedText Normalize(string text, string? requestedLanguage = null); }
public interface IPiiChecker { bool ContainsPii(string text); string Mask(string text); }
public interface IEmbeddingProvider { string ModelVersion { get; } Task<float[]> EmbedAsync(string text, CancellationToken ct); }
public interface IIntentProcessingDispatcher
{
    string? ModelVersion { get; }
    Task<Intent?> DispatchAsync(Intent intent, CancellationToken ct);
}
public interface IReciprocalScorer { PairScore Score(float[] requesterWant, float[] candidateOffer, float[]? candidateWant, float[]? requesterOffer); }
public interface IMatchRanker { IReadOnlyList<RankedMatch> Rank(MemberIntents requester, IReadOnlyList<(Candidate Candidate, PairScore Score)> candidates, RankingConfig config, int limit); }
public interface IExplanationGenerator { (IReadOnlyList<string> Codes, string Text) Explain(MemberIntents requester, Candidate candidate, PairScore score); }

public interface IIntentRepository
{
    Task UpsertProcessingAsync(Intent intent, string? expectedETag, CancellationToken ct);
    Task<Intent?> GetAsync(string memberId, string intentId, CancellationToken ct);
    Task<Intent> MarkReadyAsync(string intentId, string normalizedText, string normalizedHash, float[] embedding, string modelVersion, string preprocessingVersion, CancellationToken ct);
}

public interface ICandidateRepository
{
    Task<MemberIntents?> GetRequesterIntentsAsync(string memberId, string requestedIntentId, string contextId, CancellationToken ct);
    Task<IReadOnlyList<Candidate>> GetEligibleCandidatesAsync(string requesterId, string contextId, string modelVersion, int maxRows, CancellationToken ct);
}

public interface IRankingConfigRepository { Task<RankingConfig> GetActiveAsync(CancellationToken ct); }

public interface IMatchRequestRepository
{
    Task<MatchStartResult> TryStartAsync(MatchExecution execution, CancellationToken ct);
    Task<MatchExecution?> GetAsync(string requestId, string requesterId, CancellationToken ct);
    Task<IReadOnlyList<RankedMatch>> GetResultsAsync(string requestId, CancellationToken ct);
    Task CompleteAsync(string requestId, string preprocessingVersion, string modelVersion, string rankingVersion, double threshold, int candidateCount, CancellationToken ct);
    Task FailAsync(string requestId, string errorCode, CancellationToken ct);
}

public interface IMatchResultRepository
{
    Task<IReadOnlyList<RankedMatch>> SaveAsync(string requestId, string requesterId, IReadOnlyList<RankedMatch> matches, string modelVersion, string preprocessingVersion, string rankingVersion, CancellationToken ct);
}

public interface IFeedbackRepository
{
    Task<FeedbackRecord> SaveAsync(long matchResultId, string requesterId, string label, string? reasonCode, string? reason, long? supersedesFeedbackId, CancellationToken ct);
    Task<FeedbackRecord> SaveLegacyAsync(string requestId, string requesterId, string candidateId, string label, string? reason, CancellationToken ct);
}

public interface IIntentService
{
    Task<IntentResponse> SaveAsync(string memberId, IntentUpsertRequest request, string? expectedETag, CancellationToken ct);
    Task<IntentDetailResponse?> GetAsync(string memberId, string intentId, CancellationToken ct);
}

public interface IMatchingService
{
    Task<MatchSearchResponse> SearchAsync(string requesterId, MatchSearchRequest request, CancellationToken ct);
    Task<MatchRequestStatusResponse?> GetAsync(string requesterId, string requestId, CancellationToken ct);
    Task<PairScore> ScorePairAsync(ScorePairRequest request, CancellationToken ct);
}

public interface ILiveMatchCountService
{
    Task<LiveMatchCountResponse> CountAsync(string requesterId, string eventId, string intentId, double threshold, bool requireReciprocal, CancellationToken ct);
}

public interface IFeedbackService
{
    Task<FeedbackResponse> SaveAsync(long matchResultId, string requesterId, FeedbackCreateRequest request, CancellationToken ct);
    Task<FeedbackResponse> SaveLegacyAsync(string requesterId, FeedbackRequest request, CancellationToken ct);
}

public interface IEvaluationRepository
{
    Task<EvaluationExecution> TryStartAsync(string evaluationRunId, string datasetId, string modelVersion, string rankingVersion, CancellationToken ct);
    Task<IReadOnlyList<EvaluationSample>> GetApprovedSamplesAsync(string datasetId, CancellationToken ct);
    Task CompleteAsync(string evaluationRunId, string metricsJson, CancellationToken ct);
    Task FailAsync(string evaluationRunId, CancellationToken ct);
    Task<EvaluationExecution?> GetAsync(string evaluationRunId, CancellationToken ct);
}

public interface IEvaluationService
{
    Task<EvaluationRunResponse> RunAsync(string evaluationRunId, EvaluationRunRequest request, CancellationToken ct);
    Task<EvaluationRunResponse?> GetAsync(string evaluationRunId, CancellationToken ct);
}
