using Olga.Nlp.Domain;
using Olga.Nlp.Infrastructure;

namespace Olga.Nlp.UnitTests;

public sealed class NlpTests
{
    [Fact]
    public void Normalization_is_deterministic_and_masks_pii()
    {
        var normalizer = new TextNormalizer(new PiiChecker());
        var actual = normalizer.Normalize("  Need  cold-chain\tstorage. Email a@b.com ");
        Assert.Equal("Need cold-chain storage. Email [REDACTED]", actual.Value);
        Assert.True(actual.ContainsPii);
        Assert.Equal(actual.Hash, normalizer.Normalize("Need cold-chain storage. Email a@b.com").Hash);
    }

    [Fact]
    public async Task Fake_embeddings_are_stable_and_semantic_for_local_fixtures()
    {
        var firstProvider = new FakeEmbeddingProvider();
        var secondProvider = new FakeEmbeddingProvider();
        var want = await firstProvider.EmbedAsync("cold-chain pharmaceutical storage", default);
        var related = await secondProvider.EmbedAsync("temperature controlled healthcare warehouse", default);
        var unrelated = await secondProvider.EmbedAsync("digital marketing website", default);
        Assert.Equal(AzureEmbeddingOptions.Dimensions, want.Length);
        Assert.Equal(want, await secondProvider.EmbedAsync("cold-chain pharmaceutical storage", default));
        var scorer = new ReciprocalScorer();
        Assert.True(scorer.Score(want, related, null, null).Forward > scorer.Score(want, unrelated, null, null).Forward);
    }

    [Fact]
    public void Reciprocal_score_uses_harmonic_mean_and_supports_one_way()
    {
        var scorer = new ReciprocalScorer();
        var oneWay = scorer.Score([1, 0], [1, 0], null, null);
        var reciprocal = scorer.Score([1, 0], [1, 0], [0, 1], [1, 0]);
        Assert.Equal(1, oneWay.Reciprocal);
        Assert.Equal(0, reciprocal.Reciprocal);
    }

    [Fact]
    public void Ranker_applies_versioned_business_weights_and_reasons()
    {
        var requester = new MemberIntents("A", Intent("a-offer", "A", IntentType.Offer, "distribution"), Intent("a-want", "A", IntentType.Want, "storage"));
        var candidate = new Candidate("B", Intent("b-offer", "B", IntentType.Offer, "storage"), Intent("b-want", "B", IntentType.Want, "distribution"), 1, 1, 1, 1);
        var ranked = new MatchRanker(new ExplanationGenerator()).Rank(requester, [(candidate, new PairScore(.9, .8, .85, .85))], new RankingConfig("ranking-test"), 7);
        Assert.Single(ranked);
        Assert.Contains("RECIPROCAL_INTENT", ranked[0].ReasonCodes);
        Assert.True(ranked[0].Score >= .75);
    }

    private static Intent Intent(string id, string member, IntentType type, string category) => new(id, member, "event", type, category, category, DateTimeOffset.UtcNow.AddDays(1), IntentStatus.MatchReady, [1, 0], "fake-v1", "normalizer-v1", category, "pharma", "Selangor");
}
