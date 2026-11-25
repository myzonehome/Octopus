using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Octopus.Core.Settings;
using Octopus.Platform;

namespace Octopus.Agents.PromptEnhancement;

/// <summary>Whether an endpoint can be called, and if not, what it is missing.</summary>
public enum PromptEnhancementEndpointStatus
{
    Ready,
    /// <summary>The agent's session has no API key Octopus can use (a subscription sign-in, or an unsupported agent).</summary>
    SessionUnavailable,
    MissingBaseUrl,
    MissingKey,
    /// <summary>The session's agent does not speak a protocol Octopus can call directly.</summary>
    UnsupportedDialect,
}

/// <summary>One endpoint the enhance button can use, with the key already resolved for the call.</summary>
public sealed record PromptEnhancementEndpointView(
    string Id,
    string Name,
    string? Protocol,
    string? BaseUrl,
    string? ApiKey,
    string? Model,
    double? Temperature,
    int? MaxTokens,
    string? SystemPrompt,
    PromptEnhancementEndpointStatus Status,
    bool IsSession,
    bool IsDefault)
{
    public bool IsAvailable => Status == PromptEnhancementEndpointStatus.Ready;
}

public sealed class PromptEnhancementException(string message) : Exception(message);

/// <summary>Resolves the configured endpoints (and the built-in one that follows the agent) and rewrites a prompt.</summary>
public interface IPromptEnhancer
{
    /// <summary>Every endpoint, the built-in "follow agent" one first; the default endpoint is also flagged.</summary>
    Task<IReadOnlyList<PromptEnhancementEndpointView>> ListAsync(
        PromptEnhancementSettings settings,
        string? dialect,
        IReadOnlyDictionary<string, string> sessionEnvironment,
        string? sessionModelId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The endpoint the button uses: the saved default; without one, the built-in "follow agent" entry.
    /// A saved default that is not usable stays selected (and unavailable) rather than silently falling back.
    /// </summary>
    Task<PromptEnhancementEndpointView?> ResolveDefaultAsync(
        PromptEnhancementSettings settings,
        string? dialect,
        IReadOnlyDictionary<string, string> sessionEnvironment,
        string? sessionModelId,
        CancellationToken cancellationToken = default);

    /// <summary>Rewrites the prompt; throws <see cref="PromptEnhancementException"/> on failure.</summary>
    Task<string> EnhanceAsync(
        PromptEnhancementEndpointView endpoint,
        string prompt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Calls Anthropic Messages or OpenAI Chat Completions directly with <see cref="HttpClient"/> and <see cref="JsonNode"/>:
/// no SDK, NativeAOT safe. Keys come from the keystore, never from settings or logs.
/// </summary>
public sealed class PromptEnhancer : IPromptEnhancer
{
    public const string SessionName = "Follow agent";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    public const int DefaultMaxTokens = 2048;

    private const string DefaultAnthropicModel = "claude-sonnet-5-5";
    private const string DefaultOpenAiModel = "gpt-5.5";
    private const string AnthropicUrl = "https://api.anthropic.com";
    private const string OpenAiUrl = "https://api.openai.com/v1";
    private const string AnthropicVersion = "2023-06-01";

    internal const string DefaultSystemPrompt =
        "You are a prompt enhancer. Rewrite the user's prompt so that an AI coding agent can act on it with fewer " +
        "ambiguities. Preserve the original intent, language, tone and every explicit constraint (paths, names, " +
        "limits, requirements). Do not invent requirements or facts. Make implicit structure explicit: goal, " +
        "relevant context, expected outcome, constraints. Keep it concise. If the input is a slash command or " +
        "mentions @file paths, keep them verbatim. Reply with the enhanced prompt only: no preamble, no " +
        "explanation, no surrounding quotes or code fences.";

    private readonly ISecretStore _secrets;
    private readonly HttpClient _http;

    public PromptEnhancer(ISecretStore secrets, HttpClient? httpClient = null)
    {
        _secrets = secrets;
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<IReadOnlyList<PromptEnhancementEndpointView>> ListAsync(
        PromptEnhancementSettings settings,
        string? dialect,
        IReadOnlyDictionary<string, string> sessionEnvironment,
        string? sessionModelId,
        CancellationToken cancellationToken = default)
    {
        var defaultId = settings.DefaultEndpointId ?? PromptEnhancementSources.SessionEndpointId;
        var list = new List<PromptEnhancementEndpointView>
        {
            ResolveSession(dialect, sessionEnvironment, sessionModelId, isDefault: defaultId == PromptEnhancementSources.SessionEndpointId),
        };

        foreach (var endpoint in settings.Endpoints)
        {
            var key = _secrets.IsAvailable
                ? await _secrets.GetAsync(PromptEnhancementSources.SecretService, endpoint.Id, cancellationToken).ConfigureAwait(false)
                : null;
            list.Add(ResolveCustom(endpoint, key, isDefault: endpoint.Id == defaultId));
        }

        return list;
    }

    public async Task<PromptEnhancementEndpointView?> ResolveDefaultAsync(
        PromptEnhancementSettings settings,
        string? dialect,
        IReadOnlyDictionary<string, string> sessionEnvironment,
        string? sessionModelId,
        CancellationToken cancellationToken = default)
    {
        var defaultId = settings.DefaultEndpointId ?? PromptEnhancementSources.SessionEndpointId;
        var list = await ListAsync(settings, dialect, sessionEnvironment, sessionModelId, cancellationToken).ConfigureAwait(false);
        return list.FirstOrDefault(v => v.Id == defaultId);
    }

    private static PromptEnhancementEndpointView ResolveSession(
        string? dialect, IReadOnlyDictionary<string, string> env, string? sessionModelId, bool isDefault)
    {
        PromptEnhancementEndpointView Unavailable(string? model, PromptEnhancementEndpointStatus status, string? protocol = null) =>
            new(PromptEnhancementSources.SessionEndpointId, SessionName, protocol, null, null, model, null, null, null,
                status, IsSession: true, isDefault);

        if (IsClaudeDialect(dialect))
        {
            var baseUrl = FirstNonEmpty(Get(env, "ANTHROPIC_BASE_URL")) ?? AnthropicUrl;
            var apiKey = FirstNonEmpty(Get(env, "ANTHROPIC_API_KEY"));
            var authToken = FirstNonEmpty(Get(env, "ANTHROPIC_AUTH_TOKEN"));
            var model = FirstNonEmpty(sessionModelId, Get(env, "ANTHROPIC_MODEL"), Get(env, "ANTHROPIC_DEFAULT_SONNET_MODEL"));
            if (apiKey is null && authToken is null)
            {
                return Unavailable(model, PromptEnhancementEndpointStatus.MissingKey, PromptEnhancementSources.Anthropic);
            }
            // An auth token is a gateway's bearer credential; it goes in Authorization instead of x-api-key.
            return new(PromptEnhancementSources.SessionEndpointId, SessionName, PromptEnhancementSources.Anthropic,
                baseUrl, apiKey ?? $"bearer:{authToken}", model, null, null, null,
                PromptEnhancementEndpointStatus.Ready, IsSession: true, isDefault);
        }

        if (IsCodexDialect(dialect))
        {
            var baseUrl = FirstNonEmpty(Get(env, "OPENAI_BASE_URL")) ?? OpenAiUrl;
            var apiKey = FirstNonEmpty(Get(env, "OPENAI_API_KEY"));
            var model = FirstNonEmpty(sessionModelId, Get(env, "OPENAI_MODEL"));
            return apiKey is null
                ? Unavailable(model, PromptEnhancementEndpointStatus.MissingKey, PromptEnhancementSources.OpenAi)
                : new(PromptEnhancementSources.SessionEndpointId, SessionName, PromptEnhancementSources.OpenAi,
                    baseUrl, apiKey, model, null, null, null, PromptEnhancementEndpointStatus.Ready, IsSession: true, isDefault);
        }

        return Unavailable(null, PromptEnhancementEndpointStatus.UnsupportedDialect);
    }

    private static PromptEnhancementEndpointView ResolveCustom(PromptEnhancementEndpoint endpoint, string? key, bool isDefault)
    {
        var protocol = endpoint.Protocol ?? PromptEnhancementSources.Anthropic;
        var status = string.IsNullOrWhiteSpace(endpoint.BaseUrl) ? PromptEnhancementEndpointStatus.MissingBaseUrl
            : string.IsNullOrWhiteSpace(key) ? PromptEnhancementEndpointStatus.MissingKey
            : PromptEnhancementEndpointStatus.Ready;
        var model = endpoint.Model ?? (protocol == PromptEnhancementSources.OpenAi ? DefaultOpenAiModel : DefaultAnthropicModel);
        return new(endpoint.Id, endpoint.Name, protocol, endpoint.BaseUrl, key, model,
            endpoint.Temperature, endpoint.MaxTokens, endpoint.SystemPrompt, status, IsSession: false, isDefault);
    }

    private static bool IsClaudeDialect(string? dialect) =>
        string.Equals(dialect, "claude", StringComparison.OrdinalIgnoreCase)
        || string.Equals(dialect, "codebuddy", StringComparison.OrdinalIgnoreCase);

    private static bool IsCodexDialect(string? dialect) =>
        string.Equals(dialect, "codex", StringComparison.OrdinalIgnoreCase);

    public async Task<string> EnhanceAsync(
        PromptEnhancementEndpointView endpoint, string prompt, CancellationToken cancellationToken = default)
    {
        if (!endpoint.IsAvailable || endpoint.BaseUrl is null)
        {
            throw new PromptEnhancementException("接口不可用。");
        }

        var isOpenAi = endpoint.Protocol == PromptEnhancementSources.OpenAi;
        var system = endpoint.SystemPrompt ?? DefaultSystemPrompt;
        var body = isOpenAi ? BuildOpenAiBody(endpoint, system, prompt) : BuildAnthropicBody(endpoint, system, prompt);
        var baseUrl = endpoint.BaseUrl.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, isOpenAi ? OpenAiEndpoint(baseUrl) : AnthropicEndpoint(baseUrl))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (isOpenAi)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        }
        else if (endpoint.ApiKey!.StartsWith("bearer:", StringComparison.Ordinal))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey["bearer:".Length..]);
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("x-api-key", endpoint.ApiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(e.Message, e, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new PromptEnhancementException("请求超时。");
        }
        catch (HttpRequestException e)
        {
            throw new PromptEnhancementException($"无法连接接口：{e.Message}");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = text.Length > 300 ? text[..300] : text;
                throw new PromptEnhancementException($"接口返回 {(int)response.StatusCode}：{detail}");
            }

            var result = Clean(isOpenAi ? ReadOpenAiText(text) : ReadAnthropicText(text));
            return result.Length > 0 ? result : throw new PromptEnhancementException("模型返回了空结果。");
        }
    }

    private static JsonObject BuildAnthropicBody(PromptEnhancementEndpointView endpoint, string system, string prompt)
    {
        var json = new JsonObject
        {
            ["model"] = endpoint.Model,
            ["max_tokens"] = endpoint.MaxTokens ?? DefaultMaxTokens,
            ["system"] = system,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
        };
        if (endpoint.Temperature is { } temperature) json["temperature"] = temperature;
        return json;
    }

    private static JsonObject BuildOpenAiBody(PromptEnhancementEndpointView endpoint, string system, string prompt)
    {
        var json = new JsonObject
        {
            ["model"] = endpoint.Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = prompt }),
        };
        if (endpoint.MaxTokens is { } maxTokens) json["max_tokens"] = maxTokens;
        if (endpoint.Temperature is { } temperature) json["temperature"] = temperature;
        return json;
    }

    private static string AnthropicEndpoint(string baseUrl) =>
        baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl + "/messages" : baseUrl + "/v1/messages";

    private static string OpenAiEndpoint(string baseUrl) =>
        baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl + "/chat/completions" : baseUrl + "/v1/chat/completions";

    private static string ReadAnthropicText(string json)
    {
        try
        {
            var content = JsonNode.Parse(json)?["content"]?.AsArray();
            if (content is null) return "";
            return string.Concat(content
                .Where(block => block?["type"]?.GetValue<string>() == "text")
                .Select(block => block!["text"]?.GetValue<string>() ?? ""));
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            throw new PromptEnhancementException($"无法解析接口返回：{e.Message}");
        }
    }

    private static string ReadOpenAiText(string json)
    {
        try
        {
            return JsonNode.Parse(json)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            throw new PromptEnhancementException($"无法解析接口返回：{e.Message}");
        }
    }

    /// <summary>Cuts a code fence or a matching pair of quotes the model wrapped the whole answer in.</summary>
    internal static string Clean(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal) && trimmed.EndsWith("```", StringComparison.Ordinal) && trimmed.Length >= 6)
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0)
            {
                trimmed = trimmed[(firstNewline + 1)..^3].Trim();
            }
        }
        if (trimmed.Length >= 2 && trimmed[0] == trimmed[^1] && trimmed[0] is '"' or '\'')
        {
            trimmed = trimmed[1..^1].Trim();
        }
        return trimmed;
    }

    private static string? Get(IReadOnlyDictionary<string, string> env, string name) =>
        env.TryGetValue(name, out var value) ? value : null;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
