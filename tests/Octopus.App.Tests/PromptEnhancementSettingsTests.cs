using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using Xunit;
using Octopus.Agents.PromptEnhancement;
using Octopus.App.AgentGui;
using Octopus.App.Composition;
using Octopus.App.Localization;
using Octopus.App.Settings;
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

    private static TKCode.Arc.Select OpenPage(PromptEnhancementSettingsViewModel vm)
    {
        var view = new PromptEnhancementPageView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return view.FindControl<TKCode.Arc.Select>("DefaultEndpointSelect")!;
    }

    [AvaloniaFact]
    public async Task Default_select_shows_the_saved_endpoint_and_follows_picks()
    {
        var (vm, settings, _, _) = Create();
        var select = OpenPage(vm);
        Assert.Equal(PromptEnhancementSources.SessionEndpointId, vm.DefaultEndpointId);
        Assert.Equal(PromptEnhancementSources.SessionEndpointId, select.SelectedValue);
        Assert.Equal("跟随智能体接口", select.SelectedText);

        await vm.AddCommand.ExecuteAsync(null);
        var card = Assert.Single(vm.Endpoints);
        card.Name = "Office";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(card.Id, vm.DefaultEndpointId);
        Assert.Equal("Office", select.SelectedText);
        Assert.True(select.SelectedIndex >= 0);

        select.SelectedValue = PromptEnhancementSources.SessionEndpointId;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(PromptEnhancementSources.SessionEndpointId, vm.DefaultEndpointId);
        Assert.Null(settings.Current.PromptEnhancement.DefaultEndpointId);
        Assert.Equal("跟随智能体接口", select.SelectedText);

        select.SelectedValue = card.Id;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(card.Id, settings.Current.PromptEnhancement.DefaultEndpointId);
        Assert.Equal("Office", select.SelectedText);
    }

    [AvaloniaFact]
    public async Task Deleting_the_default_endpoint_updates_the_select()
    {
        var (vm, _, _, _) = Create();
        var select = OpenPage(vm);
        await vm.AddCommand.ExecuteAsync(null);
        var card = Assert.Single(vm.Endpoints);
        Assert.Equal(card.Id, select.SelectedValue);

        await card.DeleteCommand.ExecuteAsync(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(PromptEnhancementSources.SessionEndpointId, vm.DefaultEndpointId);
        Assert.Equal("跟随智能体接口", select.SelectedText);
        Assert.True(select.SelectedIndex >= 0);
    }

    private static CodeBuddyModelCredentials Credentials(string json)
    {
        var home = Directory.CreateTempSubdirectory("octopus-codebuddy-").FullName;
        Directory.CreateDirectory(Path.Combine(home, ".codebuddy"));
        File.WriteAllText(Path.Combine(home, ".codebuddy", "models.json"), json);
        return new CodeBuddyModelCredentials(new Octopus.Plugins.Security.UserHome(home));
    }

    [Fact]
    public void CodeBuddy_user_model_resolves_to_its_own_gateway()
    {
        var reader = Credentials("""
            { "models": [
              { "id": "m1", "name": "Model One", "url": "https://openrouter.ai/api/v1/chat/completions", "apiKey": "sk-secret" },
              { "id": "m2", "url": "http://gateway.local/v1", "apiKey": "sk-two" }
            ] }
            """);

        var found = reader.Find("Model One");
        Assert.NotNull(found);
        Assert.Equal("https://openrouter.ai/api/v1", found!.BaseUrl);
        Assert.Equal("sk-secret", found.ApiKey);
        Assert.Equal("m1", found.Model);
        Assert.DoesNotContain("sk-secret", found.ToString());

        Assert.Equal("http://gateway.local/v1", reader.Find("m2")!.BaseUrl);
        Assert.Null(reader.Find("hy4flash-free"));
    }

    [Fact]
    public void CodeBuddy_models_without_a_key_or_valid_json_are_not_endpoints()
    {
        var reader = Credentials("{\"models\":[{\"id\":\"m1\",\"url\":\"https://x/v1\"}]}");
        Assert.Null(reader.Find("m1"));
        Assert.Null(Credentials("not json").Find("m1"));
    }

    public sealed class KeyChangeRecorder : IRecipient<PromptEnhancementEndpointsChanged>
    {
        public int Count { get; private set; }
        public void Receive(PromptEnhancementEndpointsChanged message) => Count++;
    }

    [AvaloniaFact]
    public async Task Saving_and_clearing_a_key_notifies_the_composer()
    {
        var (vm, _, _, _) = Create();
        await vm.AddCommand.ExecuteAsync(null);
        var card = Assert.Single(vm.Endpoints);
        var recorder = new KeyChangeRecorder();
        WeakReferenceMessenger.Default.Register<PromptEnhancementEndpointsChanged>(recorder);
        try
        {
            card.KeyDraft = "sk-key";
            await card.SaveKeyCommand.ExecuteAsync(null);
            Assert.Equal(1, recorder.Count);

            await card.ClearKeyCommand.ExecuteAsync(null);
            Assert.Equal(2, recorder.Count);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recorder);
        }
    }

    private sealed class StubEnhancer : IPromptEnhancer
    {
        public Func<PromptEnhancementEndpointView, Task<string>> Enhance { get; set; } =
            _ => Task.FromResult("OK");

        public Task<string> EnhanceAsync(PromptEnhancementEndpointView endpoint, string prompt, CancellationToken cancellationToken = default) =>
            Enhance(endpoint);

        public Task<IReadOnlyList<PromptEnhancementEndpointView>> ListAsync(PromptEnhancementSettings settings, string? dialect,
            IReadOnlyDictionary<string, string> sessionEnvironment, string? sessionModelId, CancellationToken cancellationToken = default,
            PromptEnhancementSessionCredentials? sessionCredentials = null) =>
            Task.FromResult<IReadOnlyList<PromptEnhancementEndpointView>>([]);

        public Task<PromptEnhancementEndpointView?> ResolveDefaultAsync(PromptEnhancementSettings settings, string? dialect,
            IReadOnlyDictionary<string, string> sessionEnvironment, string? sessionModelId, CancellationToken cancellationToken = default,
            PromptEnhancementSessionCredentials? sessionCredentials = null) =>
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
    public void Settings_has_prompt_enhancement_section_and_page_binds_to_its_view_model()
    {
        var vm = new Octopus.App.Settings.SettingsViewModel(AppHost.Services);
        var section = Assert.Single(vm.All, s => s.Key == "prompt-enhancement");
        Assert.Equal(AppLangResourceKind.SettingsNavPromptEnhancement, section.TitleKind);
        Assert.Contains(vm.Ai, s => s.Key == "prompt-enhancement");

        try
        {
            vm.SelectCommand.Execute(section);
            var page = Assert.IsType<PromptEnhancementSettingsViewModel>(section.Page);
            Assert.Same(section.Page, page);
            // The page view compiles against this model (compiled bindings fail at build time when a name is unknown).
            var view = new PromptEnhancementPageView { DataContext = page };
            Assert.Same(page, view.DataContext);
        }
        finally
        {
            vm.Dispose();
        }
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

    [AvaloniaFact]
    public async Task Test_connection_uses_the_same_defaults_as_the_enhance_button()
    {
        var (vm, _, secrets, enhancer) = Create();
        await vm.AddCommand.ExecuteAsync(null);
        var card = Assert.Single(vm.Endpoints);
        card.BaseUrl = "https://gw.example.com/v1";
        card.KeyDraft = "k";
        await card.SaveKeyCommand.ExecuteAsync(null);

        PromptEnhancementEndpointView? seen = null;
        enhancer.Enhance = view =>
        {
            seen = view;
            return Task.FromResult("OK");
        };
        await card.TestCommand.ExecuteAsync(null);

        Assert.NotNull(seen);
        Assert.False(string.IsNullOrWhiteSpace(seen!.Model)); // resolved to the provider default, as enhancement does
        Assert.Null(seen.SystemPrompt);                       // the built-in instructions then apply
        Assert.Equal(PromptEnhancementSources.Anthropic, seen.Protocol);
        Assert.False(card.TestFailed);
    }

    [AvaloniaFact]
    public async Task Typing_a_base_url_keeps_its_slashes()
    {
        var (vm, settings, _, _) = Create();
        await vm.AddCommand.ExecuteAsync(null);
        var card = Assert.Single(vm.Endpoints);

        foreach (var prefix in new[] { "h", "ht", "htt", "http", "https", "https:", "https:/", "https://", "https://x", "https://x/" })
        {
            card.BaseUrl = prefix;
        }

        Assert.Equal("https://x/", card.BaseUrl);
        Assert.Equal("https://x/", Assert.Single(settings.Current.PromptEnhancement.Endpoints).BaseUrl);
    }
}
