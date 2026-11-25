namespace Octopus.Core.Settings;

/// <summary>
/// The prompt enhancement feature's own AI endpoints, independent of the agents' models and credentials.
/// Keys are never stored here: they live in the system keystore under
/// <see cref="PromptEnhancementSources.SecretService"/> with the endpoint id as the name.
/// </summary>
public sealed class PromptEnhancementSettings
{
    /// <summary>User-configured endpoints, in the order they were added.</summary>
    public List<PromptEnhancementEndpoint> Endpoints { get; set; } = [];

    /// <summary>
    /// The endpoint the enhance button uses. Null, or <see cref="PromptEnhancementSources.SessionEndpointId"/>:
    /// the agent session's own credentials (the built-in "follow agent" entry).
    /// </summary>
    public string? DefaultEndpointId { get; set; }
}

/// <summary>One AI endpoint that rewrites prompts. The API key is in the keystore, not here.</summary>
public sealed class PromptEnhancementEndpoint
{
    /// <summary>Stable identifier (a GUID); names the keystore entry.</summary>
    public string Id { get; set; } = "";
    /// <summary>Shown in the settings list and the composer's menu; may be empty.</summary>
    public string Name { get; set; } = "";
    /// <summary><see cref="PromptEnhancementSources.Anthropic"/> (null too) or <see cref="PromptEnhancementSources.OpenAi"/>.</summary>
    public string? Protocol { get; set; }
    /// <summary>The API prefix (for example <c>https://api.example.com/v1</c>); null uses the protocol's official address.</summary>
    public string? BaseUrl { get; set; }
    /// <summary>The model id; null uses the protocol's default.</summary>
    public string? Model { get; set; }
    /// <summary>0–2; null leaves the provider default.</summary>
    public double? Temperature { get; set; }
    /// <summary>64–8192; null uses the built-in default.</summary>
    public int? MaxTokens { get; set; }
    /// <summary>Replaces the built-in enhancer instructions when set.</summary>
    public string? SystemPrompt { get; set; }
}

/// <summary>Values shared by the settings, the enhancer and the views.</summary>
public static class PromptEnhancementSources
{
    /// <summary>The keystore service that holds prompt enhancement keys; each endpoint id is a name inside it.</summary>
    public const string SecretService = "Octopus/prompt-enhancement";

    /// <summary>The built-in entry that uses the agent session's own API key and address.</summary>
    public const string SessionEndpointId = "session";

    public const string Anthropic = "anthropic";
    public const string OpenAi = "openai";

    public static bool IsKnownProtocol(string? value) =>
        value is null or Anthropic or OpenAi;
}
