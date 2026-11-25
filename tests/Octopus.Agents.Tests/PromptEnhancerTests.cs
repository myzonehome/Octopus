using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Octopus.Agents.PromptEnhancement;
using Octopus.Core.Settings;
using Octopus.Platform;

namespace Octopus.Agents.Tests;

public class PromptEnhancerTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage AnthropicText(string text) =>
        Json(HttpStatusCode.OK, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        }.ToJsonString());

    private static HttpResponseMessage OpenAiText(string text) =>
        Json(HttpStatusCode.OK, new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text } }),
        }.ToJsonString());

    private static (PromptEnhancer Enhancer, MemorySecretStore Secrets, StubHandler Handler) Create(
        Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        var secrets = new MemorySecretStore();
        var handler = new StubHandler(respond);
        return (new PromptEnhancer(secrets, new HttpClient(handler)), secrets, handler);
    }

    private static PromptEnhancementEndpoint Custom(string id = "ep1", string? protocol = null, string? model = "m") => new()
    {
        Id = id,
        Name = id,
        Protocol = protocol,
        BaseUrl = "https://gw.example.com/v1",
        Model = model,
    };

    [Fact]
    public async Task Session_endpoint_uses_anthropic_environment_and_reports_missing_key()
    {
        var (enhancer, _, _) = Create((_, _) => AnthropicText("x"));
        var env = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-ant", ["ANTHROPIC_DEFAULT_SONNET_MODEL"] = "sonnet-x" };

        var view = Assert.Single(await enhancer.ListAsync(new PromptEnhancementSettings(), "claude", env, null));
        Assert.True(view.IsSession);
        Assert.Equal(PromptEnhancementEndpointStatus.Ready, view.Status);
        Assert.Equal("sk-ant", view.ApiKey);
        Assert.Equal("https://api.anthropic.com", view.BaseUrl);
        Assert.Equal("sonnet-x", view.Model);

        var empty = Assert.Single(await enhancer.ListAsync(new PromptEnhancementSettings(), "claude", new Dictionary<string, string>(), null));
        Assert.Equal(PromptEnhancementEndpointStatus.MissingKey, empty.Status);
    }

    [Fact]
    public async Task Session_endpoint_reports_unsupported_dialect_for_codex_without_openai_key()
    {
        var (enhancer, _, _) = Create((_, _) => AnthropicText("x"));
        var gemini = Assert.Single(await enhancer.ListAsync(new PromptEnhancementSettings(), "gemini", new Dictionary<string, string>(), null));
        Assert.Equal(PromptEnhancementEndpointStatus.UnsupportedDialect, gemini.Status);

        var codexEnv = new Dictionary<string, string> { ["OPENAI_API_KEY"] = "sk-oai" };
        var codex = Assert.Single(await enhancer.ListAsync(new PromptEnhancementSettings(), "codex", codexEnv, "gpt-x"));
        Assert.Equal(PromptEnhancementEndpointStatus.Ready, codex.Status);
        Assert.Equal(PromptEnhancementSources.OpenAi, codex.Protocol);
        Assert.Equal("gpt-x", codex.Model);
    }

    [Fact]
    public async Task Custom_endpoint_missing_key_is_reported_and_never_falls_back()
    {
        var (enhancer, _, _) = Create((_, _) => AnthropicText("x"));
        var settings = new PromptEnhancementSettings
        {
            Endpoints = [Custom()],
            DefaultEndpointId = "ep1",
        };
        var env = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-session" };

        var list = await enhancer.ListAsync(settings, "claude", env, null);
        Assert.Equal(2, list.Count);
        Assert.False(Assert.Single(list, v => v.Id == "ep1").IsAvailable);
        var resolved = await enhancer.ResolveDefaultAsync(settings, "claude", env, null);
        Assert.Equal("ep1", resolved?.Id);
        Assert.Equal(PromptEnhancementEndpointStatus.MissingKey, resolved!.Status);
    }

    [Fact]
    public async Task Resolve_default_prefers_saved_default_then_session_then_first_ready()
    {
        var (enhancer, secrets, _) = Create((_, _) => AnthropicText("x"));
        await secrets.SetAsync(PromptEnhancementSources.SecretService, "ep1", "k1");
        await secrets.SetAsync(PromptEnhancementSources.SecretService, "ep2", "k2");
        var env = new Dictionary<string, string>();

        var saved = new PromptEnhancementSettings { Endpoints = [Custom("ep1"), Custom("ep2")], DefaultEndpointId = "ep2" };
        Assert.Equal("ep2", (await enhancer.ResolveDefaultAsync(saved, "claude", env, null))?.Id);

        var sessionFallback = new PromptEnhancementSettings { Endpoints = [Custom("ep1")] };
        var resolved = await enhancer.ResolveDefaultAsync(sessionFallback, "claude", new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "k" }, null);
        Assert.True(resolved!.IsSession);
    }

    [Fact]
    public async Task Anthropic_request_contains_system_prompt_messages_and_headers()
    {
        var (enhancer, _, handler) = Create((_, _) => AnthropicText("better"));
        var endpoint = new PromptEnhancementEndpointView(
            "ep1", "GW", PromptEnhancementSources.Anthropic, "https://gw.example.com/v1/", "sk-1", "m1",
            null, null, null, PromptEnhancementEndpointStatus.Ready, IsSession: false, IsDefault: true);

        var result = await enhancer.EnhanceAsync(endpoint, "make it better");

        Assert.Equal("better", result);
        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal("https://gw.example.com/v1/messages", request.RequestUri!.ToString());
        Assert.Equal("sk-1", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
        var json = JsonNode.Parse(body)!.AsObject();
        Assert.Equal("m1", json["model"]!.GetValue<string>());
        Assert.Equal("make it better", json["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Contains("prompt enhancer", json["system"]!.GetValue<string>());
        Assert.True(json["max_tokens"]!.GetValue<int>() > 0);
    }

    [Fact]
    public async Task Openai_request_uses_chat_completions_and_bearer()
    {
        var (enhancer, _, handler) = Create((_, _) => OpenAiText("better"));
        var endpoint = new PromptEnhancementEndpointView(
            "ep1", "GW", PromptEnhancementSources.OpenAi, "https://gw.example.com", "sk-1", "m1",
            null, null, null, PromptEnhancementEndpointStatus.Ready, IsSession: false, IsDefault: false);

        Assert.Equal("better", await enhancer.EnhanceAsync(endpoint, "hi"));

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal("https://gw.example.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("sk-1", request.Headers.Authorization.Parameter);
        var json = JsonNode.Parse(body)!.AsObject();
        Assert.Equal("system", json["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("hi", json["messages"]![1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Code_fence_and_surrounding_quotes_are_stripped_from_result()
    {
        var (enhancer, _, _) = Create((_, _) => AnthropicText("```markdown\nDo the thing.\n```"));
        var view = new PromptEnhancementEndpointView("e", "n", null, "https://x", "k", "m", null, null, null,
            PromptEnhancementEndpointStatus.Ready, false, false);
        Assert.Equal("Do the thing.", await enhancer.EnhanceAsync(view, "do the thing"));

        var (quoted, _, _) = Create((_, _) => AnthropicText("\"Quoted prompt\""));
        Assert.Equal("Quoted prompt", await quoted.EnhanceAsync(view, "x"));
    }

    [Fact]
    public async Task Empty_result_throws_PromptEnhancementException()
    {
        var (enhancer, _, _) = Create((_, _) => AnthropicText("   "));
        var view = new PromptEnhancementEndpointView("e", "n", null, "https://x", "k", "m", null, null, null,
            PromptEnhancementEndpointStatus.Ready, false, false);
        await Assert.ThrowsAsync<PromptEnhancementException>(() => enhancer.EnhanceAsync(view, "x"));
    }

    [Fact]
    public async Task Non_success_status_throws_with_status_code()
    {
        var (enhancer, _, _) = Create((_, _) => Json(HttpStatusCode.Unauthorized, "{\"error\":\"bad key\"}"));
        var view = new PromptEnhancementEndpointView("e", "n", null, "https://x", "k", "m", null, null, null,
            PromptEnhancementEndpointStatus.Ready, false, false);
        var ex = await Assert.ThrowsAsync<PromptEnhancementException>(() => enhancer.EnhanceAsync(view, "x"));
        Assert.Contains("401", ex.Message);
        Assert.Contains("bad key", ex.Message);
        Assert.DoesNotContain("sk-", ex.Message);
    }

    [Fact]
    public async Task Custom_system_prompt_temperature_and_max_tokens_override_defaults()
    {
        var (enhancer, _, handler) = Create((_, _) => AnthropicText("ok"));
        var view = new PromptEnhancementEndpointView("e", "n", null, "https://x", "k", "m", 0.3, 100, "Custom rules",
            PromptEnhancementEndpointStatus.Ready, false, false);
        await enhancer.EnhanceAsync(view, "x");
        var json = JsonNode.Parse(handler.Calls[0].Body)!.AsObject();
        Assert.Equal("Custom rules", json["system"]!.GetValue<string>());
        Assert.Equal(0.3, json["temperature"]!.GetValue<double>(), 3);
        Assert.Equal(100, json["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task Cancellation_propagates_as_OperationCanceled()
    {
        var (enhancer, _, _) = Create((_, _) => throw new OperationCanceledException());
        var view = new PromptEnhancementEndpointView("e", "n", null, "https://x", "k", "m", null, null, null,
            PromptEnhancementEndpointStatus.Ready, false, false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enhancer.EnhanceAsync(view, "x", cancelled.Token));
    }
}
