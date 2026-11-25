using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents.PromptEnhancement;
using Octopus.App.Localization;
using Octopus.Core.Settings;
using Octopus.Platform;
using Octopus.Storage;
using TKCode.Arc;

namespace Octopus.App.Settings.PromptEnhancement;

/// <summary>One endpoint card on the prompt enhancement settings page. Every field saves as it changes.</summary>
public sealed partial class PromptEnhancementEndpointViewModel : ObservableObject
{
    /// <summary>What the connection test sends; the reply is not shown, only whether it arrived.</summary>
    private const string TestPrompt = "Reply with the single word OK.";

    private readonly PromptEnhancementEndpoint _model;
    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly IPromptEnhancer _enhancer;
    private readonly Func<PromptEnhancementEndpointViewModel, Task<string?>> _resolveKey;
    private readonly Func<Task> _deleteAsync;

    [ObservableProperty]
    public partial string KeyDraft { get; set; } = "";

    [ObservableProperty]
    public partial bool HasSavedKey { get; set; }

    [ObservableProperty]
    public partial bool IsTesting { get; set; }

    [ObservableProperty]
    public partial string? TestResult { get; set; }

    [ObservableProperty]
    public partial bool TestFailed { get; set; }

    [ObservableProperty]
    public partial bool IsDefault { get; set; }

    public PromptEnhancementEndpointViewModel(
        PromptEnhancementEndpoint model,
        ISettingsStore settings,
        ISecretStore secrets,
        IPromptEnhancer enhancer,
        Func<PromptEnhancementEndpointViewModel, Task<string?>> resolveKey,
        Func<Task> deleteAsync)
    {
        _model = model;
        _settings = settings;
        _secrets = secrets;
        _enhancer = enhancer;
        _resolveKey = resolveKey;
        _deleteAsync = deleteAsync;
        _ = LoadKeyStateAsync();
    }

    public string Id => _model.Id;

    public string DisplayName => string.IsNullOrWhiteSpace(_model.Name)
        ? AppLangResources.Get(AppLangResourceKind.SettingsPromptEnhancementUnnamed)
        : _model.Name;

    public string Name
    {
        get => _model.Name;
        set
        {
            if (value == _model.Name) return;
            Save(m => m.Name = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    public string Protocol
    {
        get => _model.Protocol ?? PromptEnhancementSources.Anthropic;
        set
        {
            if (value == Protocol) return;
            Save(m => m.Protocol = value);
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<SelectOption> ProtocolChoices =>
    [
        new(PromptEnhancementSources.Anthropic, AppLangResources.Get(AppLangResourceKind.SettingsPromptEnhancementProtocolAnthropic)),
        new(PromptEnhancementSources.OpenAi, AppLangResources.Get(AppLangResourceKind.SettingsPromptEnhancementProtocolOpenAi)),
    ];

    public string BaseUrl
    {
        get => _model.BaseUrl ?? "";
        set
        {
            if (value == BaseUrl) return;
            Save(m => m.BaseUrl = value);
            OnPropertyChanged();
        }
    }

    public string Model
    {
        get => _model.Model ?? "";
        set
        {
            if (value == Model) return;
            Save(m => m.Model = value);
            OnPropertyChanged();
        }
    }

    public string SystemPrompt
    {
        get => _model.SystemPrompt ?? "";
        set
        {
            if (value == _model.SystemPrompt) return;
            Save(m => m.SystemPrompt = string.IsNullOrWhiteSpace(value) ? null : value);
            OnPropertyChanged();
        }
    }

    public double? Temperature
    {
        get => _model.Temperature;
        set
        {
            if (value == _model.Temperature) return;
            Save(m => m.Temperature = value);
            OnPropertyChanged();
        }
    }

    public int? MaxTokens
    {
        get => _model.MaxTokens;
        set
        {
            if (value == _model.MaxTokens) return;
            Save(m => m.MaxTokens = value);
            OnPropertyChanged();
        }
    }

    public bool IsKeystoreAvailable => _secrets.IsAvailable;

    public string KeyStateText => AppLangResources.Get(HasSavedKey
        ? AppLangResourceKind.SettingsPromptEnhancementKeySaved
        : AppLangResourceKind.SettingsPromptEnhancementKeyUnset);

    public string TestButtonText => AppLangResources.Get(IsTesting
        ? AppLangResourceKind.SettingsPromptEnhancementTesting
        : AppLangResourceKind.SettingsPromptEnhancementTest);

    private void Save(Action<PromptEnhancementEndpoint> mutate) =>
        _settings.Update(s => mutate(s.PromptEnhancement.Endpoints.FirstOrDefault(e => e.Id == _model.Id) ?? _model));

    private async Task LoadKeyStateAsync()
    {
        var key = await _resolveKey(this).ConfigureAwait(false);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            HasSavedKey = !string.IsNullOrWhiteSpace(key);
            OnPropertyChanged(nameof(KeyStateText));
        });
    }

    partial void OnHasSavedKeyChanged(bool value) => OnPropertyChanged(nameof(KeyStateText));

    partial void OnIsTestingChanged(bool value)
    {
        OnPropertyChanged(nameof(TestButtonText));
        TestCommand.NotifyCanExecuteChanged();
    }

    partial void OnTestResultChanged(string? value) => OnPropertyChanged(nameof(HasTestResult));

    public bool HasTestResult => !string.IsNullOrEmpty(TestResult);

    [RelayCommand]
    private async Task SaveKeyAsync()
    {
        var key = KeyDraft.Trim();
        if (key.Length == 0 || !_secrets.IsAvailable) return;
        await _secrets.SetAsync(PromptEnhancementSources.SecretService, _model.Id, key);
        KeyDraft = "";
        HasSavedKey = true;
    }

    [RelayCommand]
    private async Task ClearKeyAsync()
    {
        if (!_secrets.IsAvailable) return;
        await _secrets.DeleteAsync(PromptEnhancementSources.SecretService, _model.Id);
        HasSavedKey = false;
    }

    [RelayCommand]
    private Task DeleteAsync() => _deleteAsync();

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        IsTesting = true;
        TestResult = null;
        try
        {
            var key = await _resolveKey(this).ConfigureAwait(false);
            var view = new PromptEnhancementEndpointView(_model.Id, DisplayName, Protocol, BaseUrl, key, Model,
                Temperature, MaxTokens, SystemPrompt,
                string.IsNullOrWhiteSpace(BaseUrl) ? PromptEnhancementEndpointStatus.MissingBaseUrl
                    : string.IsNullOrWhiteSpace(key) ? PromptEnhancementEndpointStatus.MissingKey
                    : PromptEnhancementEndpointStatus.Ready,
                IsSession: false, IsDefault: false);
            if (!view.IsAvailable)
            {
                TestFailed = true;
                TestResult = AppLangResources.Get(view.Status == PromptEnhancementEndpointStatus.MissingKey
                    ? AppLangResourceKind.SettingsPromptEnhancementKeyUnset
                    : AppLangResourceKind.SettingsPromptEnhancementBaseUrl);
                return;
            }
            await _enhancer.EnhanceAsync(view, TestPrompt);
            TestFailed = false;
            TestResult = AppLangResources.Get(AppLangResourceKind.SettingsPromptEnhancementTestOk);
        }
        catch (PromptEnhancementException e)
        {
            TestFailed = true;
            TestResult = e.Message;
        }
        catch (Exception e) when (e is HttpRequestException)
        {
            TestFailed = true;
            TestResult = e.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    private bool CanTest() => !IsTesting;
}
