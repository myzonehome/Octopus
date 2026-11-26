using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
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

    private string _defaultEndpointId = PromptEnhancementSources.SessionEndpointId;

    /// <summary>
    /// Bound by a Select whose value is cleared (set to null) while it reloads its options; the property itself is
    /// never null, so code reading it need not guard against that.
    /// </summary>
    public string? DefaultEndpointId
    {
        get => _defaultEndpointId;
        set
        {
            // A Select cleared while its options are rebuilt is not a request to change the default.
            if (string.IsNullOrWhiteSpace(value) || value == _defaultEndpointId) return;
            // Only a known choice: an id the list does not know is not saved at all, and the normalizer would then reset
            // the default to "follow the agent", which would drop the endpoints while the person is still editing them.
            if (value != PromptEnhancementSources.SessionEndpointId && !Endpoints.Any(c => c.Id == value)) return;
            _defaultEndpointId = value;
            OnPropertyChanged();

            var saved = value == PromptEnhancementSources.SessionEndpointId ? null : value;
            if (_settings.Current.PromptEnhancement.DefaultEndpointId != saved)
                _settings.Update(s => s.PromptEnhancement.DefaultEndpointId = saved);
            foreach (var card in Endpoints)
            {
                card.IsDefault = card.Id == value;
            }
        }
    }

    /// <summary>
    /// Keep the same collection instance: replacing ItemsSource makes the Select drop its SelectedValue, which
    /// two-way writes an empty id back into the view model and leaves the field stuck on its placeholder.
    /// </summary>
    public ObservableCollection<SelectOption> DefaultChoices { get; } = [];

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
        var defaultId = settings.DefaultEndpointId ?? PromptEnhancementSources.SessionEndpointId;

        var choices = new List<SelectOption>
        {
            new(PromptEnhancementSources.SessionEndpointId, AppLangResources.Get(AppLangResourceKind.SettingsPromptEnhancementSession)),
        };
        choices.AddRange(Endpoints.Select(card => new SelectOption(card.Id, card.DisplayName)));

        // Update in place: the Select loses its resolved option on a Clear(), leaving the field blank even
        // though SelectedValue still holds the right id.
        for (var i = DefaultChoices.Count - 1; i >= 0; i--)
        {
            if (!choices.Any(c => c.Value == DefaultChoices[i].Value)) DefaultChoices.RemoveAt(i);
        }
        for (var i = 0; i < choices.Count; i++)
        {
            var at = -1;
            for (var j = 0; j < DefaultChoices.Count; j++)
            {
                if (DefaultChoices[j].Value == choices[i].Value) { at = j; break; }
            }
            if (at < 0) DefaultChoices.Add(choices[i]);
            else if (DefaultChoices[at].Label != choices[i].Label) DefaultChoices[at] = choices[i];
        }

        // The option must be in the list before the Select is told to pick it: it does not re-resolve a
        // SelectedValue when a matching item arrives later.
        DefaultEndpointId = defaultId;
        foreach (var card in Endpoints)
        {
            card.IsDefault = card.Id == DefaultEndpointId;
        }

        OnPropertyChanged(nameof(HasEndpoints));
    }

    /// <summary>
    /// Queued: the page also listens for settings changes and refreshes on the next loop turn; adding here and now
    /// would let that refresh reload the Select's options before this card is in the list.
    /// </summary>
    [RelayCommand]
    private Task AddAsync()
    {
        var endpoint = new PromptEnhancementEndpoint { Id = Guid.NewGuid().ToString("N") };
        UiThread(() =>
        {
            _settings.Update(s =>
            {
                s.PromptEnhancement.Endpoints.Add(endpoint);
                // The first endpoint the person adds becomes the default: configuring one means wanting to use it.
                if (s.PromptEnhancement.Endpoints.Count == 1) s.PromptEnhancement.DefaultEndpointId = endpoint.Id;
            });
            Endpoints.Add(CreateCard(endpoint));
            Refresh();
        });
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
        WeakReferenceMessenger.Default.Send(new PromptEnhancementEndpointsChanged());
        var card = Endpoints.FirstOrDefault(c => c.Id == id);
        UiThread(() =>
        {
            if (card is not null) Endpoints.Remove(card);
            Refresh();
        });
    }
}

/// <summary>Sent when a key changes in the keystore, which settings.json alone cannot announce.</summary>
public sealed record PromptEnhancementEndpointsChanged;
