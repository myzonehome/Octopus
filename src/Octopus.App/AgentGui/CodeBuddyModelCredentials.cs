using System.Text.Json.Nodes;
using Octopus.Agents.PromptEnhancement;
using Octopus.Plugins.Security;

namespace Octopus.App.AgentGui;

/// <summary>
/// Looks up a CodeBuddy user model's own endpoint so the prompt enhancer's "follow agent" entry can use the same
/// gateway and key the CodeBuddy session uses. Built-in models have no entry here and stay unavailable.
/// </summary>
public interface ICodeBuddyModelCredentials
{
    PromptEnhancementSessionCredentials? Find(string? modelId);
}

public sealed class CodeBuddyModelCredentials(UserHome home) : ICodeBuddyModelCredentials
{
    public PromptEnhancementSessionCredentials? Find(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var path = Path.Combine(home.Path, ".codebuddy", "models.json");
        if (!File.Exists(path)) return null;

        try
        {
            var models = JsonNode.Parse(File.ReadAllText(path))?["models"]?.AsArray();
            if (models is null) return null;
            foreach (var model in models)
            {
                var id = Text(model, "id");
                var name = Text(model, "name");
                if (!string.Equals(id, modelId, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, modelId, StringComparison.OrdinalIgnoreCase)) continue;

                var key = Text(model, "apiKey");
                var baseUrl = BaseUrl(Text(model, "url"));
                if (string.IsNullOrWhiteSpace(key) || baseUrl is null) return null;
                return new PromptEnhancementSessionCredentials(baseUrl, key, id ?? name!);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or InvalidOperationException or FormatException)
        {
            // A damaged or unreadable config is not a usable endpoint.
        }
        return null;
    }

    private static string? Text(JsonNode? model, string property) =>
        model?[property] is { } node ? node.GetValue<string>() : null;

    private static string? BaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var baseUrl = url.Trim().TrimEnd('/');
        const string chatSuffix = "/chat/completions";
        if (baseUrl.EndsWith(chatSuffix, StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^chatSuffix.Length];
        }
        return baseUrl.Length == 0 ? null : baseUrl;
    }
}
