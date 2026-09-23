using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.OpenApi;
using Npgsql;
using Olga.Nlp.Api;
using Olga.Nlp.Application;
using Olga.Nlp.Contracts;
using Olga.Nlp.Domain;
using Olga.Nlp.Infrastructure;
using Pgvector.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
const string memberIdHeader = "X-Member-Id";
const string idempotencyKeyHeader = "Idempotency-Key";
const string ifMatchHeader = "If-Match";
var defaultMemberId = builder.Configuration["Mvp:DefaultMemberId"] ?? "A123";
var includeExceptionDetails = builder.Configuration.GetValue<bool>("Diagnostics:IncludeExceptionDetails");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        // Resolve against the Swagger page's origin so Azure HTTPS is preserved.
        document.Servers = [new OpenApiServer { Url = "/" }];

        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) =>
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<MemberContextMetadata>().Any())
            AddHeaderParameter(operation, memberIdHeader, false, $"MVP caller member ID. Defaults to {defaultMemberId} when omitted.", 64);

        var relativePath = context.Description.RelativePath;
        if (context.Description.HttpMethod == "POST" && relativePath is not null && IsStateMutationPath(new PathString($"/{relativePath}")))
            AddHeaderParameter(operation, idempotencyKeyHeader, true, "Unique key for this logical mutation. Reuse the same key only when retrying the same request.", 128);

        if (metadata.OfType<IfMatchMetadata>().Any())
            AddHeaderParameter(operation, ifMatchHeader, false, "ETag returned by GET /v1/intents/{intentId}. Required when updating an existing intent.");

        return Task.CompletedTask;
    });
});
builder.Services.AddHealthChecks();
builder.Services.AddHttpsRedirection(options =>
{
    options.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
    options.HttpsPort = builder.Environment.IsDevelopment() ? 7043 : 443;
});

var connection = builder.Configuration.GetConnectionString("PostgreSql");
var useInMemory = string.IsNullOrWhiteSpace(connection);
if (useInMemory) builder.Services.AddDbContext<NlpDbContext>(o => o.UseInMemoryDatabase("olga-nlp-local"));
else
{
    var connectionBuilder = new NpgsqlConnectionStringBuilder(connection)
    {
        Pooling = true,
        MaxPoolSize = 25,
        CommandTimeout = 15,
        SslMode = SslMode.VerifyFull
    };
    builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionBuilder.ConnectionString);
        dataSourceBuilder.UseVector();
        return dataSourceBuilder.Build();
    });
    builder.Services.AddSingleton<PostgresTransactionGuardInterceptor>();
    builder.Services.AddDbContext<NlpDbContext>((services, options) => options
        .AddInterceptors(services.GetRequiredService<PostgresTransactionGuardInterceptor>())
        .UseNpgsql(services.GetRequiredService<NpgsqlDataSource>(),
            postgres => postgres.UseVector().CommandTimeout(15).EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null)));
}

