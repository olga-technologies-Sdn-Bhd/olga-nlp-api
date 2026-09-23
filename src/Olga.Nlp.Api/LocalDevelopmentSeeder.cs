using Microsoft.EntityFrameworkCore;
using Olga.Nlp.Application;
using Olga.Nlp.Contracts;
using Olga.Nlp.Infrastructure;

namespace Olga.Nlp.Api;

public static class LocalDevelopmentSeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NlpDbContext>();
        if (await db.Intents.AnyAsync(ct)) return;

        db.RankingConfigs.Add(new() { RankingVersion = "ranking-v1", SemanticWeight = .40m, CategoryWeight = .25m, IndustryWeight = .15m, GeographyWeight = .10m, FreshnessWeight = .10m, Threshold = .35m, ActiveFrom = DateTimeOffset.UtcNow.AddDays(-1) });
        db.ModelVersions.Add(new() { ModelVersion = "fake-embedding-v2", Provider = "LOCAL", DeploymentName = "fake", Dimensions = AzureEmbeddingOptions.Dimensions, PreprocessingVersion = "normalizer-v1", Status = "ACTIVE", ActivatedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow });
        db.EvaluationDatasets.Add(new() { DatasetId = "local-smoke-v1", Name = "Local smoke test", Version = "1", SourcePolicy = "Synthetic and de-identified", Status = "APPROVED", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        db.EvaluationPairs.AddRange(
            new() { DatasetId = "local-smoke-v1", RequesterIntentText = "cold-chain storage", CandidateIntentText = "temperature-controlled warehouse", GoldLabel = "STRONG", Split = "TEST" },
            new() { DatasetId = "local-smoke-v1", RequesterIntentText = "cold-chain storage", CandidateIntentText = "website design", GoldLabel = "NONE", Split = "TEST" });
        foreach (var member in new[] { "A123", "B456", "C789", "D111" }) db.MemberEligibility.Add(new() { MemberId = member, ContextId = "event-001", IsLive = true, IsVisible = true, HasConsent = true });
        db.MemberRelationships.Add(new() { MemberId = "A123", OtherMemberId = "D111", ContextId = "event-001", IsBlocked = true });
        await db.SaveChangesAsync(ct);

        var intents = scope.ServiceProvider.GetRequiredService<IIntentService>();
        var expiry = DateTimeOffset.UtcNow.AddDays(30);
        await Save(intents, "A123", "a-want", "WANT", "Need cold-chain storage for pharmaceutical products", "cold-chain storage", "pharmaceutical", "Selangor", expiry, ct);
        await Save(intents, "A123", "a-offer", "OFFER", "Healthcare distribution and pharmaceutical transport", "distribution", "pharmaceutical", "Selangor", expiry, ct);
        await Save(intents, "B456", "b-offer", "OFFER", "Temperature-controlled warehouse for healthcare clients", "cold-chain storage", "pharmaceutical", "Selangor", expiry, ct);
        await Save(intents, "B456", "b-want", "WANT", "Looking for a pharmaceutical distribution partner", "distribution", "pharmaceutical", "Selangor", expiry, ct);
        await Save(intents, "C789", "c-offer", "OFFER", "Digital marketing and website design services", "marketing", "technology", "Kuala Lumpur", expiry, ct);
        await Save(intents, "D111", "d-offer", "OFFER", "Cold-chain pharmaceutical warehouse", "cold-chain storage", "pharmaceutical", "Selangor", expiry, ct);
    }

    private static Task<IntentResponse> Save(IIntentService service, string memberId, string intentId, string type, string text, string category, string industry, string geography, DateTimeOffset expiry, CancellationToken ct) => service.SaveAsync(memberId, new IntentUpsertRequest(intentId, "event-001", type, text, expiry, category, industry, geography), null, ct);
}
