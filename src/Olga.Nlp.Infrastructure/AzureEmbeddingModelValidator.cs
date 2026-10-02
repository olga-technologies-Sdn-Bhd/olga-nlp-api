using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Olga.Nlp.Infrastructure;

public sealed class AzureEmbeddingModelValidator(
    IServiceScopeFactory scopes,
    AzureEmbeddingOptions options) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NlpDbContext>();
        var model = await db.ModelVersions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ModelVersion == options.ModelVersion, ct);

        if (model is null ||
            model.Status != "ACTIVE" ||
            model.Provider != AzureEmbeddingOptions.ProviderName ||
            model.DeploymentName != options.DeploymentName ||
            model.Dimensions != AzureEmbeddingOptions.Dimensions)
            throw new InvalidOperationException("Azure OpenAI configuration does not match the active nlp_model_version record.");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
