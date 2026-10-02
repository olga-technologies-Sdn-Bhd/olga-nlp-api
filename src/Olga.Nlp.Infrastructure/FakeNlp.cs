using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Olga.Nlp.Application;
using Olga.Nlp.Domain;

namespace Olga.Nlp.Infrastructure;

public sealed class PiiChecker : IPiiChecker
{
    private static readonly Regex Pii = new(@"(?:[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}|\+?\d[\d\s().-]{7,}\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public bool ContainsPii(string text) => Pii.IsMatch(text);
    public string Mask(string text) => Pii.Replace(text, "[REDACTED]");
}

public sealed class TextNormalizer(IPiiChecker pii) : ITextNormalizer
{
    public NormalizedText Normalize(string text, string? requestedLanguage = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000) throw new ArgumentException("TEXT_INVALID");
        var value = pii.Mask(text.Normalize(NormalizationForm.FormKC)).Trim();
        value = Regex.Replace(value, @"\s+", " ", RegexOptions.CultureInvariant);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return new(text, value, requestedLanguage ?? "en", hash, pii.ContainsPii(text));
    }
}

public sealed class FakeEmbeddingProvider : IEmbeddingProvider
{
    private static readonly Dictionary<string, string> Concepts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["temperature"] = "coldchain", ["controlled"] = "coldchain", ["cold"] = "coldchain", ["chain"] = "coldchain",
        ["pharma"] = "pharmaceutical", ["medicine"] = "pharmaceutical", ["healthcare"] = "pharmaceutical",
        ["warehouse"] = "storage", ["warehousing"] = "storage", ["logistics"] = "distribution", ["distributor"] = "distribution"
    };

    public string ModelVersion => "fake-embedding-v2";

    public Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var vector = new float[AzureEmbeddingOptions.Dimensions];
        foreach (Match match in Regex.Matches(text.ToLowerInvariant(), "[a-z0-9]+", RegexOptions.CultureInvariant))
        {
            var token = Concepts.TryGetValue(match.Value, out var concept) ? concept : match.Value;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            var index = BinaryPrimitives.ReadUInt16LittleEndian(hash) % vector.Length;
            vector[index] += (hash[2] & 1) == 0 ? 1 : -1;
        }
        var norm = MathF.Sqrt(vector.Sum(x => x * x));
        if (norm > 0) for (var i = 0; i < vector.Length; i++) vector[i] /= norm;
        return Task.FromResult(vector);
    }
}

public sealed class LocalEmbeddingProvider : IEmbeddingProvider
{
    private readonly FakeEmbeddingProvider inner = new();
    public string ModelVersion => "local-placeholder-v1";
    public Task<float[]> EmbedAsync(string text, CancellationToken ct) => inner.EmbedAsync(text, ct);
}

public sealed class ReciprocalScorer : IReciprocalScorer
{
    public PairScore Score(float[] requesterWant, float[] candidateOffer, float[]? candidateWant, float[]? requesterOffer)
    {
        var forward = Cosine(requesterWant, candidateOffer);
        double? reverse = candidateWant is not null && requesterOffer is not null ? Cosine(candidateWant, requesterOffer) : null;
        var reciprocal = reverse is null ? forward : HarmonicMean(forward, reverse.Value);
        return new(forward, reverse, reciprocal, reciprocal);
    }

    private static double HarmonicMean(double a, double b) => a + b <= 0 ? 0 : 2 * a * b / (a + b);
    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException("EMBEDDING_DIMENSION_MISMATCH");
        return Math.Clamp(a.Zip(b).Sum(x => x.First * x.Second), 0, 1);
    }
}

public sealed class ExplanationGenerator : IExplanationGenerator
{
    public (IReadOnlyList<string> Codes, string Text) Explain(MemberIntents requester, Candidate candidate, PairScore score)
    {
        var codes = new List<string>();
        if (candidate.CategoryCompatibility > 0) codes.Add("CATEGORY_COMPLEMENT");
        if (candidate.IndustryCompatibility > 0) codes.Add("INDUSTRY_MATCH");
        if (candidate.GeographyCompatibility > 0) codes.Add("GEOGRAPHY_MATCH");
        if (score.Reverse is not null) codes.Add("RECIPROCAL_INTENT");
        if (candidate.Freshness >= .75) codes.Add("RECENT_INTENT");
        if (codes.Count == 0 || score.Semantic < .20) return (Array.Empty<string>(), string.Empty);
        var requirement = requester.Want?.Category ?? "your requirement";
        return (codes, $"This member offers {candidate.Offer.Category ?? "services"} relevant to {requirement}.");
    }
}

public sealed class MatchRanker(IExplanationGenerator explanations) : IMatchRanker
{
    public IReadOnlyList<RankedMatch> Rank(MemberIntents requester, IReadOnlyList<(Candidate Candidate, PairScore Score)> candidates, RankingConfig config, int limit)
    {
        var ranked = candidates.Select(x =>
        {
            var score = Math.Clamp(config.SemanticWeight * x.Score.Semantic + config.CategoryWeight * x.Candidate.CategoryCompatibility + config.IndustryWeight * x.Candidate.IndustryCompatibility + config.GeographyWeight * x.Candidate.GeographyCompatibility + config.FreshnessWeight * x.Candidate.Freshness, 0, 1);
            var explanation = explanations.Explain(requester, x.Candidate, x.Score);
            return new RankedMatch(x.Candidate.MemberId, score, score >= .75 ? "STRONG_MATCH" : "PLAUSIBLE_MATCH", explanation.Codes, explanation.Text, x.Score.Semantic, x.Score.Reverse is null ? null : x.Score.Reciprocal);
        }).Where(x => x.Score >= config.Threshold && x.ReasonCodes.Count > 0).OrderByDescending(x => x.Score).Take(limit).ToArray();

        return ranked.Select((match, index) => match with { Rank = index + 1 }).ToArray();
    }
}
