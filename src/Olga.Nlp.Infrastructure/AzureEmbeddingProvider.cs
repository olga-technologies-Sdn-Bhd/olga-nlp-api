using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI.Embeddings;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace Olga.Nlp.Infrastructure;

public sealed class AzureEmbeddingOptions
{
    public const string RequiredModelName = "text-embedding-3-small";
    public const string ProviderName = "AZURE_OPENAI";
    public const int Dimensions = 1536;

    public required Uri Endpoint { get; init; }
    public required string DeploymentName { get; init; }
    public required string ModelVersion { get; init; }
    public string? ManagedIdentityClientId { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxRetries { get; init; } = 3;

    public static AzureEmbeddingOptions Create(
        string? endpoint,
        string? deploymentName,
        string? modelVersion,
        string? managedIdentityClientId,
        string? timeoutSeconds,
        string? maxRetries)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
            throw new InvalidOperationException("AzureOpenAI:Endpoint must be an absolute HTTPS URI.");
        if (!int.TryParse(timeoutSeconds ?? "30", out var parsedTimeoutSeconds))
            throw new InvalidOperationException("AzureOpenAI:TimeoutSeconds must be an integer.");
        if (!int.TryParse(maxRetries ?? "3", out var parsedMaxRetries))
            throw new InvalidOperationException("AzureOpenAI:MaxRetries must be an integer.");

        var options = new AzureEmbeddingOptions
        {
            Endpoint = endpointUri,
            DeploymentName = deploymentName ?? string.Empty,
            ModelVersion = modelVersion ?? string.Empty,
            ManagedIdentityClientId = managedIdentityClientId,
            Timeout = TimeSpan.FromSeconds(parsedTimeoutSeconds),
            MaxRetries = parsedMaxRetries
        };
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (!Endpoint.IsAbsoluteUri || !string.Equals(Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AzureOpenAI:Endpoint must be an absolute HTTPS URI.");
        if (string.IsNullOrWhiteSpace(DeploymentName) || DeploymentName.Length > 128)
            throw new InvalidOperationException("AzureOpenAI:DeploymentName is required and must be at most 128 characters.");
        if (string.IsNullOrWhiteSpace(ModelVersion) || ModelVersion.Length > 128)
            throw new InvalidOperationException("AzureOpenAI:ModelVersion is required and must match the nlp_model_version record.");
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("AzureOpenAI:TimeoutSeconds must be greater than zero and at most 120.");
        if (MaxRetries is < 0 or > 5)
            throw new InvalidOperationException("AzureOpenAI:MaxRetries must be between 0 and 5.");
    }
}

public sealed record AzureEmbeddingRequest(
    Uri Endpoint,
    string DeploymentName,
    string ModelName,
    int Dimensions,
    string Input);

public interface IAzureEmbeddingClient
{
    Task<float[]> GenerateAsync(AzureEmbeddingRequest request, CancellationToken ct);
}

public sealed class AzureEmbeddingClient : IAzureEmbeddingClient
{
    private readonly EmbeddingClient client;

    public AzureEmbeddingClient(AzureEmbeddingOptions options)
    {
        options.Validate();
        var identity = string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
            ? ManagedIdentityId.SystemAssigned
            : ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId);
        var credential = new ManagedIdentityCredential(identity);
        var clientOptions = new AzureOpenAIClientOptions
        {
            // Retry in AzureEmbeddingProvider so the attempt count and total timeout have one owner.
            RetryPolicy = new ClientRetryPolicy(0)
        };
        client = new AzureOpenAIClient(options.Endpoint, credential, clientOptions).GetEmbeddingClient(options.DeploymentName);
    }

    public async Task<float[]> GenerateAsync(AzureEmbeddingRequest request, CancellationToken ct)
    {
        try
        {
            ClientResult<OpenAIEmbedding> response = await client.GenerateEmbeddingAsync(
                request.Input,
                new EmbeddingGenerationOptions { Dimensions = request.Dimensions },
                ct);
            return response.Value.ToFloats().ToArray();
        }
        catch (ClientResultException exception)
        {
            // Do not retain service payloads in an exception that application logging may capture.
            throw new AzureEmbeddingRequestException(exception.Status, $"Azure OpenAI embedding request failed with status {exception.Status}.");
        }
    }
}

public sealed class AzureEmbeddingRequestException(int status, string message, Exception? inner = null) : Exception(message, inner)
{
    public int Status { get; } = status;
    public bool IsTransient => Status is 408 or 429 or 500 or 502 or 503 or 504;
}

public sealed class AzureEmbeddingProvider : Application.IEmbeddingProvider
{
    private readonly AzureEmbeddingOptions options;
    private readonly IAzureEmbeddingClient client;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public AzureEmbeddingProvider(AzureEmbeddingOptions options)
        : this(options, new AzureEmbeddingClient(options))
    {
    }

    public AzureEmbeddingProvider(
        AzureEmbeddingOptions options,
        IAzureEmbeddingClient client,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        options.Validate();
        this.options = options;
        this.client = client;
        this.delay = delay ?? Task.Delay;
    }

    public string ModelVersion => options.ModelVersion;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        using var timeout = new CancellationTokenSource(options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var request = new AzureEmbeddingRequest(
            options.Endpoint,
            options.DeploymentName,
            AzureEmbeddingOptions.RequiredModelName,
            AzureEmbeddingOptions.Dimensions,
            text);

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var vector = await client.GenerateAsync(request, linked.Token);
                    ValidateResponse(vector);
                    return vector;
                }
                catch (AzureEmbeddingRequestException exception) when (exception.IsTransient && attempt < options.MaxRetries)
                {
                    var backoff = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt));
                    await delay(backoff, linked.Token);
                }
            }
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The Azure OpenAI embedding request exceeded its configured timeout.", exception);
        }
    }

    private static void ValidateResponse(float[]? vector)
    {
        if (vector is null || vector.Length != AzureEmbeddingOptions.Dimensions || vector.Any(value => !float.IsFinite(value)))
            throw new InvalidOperationException("Azure OpenAI returned an invalid embedding vector.");
    }
}