builder.Services.AddSingleton<IPiiChecker, PiiChecker>();
builder.Services.AddSingleton<ITextNormalizer, TextNormalizer>();
if (string.Equals(builder.Configuration["EmbeddingProvider"], "Azure", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton(AzureEmbeddingOptions.Create(
        builder.Configuration["AzureOpenAI:Endpoint"],
        builder.Configuration["AzureOpenAI:DeploymentName"],
        builder.Configuration["AzureOpenAI:ModelVersion"],
        builder.Configuration["AzureOpenAI:ManagedIdentityClientId"],
        builder.Configuration["AzureOpenAI:TimeoutSeconds"],
        builder.Configuration["AzureOpenAI:MaxRetries"]));
    builder.Services.AddSingleton<IEmbeddingProvider, AzureEmbeddingProvider>();
    builder.Services.AddHostedService<AzureEmbeddingModelValidator>();
}
else
    builder.Services.AddSingleton<IEmbeddingProvider, FakeEmbeddingProvider>();

builder.Services.AddSingleton<IReciprocalScorer, ReciprocalScorer>();
builder.Services.AddSingleton<IExplanationGenerator, ExplanationGenerator>();
builder.Services.AddSingleton<IMatchRanker, MatchRanker>();
builder.Services.AddScoped<IIntentRepository, IntentRepository>();
builder.Services.AddScoped<ICandidateRepository, CandidateRepository>();
builder.Services.AddScoped<IRankingConfigRepository, RankingConfigRepository>();
builder.Services.AddScoped<IMatchRequestRepository, MatchRequestRepository>();
builder.Services.AddScoped<IMatchResultRepository, MatchResultRepository>();
builder.Services.AddScoped<IFeedbackRepository, FeedbackRepository>();
builder.Services.AddScoped<IEvaluationRepository, EvaluationRepository>();
builder.Services.AddScoped<IIntentService, IntentService>();
builder.Services.AddScoped<IMatchingService, MatchingService>();
builder.Services.AddScoped<IFeedbackService, FeedbackService>();
builder.Services.AddScoped<IEvaluationService, EvaluationService>();

var processingMode = builder.Configuration["EmbeddingProcessing:Mode"];
if (useInMemory || string.Equals(processingMode, "Inline", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddScoped<IIntentProcessingDispatcher, InlineIntentProcessingDispatcher>();
else
    builder.Services.AddScoped<IIntentProcessingDispatcher, QueuedIntentProcessingDispatcher>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseHsts();

// Azure Container Apps enforces HTTPS at its ingress. Other hosts, including
// local Development, are redirected by ASP.NET Core.
if (!app.Configuration.GetValue("Hosting:AzureContainerAppsIngress", false))
    app.UseHttpsRedirection();

app.UseSwaggerUI(options =>
{
    options.RoutePrefix = "swagger";
    options.SwaggerEndpoint("/openapi/v1.json", "OLGA NLP API v1");
    options.DocumentTitle = "OLGA NLP API";
});

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Correlation-Id"] = context.TraceIdentifier;
    try
    {
        if (context.Request.ContentLength is > 256_000)
        {
            await WriteError(context, 413, "PAYLOAD_TOO_LARGE", "The request payload is too large.");
            return;
        }

        var idempotencyKey = context.Request.Headers[idempotencyKeyHeader].ToString();
        if (IsStateMutation(context.Request) && (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128))
        {
            await WriteError(context, 400, "IDEMPOTENCY_KEY_REQUIRED", SafeMessage("IDEMPOTENCY_KEY_REQUIRED"));
            return;
        }

        await next();
    }
    catch (DomainConflictException exception) { await WriteError(context, 409, exception.Code, SafeMessage(exception.Code)); }
    catch (DbUpdateConcurrencyException) { await WriteError(context, 409, "RESOURCE_VERSION_CONFLICT", SafeMessage("RESOURCE_VERSION_CONFLICT")); }
    catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { await WriteError(context, 409, "RESOURCE_CONFLICT", SafeMessage("RESOURCE_CONFLICT")); }
    catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation }) { await WriteError(context, 409, "RESOURCE_REFERENCE_NOT_FOUND", SafeMessage("RESOURCE_REFERENCE_NOT_FOUND")); }
    catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres && IsTransientDatabaseState(postgres.SqlState)) { await WriteError(context, 503, "DATABASE_TRANSIENT_FAILURE", SafeMessage("DATABASE_TRANSIENT_FAILURE")); }
    catch (RetryLimitExceededException) { await WriteError(context, 503, "DATABASE_TRANSIENT_FAILURE", SafeMessage("DATABASE_TRANSIENT_FAILURE")); }
    catch (PostgresException exception) when (IsTransientDatabaseState(exception.SqlState)) { await WriteError(context, 503, "DATABASE_TRANSIENT_FAILURE", SafeMessage("DATABASE_TRANSIENT_FAILURE")); }
    catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation) { await WriteError(context, 409, "RESOURCE_CONFLICT", SafeMessage("RESOURCE_CONFLICT")); }
    catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation) { await WriteError(context, 409, "RESOURCE_REFERENCE_NOT_FOUND", SafeMessage("RESOURCE_REFERENCE_NOT_FOUND")); }
    catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CheckViolation) { await WriteError(context, 409, "RESOURCE_STATE_CONFLICT", SafeMessage("RESOURCE_STATE_CONFLICT")); }
    catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege) { await WriteError(context, 403, "RESOURCE_FORBIDDEN", SafeMessage("RESOURCE_FORBIDDEN")); }
    catch (PostgresException exception) when (exception.SqlState == "P0002") { await WriteError(context, 404, "RESOURCE_NOT_FOUND", SafeMessage("RESOURCE_NOT_FOUND")); }
    catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidParameterValue) { await WriteError(context, 400, "REQUEST_INVALID", SafeMessage("REQUEST_INVALID")); }
    catch (NpgsqlException exception) when (exception.IsTransient) { await WriteError(context, 503, "DATABASE_TRANSIENT_FAILURE", SafeMessage("DATABASE_TRANSIENT_FAILURE")); }
    catch (DomainNotFoundException exception) { await WriteError(context, 404, exception.Code, SafeMessage(exception.Code)); }
    catch (ArgumentException exception) { await WriteError(context, 400, exception.Message, SafeMessage(exception.Message)); }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Unhandled exception for {Method} {Path}; correlation ID {CorrelationId}", context.Request.Method, context.Request.Path, context.TraceIdentifier);
        await WriteError(context, 500, "INTERNAL_ERROR", exception.GetBaseException().Message, includeExceptionDetails ? exception : null);
    }
});

