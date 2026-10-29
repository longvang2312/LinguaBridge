using System.Net;
using System.Text;
using System.Text.Json;
using LinguaBridge.Infrastructure.External;
using LinguaBridge.Infrastructure.External.Llm;
using LinguaBridge.Modules.AiTutor.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinguaBridge.UnitTests.AiTutor;

/// <summary>Stands in for the network: records the request and answers from a script. Nothing real is called.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }
    public string? Body { get; private set; }
    public int Calls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Request = request;
        Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return respond(request);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static string Completion(string content, int? prompt = 100, int? completion = 40)
    {
        var usage = prompt is null ? "" : $$""","usage":{"prompt_tokens":{{prompt}},"completion_tokens":{{completion}},"total_tokens":{{prompt + completion}}}""";
        return $$"""{"id":"x","choices":[{"index":0,"message":{"role":"assistant","content":{{JsonSerializer.Serialize(content)}}},"finish_reason":"stop"}]{{usage}}}""";
    }
}

public class OpenAiLlmProviderTests
{
    private static readonly LlmRequest Request = new("RULES", "Hello tutor", string.Empty);

    private static (OpenAiLlmProvider Provider, StubHandler Handler) Create(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        OpenAiOptions? options = null)
    {
        options ??= new OpenAiOptions { ApiKey = "sk-test" };
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl) };
        return (new OpenAiLlmProvider(http, options, NullLogger<OpenAiLlmProvider>.Instance), handler);
    }

    [Fact]
    public async Task Sends_the_chat_completions_request_with_the_key_the_default_model_and_json_mode()
    {
        var (provider, handler) = Create(_ => StubHandler.Json(StubHandler.Completion("{}")));

        await provider.CompleteAsync(Request);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test", handler.Request.Headers.Authorization!.ToString());

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("gpt-4o-mini", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(600, body.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Messages_are_system_then_history_then_the_latest_user_turn()
    {
        var (provider, handler) = Create(_ => StubHandler.Json(StubHandler.Completion("{}")));
        var request = new LlmRequest("RULES", "latest", string.Empty, [new LlmTurn(true, "q1"), new LlmTurn(false, "a1")]);

        await provider.CompleteAsync(request);

        using var body = JsonDocument.Parse(handler.Body!);
        var messages = body.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => (m.GetProperty("role").GetString(), m.GetProperty("content").GetString()))
            .ToArray();
        Assert.Equal([("system", "RULES"), ("user", "q1"), ("assistant", "a1"), ("user", "latest")], messages);
    }

    [Fact]
    public async Task A_model_id_on_the_request_overrides_the_configured_model()
    {
        var (provider, handler) = Create(_ => StubHandler.Json(StubHandler.Completion("{}")));

        var response = await provider.CompleteAsync(Request with { ModelId = "gpt-4o" });

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("gpt-4o", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("OpenAI_gpt-4o", response.ModelProvider);
    }

    [Fact]
    public async Task Reads_the_reply_the_token_counts_and_prices_the_call()
    {
        var (provider, _) = Create(_ => StubHandler.Json(StubHandler.Completion("""{"reply_text":"Hi"}""", prompt: 1000, completion: 500)));

        var response = await provider.CompleteAsync(Request);

        Assert.Equal("""{"reply_text":"Hi"}""", response.ReplyText);
        Assert.Equal(1000, response.PromptTokens);
        Assert.Equal(500, response.CompletionTokens);
        // 1000 * 0.15/1M + 500 * 0.60/1M
        Assert.Equal(0.00045m, response.EstimatedCostUsd);
        Assert.Equal("OpenAI_gpt-4o-mini", response.ModelProvider);
    }

    [Fact]
    public async Task Custom_pricing_changes_the_cost()
    {
        var options = new OpenAiOptions { ApiKey = "k", Pricing = new TokenPricing(1m, 2m) };
        var (provider, _) = Create(_ => StubHandler.Json(StubHandler.Completion("x", 1_000_000, 1_000_000)), options);

        Assert.Equal(3m, (await provider.CompleteAsync(Request)).EstimatedCostUsd);
    }

    [Fact]
    public async Task Missing_usage_is_estimated_so_the_call_is_never_free_for_the_quota()
    {
        var (provider, _) = Create(_ => StubHandler.Json(StubHandler.Completion(new string('a', 400), prompt: null)));

        var response = await provider.CompleteAsync(Request);

        Assert.True(response.PromptTokens > 0);
        Assert.Equal(100, response.CompletionTokens);
    }

    [Fact]
    public async Task Without_an_api_key_it_fails_with_a_provider_error_and_sends_nothing()
    {
        var (provider, handler) = Create(_ => StubHandler.Json(StubHandler.Completion("x")), new OpenAiOptions());

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => provider.CompleteAsync(Request));

        Assert.Contains("OpenAI:ApiKey", ex.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task An_http_error_is_a_provider_error_that_does_not_leak_the_body(HttpStatusCode status)
    {
        var (provider, _) = Create(_ => StubHandler.Json("""{"error":{"message":"secret detail sk-live-123"}}""", status));

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => provider.CompleteAsync(Request));

        Assert.Contains(((int)status).ToString(), ex.Message);
        Assert.DoesNotContain("sk-live-123", ex.Message);
    }

    [Fact]
    public async Task A_network_failure_is_a_provider_error()
    {
        var (provider, _) = Create(_ => throw new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => provider.CompleteAsync(Request));

        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task A_timeout_is_a_provider_error()
    {
        var (provider, _) = Create(_ => throw new TaskCanceledException("timed out"));

        await Assert.ThrowsAsync<LlmProviderException>(() => provider.CompleteAsync(Request));
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_swallowed_as_a_provider_error()
    {
        var (provider, _) = Create(_ => throw new TaskCanceledException());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(Request, cts.Token));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"message":{"content":null}}]}""")]
    [InlineData("""{"choices":[{"message":{"content":"   "}}]}""")]
    [InlineData("""{"choices":[{"message":{"refusal":"no"}}]}""")]
    public async Task An_unreadable_or_empty_answer_is_a_provider_error(string body)
    {
        var (provider, _) = Create(_ => StubHandler.Json(body));

        await Assert.ThrowsAsync<LlmProviderException>(() => provider.CompleteAsync(Request));
    }

    [Fact]
    public void Options_are_read_from_configuration_with_gpt_4o_mini_as_default()
    {
        var empty = OpenAiOptions.From(new ConfigurationBuilder().Build());
        Assert.Equal("gpt-4o-mini", empty.Model);
        Assert.False(empty.IsConfigured);

        var configured = OpenAiOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenAI:ApiKey"] = " sk-1 ",
            ["OpenAI:Model"] = "gpt-4o",
            ["OpenAI:InputUsdPerMillion"] = "2.5",
        }).Build());
        Assert.True(configured.IsConfigured);
        Assert.Equal("sk-1", configured.ApiKey);
        Assert.Equal("gpt-4o", configured.Model);
        Assert.Equal(2.5m, configured.Pricing.InputUsdPerMillion);
        Assert.Equal(0.60m, configured.Pricing.OutputUsdPerMillion);
    }

    [Fact]
    public void A_malformed_number_in_configuration_is_refused()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OpenAI:MaxOutputTokens"] = "lots" }).Build();

        Assert.Throws<InvalidOperationException>(() => OpenAiOptions.From(configuration));
    }
}

public class AzureAndFallbackProviderTests
{
    private static readonly LlmRequest Request = new("RULES", "Hello tutor", "ignored-model");

    private static readonly AzureOpenAiOptions Azure = new()
    {
        Endpoint = "https://contoso.openai.azure.com/",
        ApiKey = "az-key",
        Deployment = "tutor-mini",
    };

    [Fact]
    public async Task Azure_calls_the_deployment_url_with_an_api_key_header_and_no_model_in_the_body()
    {
        var handler = new StubHandler(_ => StubHandler.Json(StubHandler.Completion("{}")));
        var provider = new AzureOpenAiLlmProvider(new HttpClient(handler), Azure, NullLogger<AzureOpenAiLlmProvider>.Instance);

        var response = await provider.CompleteAsync(Request);

        Assert.Equal(
            "https://contoso.openai.azure.com/openai/deployments/tutor-mini/chat/completions?api-version=2024-06-01",
            handler.Request!.RequestUri!.ToString());
        Assert.Equal("az-key", Assert.Single(handler.Request.Headers.GetValues("api-key")));
        Assert.Null(handler.Request.Headers.Authorization);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("model", out _));
        Assert.Equal("AzureOpenAI_tutor-mini", response.ModelProvider);
    }

    [Fact]
    public async Task Azure_without_full_configuration_fails_with_a_provider_error()
    {
        var handler = new StubHandler(_ => StubHandler.Json(StubHandler.Completion("{}")));
        var provider = new AzureOpenAiLlmProvider(new HttpClient(handler), new AzureOpenAiOptions { Endpoint = "https://x" }, NullLogger<AzureOpenAiLlmProvider>.Instance);

        await Assert.ThrowsAsync<LlmProviderException>(() => provider.CompleteAsync(Request));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task The_fallback_is_used_only_when_the_primary_fails()
    {
        var primary = new ScriptedLlm();
        var secondary = new ScriptedLlm { Respond = _ => new LlmResponse("from azure", 1, 1, 0m, "AzureOpenAI_x") };
        var provider = new FallbackLlmProvider(primary, secondary, NullLogger<FallbackLlmProvider>.Instance);

        var healthy = await provider.CompleteAsync(Request);
        Assert.Equal(ScriptedLlm.JsonReply, healthy.ReplyText);
        Assert.Empty(secondary.Requests);

        primary.Fail = new LlmProviderException("openai down");
        var failedOver = await provider.CompleteAsync(Request);
        Assert.Equal("from azure", failedOver.ReplyText);
        Assert.Single(secondary.Requests);
    }

    [Fact]
    public async Task When_both_fail_the_secondarys_error_reaches_the_caller()
    {
        var provider = new FallbackLlmProvider(
            new ScriptedLlm { Fail = new LlmProviderException("primary") },
            new ScriptedLlm { Fail = new LlmProviderException("secondary") },
            NullLogger<FallbackLlmProvider>.Instance);

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => provider.CompleteAsync(Request));

        Assert.Equal("secondary", ex.Message);
    }

    [Fact]
    public async Task Errors_that_are_not_provider_failures_do_not_trigger_the_fallback()
    {
        var secondary = new ScriptedLlm();
        var provider = new FallbackLlmProvider(new ScriptedLlm { Fail = new InvalidOperationException("bug") }, secondary, NullLogger<FallbackLlmProvider>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync(Request));
        Assert.Empty(secondary.Requests);
    }

    [Fact]
    public void Registration_uses_openai_alone_by_default_and_adds_azure_as_fallback_when_configured()
    {
        Assert.IsType<OpenAiLlmProvider>(Resolve(new Dictionary<string, string?> { ["OpenAI:ApiKey"] = "k" }));

        var withAzure = Resolve(new Dictionary<string, string?>
        {
            ["OpenAI:ApiKey"] = "k",
            ["AzureOpenAI:Endpoint"] = "https://contoso.openai.azure.com",
            ["AzureOpenAI:ApiKey"] = "a",
            ["AzureOpenAI:Deployment"] = "d",
        });
        Assert.IsType<FallbackLlmProvider>(withAzure);
    }

    [Fact]
    public async Task The_registered_provider_without_any_key_fails_cleanly_instead_of_crashing_the_host()
    {
        var provider = Resolve(new Dictionary<string, string?>());

        await Assert.ThrowsAsync<LlmProviderException>(() => provider.CompleteAsync(Request));
    }

    private static ILlmProvider Resolve(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration).AddInfrastructureExternal(configuration);

        return services.BuildServiceProvider().GetRequiredService<ILlmProvider>();
    }
}
