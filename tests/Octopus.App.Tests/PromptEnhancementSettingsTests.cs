using Avalonia.Headless.XUnit;
using Xunit;
using Octopus.Agents.PromptEnhancement;
using Octopus.App.Composition;
using Octopus.App.Settings.PromptEnhancement;
using Octopus.Core.Settings;
using Octopus.Platform;
using Octopus.Storage;

namespace Octopus.App.Tests;

public class PromptEnhancementSettingsTests
{
    [AvaloniaFact]
    public void View_model_answers_a_default_endpoint_query()

    {
        var (vm, _, _, _) = Create();
        Assert.False(vm.HasEndpoints);
    }
    private sealed class StubEnhancer : IPromptEnhancer
    {
        public Func<PromptEnhancementEndpointView, Task<string>> Enhance { get; set; } =
            _ => Task.FromResult("OK");

        public Task<string> EnhanceAsync(PromptEnhancementEndpointView endpoint, string prompt, CancellationToken cancellationToken = default) =>
            Enhance(endpoint);

        public Task<IReadOnlyList<PromptEnhancementEndpointView>> ListAsync(PromptEnhancementSettings settings, string? dialect,
            IReadOnlyDictionary<string, string> sessionEnvironment, string? sessionModelId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PromptEnhancementEndpointView>>([]);

        public Task<PromptEnhancementEndpointView?> ResolveDefaultAsync(PromptEnhancementSettings settings, string? dialect,
            IReadOnlyDictionary<string, string> sessionEnvironment, string? sessionModelId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PromptEnhancementEndpointView?>(null);
    }

    private static (PromptEnhancementSettingsViewModel Vm, ISettingsStore Settings, MemorySecretStore Secrets, StubEnhancer Enhancer) Create()
    {
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.PromptEnhancement = new PromptEnhancementSettings());
        var secrets = new MemorySecretStore();
        var enhancer = new StubEnhancer();
        return (new PromptEnhancementSettingsViewModel(settings, secrets, enhancer), settings, secrets, enhancer);
    }

    [AvaloniaFact]
    public async Task Adding_an_endpoint_persists_it_and_makes_it_default_when_first()
    {
        var (vm, settings, _, _) = Create();
        Assert.False(vm.HasEndpoints);

        await vm.AddCommand.ExecuteAsync(null);

        var saved = Assert.Single(settings.Current.PromptEnhancement.Endpoints);
        Assert.Equal(saved.Id, settings.Current.PromptEnhancement.DefaultEndpointId);
        var card = Assert.Single(vm.Endpoints);
        Assert.True(card.IsDefault);
        Assert.Equal(2, vm.DefaultChoices.Count); // "follow agent" + the new endpoint
        Assert.Equal(PromptEnhancementSources.SessionEndpointId, vm.DefaultChoices[0].Value);
    }

    [AvaloniaFact]
    public async Task Editing_a_field_saves_it_and_saving_a_key_writes_the_keystore_never_settings()
    {
        var (vm, settings, secrets, _) = Create();
        await vm.AddCommand.ExecuteAsync(null);
        var card = Assert.Single(vm.Endpoints);

        card.Name = "Office gateway";
        card.BaseUrl = "https://gw.example.com/v1/";
        card.Model = "flash";
        card.KeyDraft = "sk-secret-key";

        Assert.Equal("Office gateway", Assert.Single(settings.Current.PromptEnhancement.Endpoints).Name);
        Assert.Null(await secrets.GetAsync(PromptEnhancementSources.SecretService, card.Id));

        await card.SaveKeyCommand.ExecuteAsync(null);

        Assert.Equal("sk-secret-key", await secrets.GetAsync(PromptEnhancementSources.SecretService, card.Id));
        Assert.Equal("", card.KeyDraft);
        Assert.True(card.HasSavedKey);
        var json = System.Text.Json.JsonSerializer.Serialize(settings.Current);
        Assert.DoesNotContain("sk-secret-key", json);
    }

    [AvaloniaFact]
    public async Task Deleting_an_endpoint_removes_its_keystore_entry_and_clears_default()
    {
        var (vm, settings, secrets, _) = Create();
        await vm.AddCommand.ExecuteAsync(null);
        var card = Assert.Single(vm.Endpoints);
        card.KeyDraft = "k";
        await card.SaveKeyCommand.ExecuteAsync(null);

        await card.DeleteCommand.ExecuteAsync(null);

        Assert.Empty(settings.Current.PromptEnhancement.Endpoints);
        Assert.Null(settings.Current.PromptEnhancement.DefaultEndpointId);
        Assert.Null(await secrets.GetAsync(PromptEnhancementSources.SecretService, card.Id));
        Assert.Empty(vm.Endpoints);
        Assert.Single(vm.DefaultChoices);
    }

    [AvaloniaFact]
    public async Task Test_connection_reports_failures_and_success()
    {
        var (vm, settings, secrets, enhancer) = Create();
        await vm.AddCommand.ExecuteAsync(null);
        var card = Assert.Single(vm.Endpoints);
        card.BaseUrl = "https://gw.example.com/v1";
        card.KeyDraft = "k";
        await card.SaveKeyCommand.ExecuteAsync(null);

        enhancer.Enhance = _ => throw new PromptEnhancementException("接口返回 401：bad key");
        await card.TestCommand.ExecuteAsync(null);
        Assert.True(card.TestFailed);
        Assert.Contains("401", card.TestResult);

        enhancer.Enhance = _ => Task.FromResult("OK");
        await card.TestCommand.ExecuteAsync(null);
        Assert.False(card.TestFailed);
        Assert.False(string.IsNullOrWhiteSpace(card.TestResult));
    }
}
