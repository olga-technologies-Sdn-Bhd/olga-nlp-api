using Microsoft.EntityFrameworkCore;
using Olga.Nlp.Application;

namespace Olga.Nlp.Infrastructure;

public sealed class EmbeddingJobProcessor(
    NlpDbContext db,
    IEmbeddingProvider provider,
    IIntentRepository repository)
{
    public async Task ProcessAsync(NlpProcessingJobRow job, CancellationToken ct)
    {
        try
        {
            var intent = await db.Intents.AsNoTracking().SingleOrDefaultAsync(x => x.IntentId == job.IntentId, ct);
            if (intent is null || intent.Status != "PROCESSING")
            {
                job.Status = "SUCCEEDED";
                job.ErrorCode = null;
            }
            else
            {
                var embedding = await provider.EmbedAsync(intent.NormalizedText, ct);
                await repository.MarkReadyAsync(
                    intent.IntentId,
                    intent.NormalizedText,
                    intent.NormalizedHash,
                    embedding,
                    provider.ModelVersion,
                    intent.PreprocessingVersion,
                    ct);
                job.Status = "SUCCEEDED";
                job.ErrorCode = null;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            job.AttemptCount++;
            job.Status = job.AttemptCount >= 5 ? "DEAD" : "FAILED";
            job.AvailableAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, job.AttemptCount) * 5));
            job.ErrorCode = exception is ArgumentException ? "EMBEDDING_INPUT_INVALID" : "EMBEDDING_PROVIDER_FAILED";
        }
        finally
        {
            job.LockedUntil = null;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }
}