app.MapOpenApi();
app.MapGet("/", () => Results.Redirect("/swagger"));
app.MapHealthChecks("/health");
app.MapGet("/ready", async (NlpDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));

var memberV1 = app.MapGroup("/v1").WithMetadata(new MemberContextMetadata());

memberV1.MapPost("/intents", async (HttpContext context, IntentUpsertRequest request, IIntentService service, CancellationToken ct) =>
{
    var memberId = Member(context, defaultMemberId);
    var response = await service.SaveAsync(memberId, request, context.Request.Headers.IfMatch.FirstOrDefault(), ct);
    if (!string.IsNullOrWhiteSpace(response.ETag)) context.Response.Headers.ETag = response.ETag;
    return response.Status == "PROCESSING" ? Results.Accepted($"/v1/intents/{response.IntentId}", response) : Results.Ok(response);
}).WithMetadata(new IfMatchMetadata());

memberV1.MapGet("/intents/{intentId}", async (HttpContext context, string intentId, IIntentService service, CancellationToken ct) =>
{
    var memberId = Member(context, defaultMemberId);
    var response = await service.GetAsync(memberId, intentId, ct);
    if (response is null) return await ErrorResult(context, 404, "INTENT_NOT_FOUND");
    context.Response.Headers.ETag = response.ETag;
    return Results.Ok(response);
});

memberV1.MapPost("/match-requests", async (HttpContext context, MatchSearchRequest body, IMatchingService service, CancellationToken ct) =>
{
    var memberId = Member(context, defaultMemberId);
    var idempotencyKey = context.Request.Headers[idempotencyKeyHeader].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(idempotencyKey)) return await ErrorResult(context, 400, "IDEMPOTENCY_KEY_REQUIRED");
    if (!string.IsNullOrWhiteSpace(body.RequestId) && !string.Equals(body.RequestId, idempotencyKey, StringComparison.Ordinal)) return await ErrorResult(context, 409, "IDEMPOTENCY_KEY_MISMATCH");
    var request = body with { RequestId = idempotencyKey };
    if (!ValidMatchRequest(request)) return await ErrorResult(context, 400, "MATCH_REQUEST_INVALID");
    var response = await service.SearchAsync(memberId, request, ct);
    return response.Status == "PROCESSING" ? Results.Accepted($"/v1/match-requests/{response.RequestId}", response) : Results.Ok(response);
});

