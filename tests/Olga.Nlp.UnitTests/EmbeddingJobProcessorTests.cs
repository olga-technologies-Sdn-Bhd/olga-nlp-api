using Microsoft.EntityFrameworkCore;
using Olga.Nlp.Application;
using Olga.Nlp.Infrastructure;

namespace Olga.Nlp.UnitTests;

public sealed class EmbeddingJobProcessorTests
{
    [Fact]
    public async Task Failed_job_is_retried_then_marked_dead_at_the_attempt_limit()
    {
        await using var db = CreateDatabase();
        var (intent, job) = AddPendingJob(db);
        await db.SaveChangesAsync();
        var processor = new EmbeddingJobProcessor(db, new StubProvider(throwOnCall: true), new IntentRepository(db));

        var before = DateTimeOffset.UtcNow;
        await processor.ProcessAsync(job, default);
        Assert.Equal("FAILED", job.Status);
        Assert.Equal(1, job.AttemptCount);
        Assert.Equal("EMBEDDING_PROVIDER_FAILED", job.ErrorCode);
        Assert.True(job.AvailableAt > before);

        for (var attempt = 2; attempt <= 5; attempt++)
            await processor.ProcessAsync(job, default);

        Assert.Equal("DEAD", job.Status);
        Assert.Equal(5, job.AttemptCount);
        Assert.Equal("PROCESSING", intent.Status);
    }

    [Fact]
    public async Task Reprocessing_a_completed_intent_is_idempotent()
    {
        await using var db = CreateDatabase();
        var (_, job) = AddPendingJob(db);
        await db.SaveChangesAsync();
        var provider = new StubProvider();
        var processor = new EmbeddingJobProcessor(db, provider, new IntentRepository(db));

        await processor.ProcessAsync(job, default);
        job.Status = "RUNNING";
        await processor.ProcessAsync(job, default);

        Assert.Equal("SUCCEEDED", job.Status);
        Assert.Equal(1, provider.CallCount);
        Assert.Single(await db.Embeddings.ToListAsync());
        Assert.Equal("MATCH_READY", (await db.Intents.SingleAsync()).Status);
    }

    private static NlpDbContext CreateDatabase() => new(
        new DbContextOptionsBuilder<NlpDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (NlpIntentRow Intent, NlpProcessingJobRow Job) AddPendingJob(NlpDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var intent = new NlpIntentRow
        {
            IntentId = "intent-1",
            MemberId = "member-1",
            ContextId = "event-1",
            IntentType = "WANT",
            OriginalText = "Need storage",
            NormalizedText = "Need storage",
            NormalizedHash = "hash",
            PreprocessingVersion = "normalizer-v1",
            Status = "PROCESSING",
            ExpiresAt = now.AddDays(1),
            CreatedAt = now,
            UpdatedAt = now
        };
        var job = new NlpProcessingJobRow
        {
            JobId = "job-1",
            IntentId = intent.IntentId,
            Status = "RUNNING",
            AvailableAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Intents.Add(intent);
        db.ProcessingJobs.Add(job);
        return (intent, job);
    }

    private sealed class StubProvider(bool throwOnCall = false) : IEmbeddingProvider
    {
        public int CallCount { get; private set; }
        public string ModelVersion => "azure-text-embedding-3-small-1536-v1";

        public Task<float[]> EmbedAsync(string text, CancellationToken ct)
        {
            CallCount++;
            if (throwOnCall) throw new InvalidOperationException("provider failure");
            return Task.FromResult(new float[AzureEmbeddingOptions.Dimensions]);
        }
    }
}
