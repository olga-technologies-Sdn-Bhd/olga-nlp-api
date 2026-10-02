using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Olga.Nlp.Contracts;

namespace Olga.Nlp.ContractTests;

public sealed class MatchContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web) { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower };
    private readonly HttpClient client;
    public MatchContractTests(WebApplicationFactory<Program> factory) => client = factory.CreateClient();

    [Fact]
    public async Task Valid_local_search_returns_versioned_explainable_match()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/matches/search") { Content = JsonContent.Create(new MatchSearchRequest("contract-1", "a-want", "event-001", 7), options: Json) };
        request.Headers.Add("Idempotency-Key", "contract-1");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<MatchSearchResponse>(Json);
        Assert.NotNull(body);
        Assert.Contains(body.Matches, x => x.MemberId == "B456" && x.ReasonCodes.Count > 0);
        Assert.DoesNotContain(body.Matches, x => x.MemberId == "D111");
        Assert.Equal("fake-embedding-v2", body.ModelVersion);
    }

    [Fact]
    public async Task Match_request_endpoint_honors_idempotency_and_status_lookup()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/match-requests")
        {
            Content = JsonContent.Create(new MatchSearchRequest("contract-2", "a-want", "event-001", 7), options: Json)
        };
        request.Headers.Add("Idempotency-Key", "contract-2");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<MatchSearchResponse>(Json);

        var status = await client.GetFromJsonAsync<MatchRequestStatusResponse>("/v1/match-requests/contract-2", Json);

        Assert.NotNull(created);
        Assert.Equal("COMPLETED", created.Status);
        Assert.NotNull(status);
        Assert.Equal("COMPLETED", status.Status);
        Assert.Equal(created.Matches.Count, status.Matches.Count);
        Assert.All(status.Matches, x => Assert.True(x.MatchResultId > 0));
    }

    [Fact]
    public async Task OpenApi_server_resolves_against_the_https_browser_origin()
    {
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var server = Assert.Single(document.RootElement.GetProperty("servers").EnumerateArray());
        Assert.Equal("/", server.GetProperty("url").GetString());
    }

    [Fact]
    public async Task OpenApi_describes_all_operations_as_anonymous()
    {
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

        Assert.False(
            document.RootElement.TryGetProperty("components", out var components)
            && components.TryGetProperty("securitySchemes", out _));
        var operation = document.RootElement.GetProperty("paths").GetProperty("/v1/normalize").GetProperty("post");
        Assert.False(operation.TryGetProperty("security", out _));
    }

    [Fact]
    public async Task OpenApi_exposes_required_client_headers_on_the_correct_operations()
    {
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

        var intentRead = Operation(document, "/v1/intents/{intentId}", "get");
        AssertHeader(intentRead, "X-Member-Id", required: false, maxLength: 64);
        AssertNoHeader(intentRead, "Idempotency-Key");

        var intentWrite = Operation(document, "/v1/intents", "post");
        AssertHeader(intentWrite, "X-Member-Id", required: false, maxLength: 64);
        AssertHeader(intentWrite, "Idempotency-Key", required: true, maxLength: 128);
        AssertHeader(intentWrite, "If-Match", required: false);

        var evaluation = Operation(document, "/v1/internal/evaluation-runs", "post");
        AssertHeader(evaluation, "Idempotency-Key", required: true, maxLength: 128);
        AssertNoHeader(evaluation, "X-Member-Id");

        var normalize = Operation(document, "/v1/normalize", "post");
        AssertNoHeader(normalize, "X-Member-Id");
        AssertNoHeader(normalize, "Idempotency-Key");
    }

    [Fact]
    public async Task Member_routes_reject_an_oversized_member_header()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/intents/a-want");
        request.Headers.Add("X-Member-Id", new string('x', 65));

        using var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal("MEMBER_ID_INVALID", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Api_is_callable_without_a_service_credential()
    {
        using var response = await client.PostAsJsonAsync("/v1/normalize", new NormalizeRequest("hello", null));
        response.EnsureSuccessStatusCode();
    }

    private static System.Text.Json.JsonElement Operation(System.Text.Json.JsonDocument document, string path, string method) =>
        document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

    private static void AssertHeader(System.Text.Json.JsonElement operation, string name, bool required, int? maxLength = null)
    {
        var header = operation.GetProperty("parameters").EnumerateArray()
            .Single(parameter => parameter.GetProperty("in").GetString() == "header"
                && string.Equals(parameter.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));
        var actualRequired = header.TryGetProperty("required", out var requiredProperty) && requiredProperty.GetBoolean();
        Assert.Equal(required, actualRequired);
        if (maxLength is not null)
            Assert.Equal(maxLength.Value, header.GetProperty("schema").GetProperty("maxLength").GetInt32());
    }

    private static void AssertNoHeader(System.Text.Json.JsonElement operation, string name)
    {
        if (!operation.TryGetProperty("parameters", out var parameters)) return;
        Assert.DoesNotContain(parameters.EnumerateArray(), parameter =>
            parameter.GetProperty("in").GetString() == "header"
            && string.Equals(parameter.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));
    }
}