// Backward-compatible synchronous endpoint. New clients should use /v1/match-requests.
memberV1.MapPost("/matches/search", async (HttpContext context, MatchSearchRequest request, IMatchingService service, CancellationToken ct) =>
{
    var memberId = Member(context, defaultMemberId);
    var idempotencyKey = context.Request.Headers[idempotencyKeyHeader].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(request.RequestId) && !string.Equals(request.RequestId, idempotencyKey, StringComparison.Ordinal)) return await ErrorResult(context, 409, "IDEMPOTENCY_KEY_MISMATCH");
    if (!ValidMatchRequest(request)) return await ErrorResult(context, 400, "MATCH_REQUEST_INVALID");
    return Results.Ok(await service.SearchAsync(memberId, request, ct));
});

memberV1.MapGet("/match-requests/{requestId}", async (HttpContext context, string requestId, IMatchingService service, CancellationToken ct) =>
{
    var memberId = Member(context, defaultMemberId);
    var response = await service.GetAsync(memberId, requestId, ct);
    return response is null ? await ErrorResult(context, 404, "MATCH_REQUEST_NOT_FOUND") : Results.Ok(response);
});

memberV1.MapPost("/matches/{matchResultId:long}/feedback", async (HttpContext context, long matchResultId, FeedbackCreateRequest request, IFeedbackService service, CancellationToken ct) =>
{
    var memberId = Member(context, defaultMemberId);
    return Results.Created($"/v1/matches/{matchResultId}/feedback", await service.SaveAsync(matchResultId, memberId, request, ct));
});

memberV1.MapPost("/feedback", async (HttpContext context, FeedbackRequest request, IFeedbackService service, CancellationToken ct) =>
{
    var memberId = Member(context, defaultMemberId);
    return Results.Created("/v1/feedback", await service.SaveLegacyAsync(memberId, request, ct));
});

app.MapPost("/v1/matches/score-pair", async (ScorePairRequest request, IMatchingService service, CancellationToken ct) => Results.Ok(await service.ScorePairAsync(request, ct)));
app.MapPost("/v1/normalize", (NormalizeRequest request, ITextNormalizer normalizer) =>
{
    var value = normalizer.Normalize(request.Text, request.Language);
    return Results.Ok(new NormalizeResponse(value.Value, value.Language, value.Hash, value.ContainsPii));
});

app.MapPost("/v1/internal/evaluation-runs", async (HttpContext context, EvaluationRunRequest request, IEvaluationService service, CancellationToken ct) =>
{
    var idempotencyKey = context.Request.Headers[idempotencyKeyHeader].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(idempotencyKey)) return await ErrorResult(context, 400, "IDEMPOTENCY_KEY_REQUIRED");
    var response = await service.RunAsync(idempotencyKey, request, ct);
    return Results.Ok(response);
});

app.MapGet("/v1/internal/evaluation-runs/{evaluationRunId}", async (HttpContext context, string evaluationRunId, IEvaluationService service, CancellationToken ct) =>
{
    var response = await service.GetAsync(evaluationRunId, ct);
    return response is null ? await ErrorResult(context, 404, "EVALUATION_RUN_NOT_FOUND") : Results.Ok(response);
});

if (useInMemory) await LocalDevelopmentSeeder.SeedAsync(app.Services, CancellationToken.None);
app.Run();

static bool ValidMatchRequest(MatchSearchRequest request) =>
    !string.IsNullOrWhiteSpace(request.RequestId) && !string.IsNullOrWhiteSpace(request.IntentId) &&
    !string.IsNullOrWhiteSpace(request.ContextId) && request.Limit is >= 3 and <= 7 &&
    (request.Options?.Threshold is null or >= 0 and <= 1);

