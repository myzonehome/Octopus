using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents.PromptEnhancement;
using Octopus.App.Localization;
using Octopus.Core.Settings;
using Octopus.Platform;
using Octopus.Storage;
using TKCode.Arc;

namespace Octopus.App.Settings.PromptEnhancement;

/// <summary>
/// The prompt enhancement page: the endpoints that rewrite prompts, independent of the agents, and which one the
/// composer's enhance button uses. Metadata is saved to settings.json as it changes; keys go to the keystore.
/// </summary>
public sealed partial class PromptEnhancementSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly IPromptEnhancer _enhancer;

    [ObservableProperty]
    public partial string DefaultEndpointId { get; set; } = PromptEnhancementSources.SessionEndpointId;

    [ObservableProperty]
    public partial IReadOnlyList<SelectOption> DefaultChoices { get; private set; } = [];

    public PromptEnhancementSettingsViewModel(ISettingsStore settings, ISecretStore secrets, IPromptEnhancer enhancer)
    {
        _settings = settings;
        _secrets = secrets;
        _enhancer = enhancer;
        Endpoints = new(settings.Current.PromptEnhancement.Endpoints.Select(CreateCard));
        _settings.Changed += OnSettingsChanged;
        Refresh();
    }

    public ObservableCollection<PromptEnhancementEndpointViewModel> Endpoints { get; }

    public bool HasEndpoints => Endpoints.Count > 0;

    public bool IsKeystoreAvailable => _secrets.IsAvailable;

    public void Dispose() => _settings.Changed -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, EventArgs e) => global::Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);

    private static void UiThread(Action action)
    {
        if (global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
        else global::Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    private PromptEnhancementEndpointViewModel CreateCard(PromptEnhancementEndpoint model) =>
        new(model, _settings, _secrets, _enhancer,
            card => ReadKeyAsync(card.Id),
            () => DeleteAsync(model.Id));

    private async Task<string?> ReadKeyAsync(string id) =>
        _secrets.IsAvailable
            ? await _secrets.GetAsync(PromptEnhancementSources.SecretService, id).ConfigureAwait(false)
            : null;

    private void Refresh()
    {
        var settings = _settings.Current.PromptEnhancement;
        DefaultEndpointId = settings.DefaultEndpointId ?? PromptEnhancementSources.SessionEndpointId;

        var choices = new List<SelectOption>
        {
            new(PromptEnhancementSources.SessionEndpointId, AppLangResources.Get(AppLangResourceKind.SettingsPromptEnhancementSession)),
        };
        choices.AddRange(Endpoints.Select(card => new SelectOption(card.Id, card.DisplayName)));
        DefaultChoices = choices;

        foreach (var card in Endpoints)
        {
            card.IsDefault = card.Id == DefaultEndpointId;
        }

        OnPropertyChanged(nameof(HasEndpoints));
    }

    partial void OnDefaultEndpointIdChanged(string value)
    {
        // A Select cleared while its options are rebuilt is not a request to change the default.
        if (string.IsNullOrWhiteSpace(value)) return;
        _settings.Update(s => s.PromptEnhancement.DefaultEndpointId =
            value == PromptEnhancementSources.SessionEndpointId ? null : value);
        foreach (var card in Endpoints)
        {
            card.IsDefault = card.Id == value;
        }
    }

    partial void OnDefaultChoicesChanged(IReadOnlyList<SelectOption> value)
    {
        // ItemsSource resets the Select's selection; reassert the saved value.
        OnPropertyChanged(nameof(DefaultEndpointId));
    }

    [RelayCommand]
    private Task AddAsync()
    {
        var endpoint = new PromptEnhancementEndpoint { Id = Guid.NewGuid().ToString("N") };
        _settings.Update(s =>
        {
            s.PromptEnhancement.Endpoints.Add(endpoint);
            // The first endpoint the person adds becomes the default: configuring one means wanting to use it.
            if (s.PromptEnhancement.Endpoints.Count == 1) s.PromptEnhancement.DefaultEndpointId = endpoint.Id;
        });
        Endpoints.Add(CreateCard(endpoint));
        Refresh();
        return Task.CompletedTask;
    }

    private async Task DeleteAsync(string id)
    {
        _settings.Update(s =>
        {
            s.PromptEnhancement.Endpoints.RemoveAll(e => e.Id == id);
            if (s.PromptEnhancement.DefaultEndpointId == id) s.PromptEnhancement.DefaultEndpointId = null;
        });
        if (_secrets.IsAvailable) await _secrets.DeleteAsync(PromptEnhancementSources.SecretService, id).ConfigureAwait(false);
        var card = Endpoints.FirstOrDefault(c => c.Id == id);
        UiThread(() =>
        {
            if (card is not null) Endpoints.Remove(card);
            Refresh();
        });
    }
}
