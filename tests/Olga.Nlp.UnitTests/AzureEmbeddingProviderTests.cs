using Olga.Nlp.Infrastructure;

namespace Olga.Nlp.UnitTests;

public sealed class AzureEmbeddingProviderTests
{
    [Fact]
    public void Rejects_missing_or_invalid_startup_configuration()
    {
        Assert.Throws<InvalidOperationException>(() => AzureEmbeddingOptions.Create(null, "deployment", "version", null, null, null));
        Assert.Throws<InvalidOperationException>(() => AzureEmbeddingOptions.Create("http://insecure.example", "deployment", "version", null, null, null));
        Assert.Throws<InvalidOperationException>(() => AzureEmbeddingOptions.Create("https://valid.example", "", "version", null, null, null));
        Assert.Throws<InvalidOperationException>(() => AzureEmbeddingOptions.Create("https://valid.example", "deployment", "", null, null, null));
    }

    [Fact]
    public async Task Uses_configured_endpoint_deployment_required_model_dimensions_and_version()
    {
        AzureEmbeddingRequest? captured = null;
        var client = new StubClient((request, _) =>
        {
            captured = request;
            return Task.FromResult(new float[AzureEmbeddingOptions.Dimensions]);
        });
        var provider = new AzureEmbeddingProvider(Options(), client);

        var result = await provider.EmbedAsync("normalized input", default);

        Assert.Equal("azure-text-embedding-3-small-1536-v1", provider.ModelVersion);
        Assert.Equal(AzureEmbeddingOptions.Dimensions, result.Length);
        Assert.Equal(new Uri("https://olga-openai.openai.azure.com/"), captured!.Endpoint);
        Assert.Equal("olga-text-embedding-3-small", captured.DeploymentName);
        Assert.Equal(AzureEmbeddingOptions.RequiredModelName, captured.ModelName);
        Assert.Equal(AzureEmbeddingOptions.Dimensions, captured.Dimensions);
    }

    [Fact]
    public async Task Rejects_wrong_dimension_or_non_finite_provider_responses()
    {
        var wrongDimensions = new AzureEmbeddingProvider(Options(), new StubClient((_, _) => Task.FromResult(new float[128])));
        var nonFinite = new float[AzureEmbeddingOptions.Dimensions];
        nonFinite[10] = float.NaN;
        var invalidValue = new AzureEmbeddingProvider(Options(), new StubClient((_, _) => Task.FromResult(nonFinite)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => wrongDimensions.EmbedAsync("input", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => invalidValue.EmbedAsync("input", default));
    }

    [Fact]
    public async Task Preserves_caller_cancellation_and_enforces_timeout()
    {
        var client = new StubClient(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new float[AzureEmbeddingOptions.Dimensions];
        });
        var provider = new AzureEmbeddingProvider(Options(timeout: TimeSpan.FromMilliseconds(30)), client);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.EmbedAsync("input", cancelled.Token));
        await Assert.ThrowsAsync<TimeoutException>(() => provider.EmbedAsync("input", default));
    }

    [Fact]
    public async Task Retries_throttling_and_transient_failures_with_a_bound()
    {
        var calls = 0;
        var delays = 0;
        var client = new StubClient((_, _) =>
        {
            calls++;
            if (calls == 1) throw new AzureEmbeddingRequestException(429, "throttled");
            if (calls == 2) throw new AzureEmbeddingRequestException(503, "unavailable");
            return Task.FromResult(new float[AzureEmbeddingOptions.Dimensions]);
        });
        var provider = new AzureEmbeddingProvider(
            Options(maxRetries: 2),
            client,
            (_, _) => { delays++; return Task.CompletedTask; });

        await provider.EmbedAsync("input", default);

        Assert.Equal(3, calls);
        Assert.Equal(2, delays);
    }

    [Fact]
    public async Task Does_not_retry_non_transient_provider_failures()
    {
        var calls = 0;
        var provider = new AzureEmbeddingProvider(
            Options(),
            new StubClient((_, _) =>
            {
                calls++;
                throw new AzureEmbeddingRequestException(400, "invalid response");
            }),
            (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<AzureEmbeddingRequestException>(() => provider.EmbedAsync("input", default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Stops_after_the_configured_transient_retry_limit()
    {
        var calls = 0;
        var provider = new AzureEmbeddingProvider(
            Options(maxRetries: 1),
            new StubClient((_, _) =>
            {
                calls++;
                throw new AzureEmbeddingRequestException(503, "unavailable");
            }),
            (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<AzureEmbeddingRequestException>(() => provider.EmbedAsync("input", default));
        Assert.Equal(2, calls);
    }

    private static AzureEmbeddingOptions Options(TimeSpan? timeout = null, int maxRetries = 3) => new()
    {
        Endpoint = new Uri("https://olga-openai.openai.azure.com/"),
        DeploymentName = "olga-text-embedding-3-small",
        ModelVersion = "azure-text-embedding-3-small-1536-v1",
        Timeout = timeout ?? TimeSpan.FromSeconds(5),
        MaxRetries = maxRetries
    };

    private sealed class StubClient(Func<AzureEmbeddingRequest, CancellationToken, Task<float[]>> handler) : IAzureEmbeddingClient
    {
        public Task<float[]> GenerateAsync(AzureEmbeddingRequest request, CancellationToken ct) => handler(request, ct);
    }
}
