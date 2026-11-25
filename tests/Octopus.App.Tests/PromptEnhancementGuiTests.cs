using Avalonia.Headless.XUnit;
using Octopus.Agents.PromptEnhancement;
using Octopus.App.AgentGui;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Settings;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>An enhancer the tests control: what it lists, what it returns, and when it finishes.</summary>
public sealed class FakePromptEnhancer : IPromptEnhancer
{
    public PromptEnhancementEndpointStatus SessionStatus { get; set; } = PromptEnhancementEndpointStatus.Ready;
    public string SessionKey { get; set; } = "sk-session";
    public Func<PromptEnhancementEndpointView, string, CancellationToken, Task<string>> Enhance { get; set; } =
        (_, prompt, _) => Task.FromResult($"[enhanced] {prompt}");

    public List<string> Calls { get; } = [];

    public Task<IReadOnlyList<PromptEnhancementEndpointView>> ListAsync(PromptEnhancementSettings settings, string? dialect,
        IReadOnlyDictionary<string, string> sessionEnvironment, string? sessionModelId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Build(settings));

    public Task<PromptEnhancementEndpointView?> ResolveDefaultAsync(PromptEnhancementSettings settings, string? dialect,
        IReadOnlyDictionary<string, string> sessionEnvironment, string? sessionModelId, CancellationToken cancellationToken = default)
    {
        var all = Build(settings);
        var id = settings.DefaultEndpointId ?? PromptEnhancementSources.SessionEndpointId;
        return Task.FromResult(all.FirstOrDefault(v => v.Id == id));
    }

    public Task<string> EnhanceAsync(PromptEnhancementEndpointView endpoint, string prompt, CancellationToken cancellationToken = default)
    {
        Calls.Add(prompt);
        return Enhance(endpoint, prompt, cancellationToken);
    }

    private IReadOnlyList<PromptEnhancementEndpointView> Build(PromptEnhancementSettings settings)
    {
        var defaultId = settings.DefaultEndpointId ?? PromptEnhancementSources.SessionEndpointId;
        var list = new List<PromptEnhancementEndpointView>
        {
            new(PromptEnhancementSources.SessionEndpointId, "Follow agent", PromptEnhancementSources.Anthropic,
                "https://api.anthropic.com", SessionStatus == PromptEnhancementEndpointStatus.Ready ? SessionKey : null, "m",
                null, null, null, SessionStatus, true, defaultId == PromptEnhancementSources.SessionEndpointId),
        };
        list.AddRange(settings.Endpoints.Select(e => new PromptEnhancementEndpointView(e.Id, e.Name, e.Protocol, e.BaseUrl,
            null, e.Model, null, null, null, PromptEnhancementEndpointStatus.MissingKey, false, e.Id == defaultId)));
        return list;
    }
}

public class PromptEnhancementGuiTests
{
    private static FakePromptEnhancer Enhancer => AppHost.Get<FakePromptEnhancer>();

    private static async Task<(AgentGuiTabViewModel Vm, AgentGuiTabView View)> OpenAsync(string agent = "claude")
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-prompt-enhance-").FullName;
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(tempDir, "prompt-enhance");
        var item = AppHost.Get<IItemOpener>().OpenAgent(agent)!;
        var view = Assert.IsType<AgentGuiTabView>(AppHost.Get<ITabContentRegistry>().GetOrCreate(project, item));
        var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);
        var window = new Avalonia.Controls.Window { Width = 920, Height = 640, Content = view };
        window.Show();
        await WaitUntilAsync(() => vm.State == AgentGuiState.Ready);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await WaitUntilAsync(() => vm.EnhancementEndpoints.Count > 0);
        return (vm, view);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    [AvaloniaFact]
    public async Task Empty_input_disables_and_filled_input_enhances_with_the_default_endpoint()
    {
        var (vm, _) = await OpenAsync();
        Assert.False(vm.CanEnhancePrompt);

        vm.InputText = "fix the bug";
        Assert.True(vm.CanEnhancePrompt);
        Assert.Contains("跟随智能体接口", vm.EnhancePromptTooltip);

        await vm.EnhancePromptCommand.ExecuteAsync(null);

        Assert.Equal("[enhanced] fix the bug", vm.InputText);
        Assert.Null(vm.PromptEnhancementError);
        Assert.False(vm.IsEnhancingPrompt);
        Assert.Single(Enhancer.Calls);
    }

    [AvaloniaFact]
    public async Task Failure_keeps_the_original_text_and_reports_the_error()
    {
        var (vm, _) = await OpenAsync();
        Enhancer.Enhance = (_, _, _) => throw new PromptEnhancementException("接口返回 500：boom");
        vm.InputText = "original text";

        await vm.EnhancePromptCommand.ExecuteAsync(null);

        Assert.Equal("original text", vm.InputText);
        Assert.Contains("500", vm.PromptEnhancementError);
        Assert.Contains("500", vm.EnhancePromptTooltip);
        Assert.False(vm.IsEnhancingPrompt);
    }

    [AvaloniaFact]
    public async Task Enhancing_does_not_overwrite_what_the_person_typed_meanwhile()
    {
        var (vm, _) = await OpenAsync();
        var release = new TaskCompletionSource<string>();
        Enhancer.Enhance = (_, _, _) => release.Task;
        vm.InputText = "first draft";

        var running = vm.EnhancePromptCommand.ExecuteAsync(null);
        Assert.True(vm.IsEnhancingPrompt);
        vm.InputText = "first draft, plus more";
        release.SetResult("[enhanced] first draft");
        await running;

        Assert.Equal("first draft, plus more", vm.InputText);
    }

    [AvaloniaFact]
    public async Task Clicking_while_enhancing_cancels_and_keeps_the_text()
    {
        var (vm, _) = await OpenAsync();
        Enhancer.Enhance = async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return "never";
        };
        vm.InputText = "keep me";

        var running = vm.EnhancePromptCommand.ExecuteAsync(null);
        Assert.True(vm.IsEnhancingPrompt);
        await vm.EnhancePromptCommand.ExecuteAsync(null); // second click cancels
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsEnhancingPrompt);
        Assert.Equal("keep me", vm.InputText);
        Assert.Null(vm.PromptEnhancementError);
    }

    [AvaloniaFact]
    public async Task A_default_endpoint_without_a_key_disables_the_button_with_a_reason()
    {
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s =>
        {
            s.PromptEnhancement.Endpoints = [new PromptEnhancementEndpoint { Id = "ep1", Name = "Office", BaseUrl = "https://x" }];
            s.PromptEnhancement.DefaultEndpointId = "ep1";
        });
        var (vm, _) = await OpenAsync();
        vm.InputText = "hello";

        Assert.False(vm.CanEnhancePrompt);
        Assert.Contains("密钥", vm.EnhancePromptTooltip);
    }

    [AvaloniaFact]
    public async Task Switching_the_default_endpoint_from_the_menu_changes_the_enhance_button()
    {
        var (vm, _) = await OpenAsync();
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.PromptEnhancement.Endpoints = [new PromptEnhancementEndpoint { Id = "ep1", Name = "Office" }]);

        vm.SetDefaultEnhancementEndpointCommand.Execute("ep1");
        Assert.Equal("ep1", settings.Current.PromptEnhancement.DefaultEndpointId);

        vm.SetDefaultEnhancementEndpointCommand.Execute(PromptEnhancementSources.SessionEndpointId);
        Assert.Null(settings.Current.PromptEnhancement.DefaultEndpointId);
    }
}