static bool IsStateMutation(HttpRequest request) => request.Method == "POST" && IsStateMutationPath(request.Path);

static bool IsStateMutationPath(PathString path) =>
    path.StartsWithSegments("/v1/intents") ||
    path.StartsWithSegments("/v1/match-requests") ||
    path.StartsWithSegments("/v1/matches/search") ||
    path.StartsWithSegments("/v1/feedback") ||
    path.Value?.Contains("/feedback", StringComparison.Ordinal) == true ||
    path.StartsWithSegments("/v1/internal/evaluation-runs");

static string Member(HttpContext context, string defaultMemberId)
{
    var memberId = context.Request.Headers[memberIdHeader].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(memberId)) return defaultMemberId;
    if (memberId.Length > 64) throw new ArgumentException("MEMBER_ID_INVALID");
    return memberId;
}

static void AddHeaderParameter(OpenApiOperation operation, string name, bool required, string description, int? maxLength = null)
{
    operation.Parameters ??= [];
    if (operation.Parameters.Any(parameter =>
            parameter.In == ParameterLocation.Header
            && string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))) return;

    operation.Parameters.Add(new OpenApiParameter
    {
        Name = name,
        In = ParameterLocation.Header,
        Required = required,
        Description = description,
        Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = maxLength }
    });
}

static bool IsTransientDatabaseState(string sqlState) => sqlState is
    PostgresErrorCodes.SerializationFailure or
    PostgresErrorCodes.DeadlockDetected or
    PostgresErrorCodes.LockNotAvailable or
    PostgresErrorCodes.QueryCanceled;

static async Task<IResult> ErrorResult(HttpContext context, int status, string code)
{
    await Task.CompletedTask;
    return Results.Json(new ApiError(code, SafeMessage(code), context.TraceIdentifier), statusCode: status, contentType: "application/problem+json");
}

static async Task WriteError(HttpContext context, int status, string code, string message, Exception? exception = null)
{
    if (context.Response.HasStarted) return;
    context.Response.StatusCode = status;
    context.Response.ContentType = "application/problem+json";
    await context.Response.WriteAsJsonAsync(new ApiError(code, message, context.TraceIdentifier, StackTrace: exception?.ToString()));
}

static string SafeMessage(string code) => code switch
{
    "IDEMPOTENCY_KEY_REQUIRED" => "An Idempotency-Key header is required.",
    "IDEMPOTENCY_KEY_REUSED" => "The idempotency key was already used for different inputs.",
    "IDEMPOTENCY_KEY_MISMATCH" => "The request ID and Idempotency-Key must match.",
    "IF_MATCH_REQUIRED" => "An If-Match header is required when updating an intent.",
    "RESOURCE_VERSION_CONFLICT" => "The resource changed since it was read.",
    "RESOURCE_REFERENCE_NOT_FOUND" => "A referenced resource does not exist.",
    "MEMBER_ID_INVALID" => "The member ID is invalid.",
    "INTENT_NOT_FOUND" => "The requested intent was not found.",
    "MATCH_REQUEST_NOT_FOUND" => "The requested match execution was not found.",
    "MATCH_RESULT_NOT_FOUND" => "The requested match result was not found.",
    "FEEDBACK_PII_DETECTED" => "Feedback text must not contain contact information.",
    "EVALUATION_RUN_NOT_FOUND" => "The requested evaluation run was not found.",
    "EVALUATION_DATASET_NOT_APPROVED" => "The evaluation dataset is not approved.",
    "EVALUATION_DATASET_EMPTY" => "The approved evaluation dataset has no test samples.",
    _ => "The request is invalid or cannot be completed in its current state."
};

public partial class Program { }
internal sealed class MemberContextMetadata { }
internal sealed class IfMatchMetadata { }
