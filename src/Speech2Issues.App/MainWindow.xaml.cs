using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Speech2Issues.App.Audio;
using Speech2Issues.App.Services;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Destinations;
using Speech2Issues.Core.Models;
using Speech2Issues.Core.Services;
using Speech2Issues.Core.Storage;

namespace Speech2Issues.App;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly AppPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly HistoryRepository _history;
    private WhisperTranscriber _whisper;
    private AppSettings _settings;
    private AppSecrets _secrets;
    private int _createdTaskCount;
    private ProjectProfile? _activeProject;
    private IReadOnlyList<AudioDeviceInfo> _microphones = [];
    private IReadOnlyList<AudioDeviceInfo> _playbackDevices = [];
    private AudioRecordingSession? _recording;
    private CancellationTokenSource? _operationCancellation;
    private DispatcherTimer? _countdownTimer;
    private RecordingOverlayWindow? _overlay;
    private List<TaskDraft> _currentDrafts = [];
    private readonly Dictionary<string, HistoryEntry> _currentHistories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, RoutingDecision>> _routes = new(StringComparer.Ordinal);
    private int _countdown;
    private bool _busy;
    private bool _awaitingDecision;
    private bool _modelAvailable;
    private bool _githubConnected;
    private bool _allowClose;

    public MainWindow(AppPaths paths, SettingsStore settingsStore, HistoryRepository history, AppSettings settings, AppSecrets secrets, int createdTaskCount)
    {
        _paths = paths;
        _settingsStore = settingsStore;
        _history = history;
        _settings = settings;
        _secrets = secrets;
        _createdTaskCount = createdTaskCount;
        _whisper = CreateWhisperTranscriber();
        InitializeComponent();
        ApplyThemeIcon();
        Loaded += async (_, _) =>
        {
            LoadDevices();
            RefreshProjectCards();
            RefreshConnectionPrompt();
            await ValidateSelectedModelAsync();
            await LoadExternalSuggestionsAsync();
        };
        Closing += Window_Closing;
    }

    public event EventHandler<bool>? RecordingStateChanged;
    public event EventHandler<string>? NotificationRequested;
    public event EventHandler? HotKeyChanged;
    public bool IsRecording => _recording is not null;

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void HideToTray() { Hide(); ShowInTaskbar = false; }
    public void RequestClose() { _allowClose = true; Close(); }
    public void OpenSettings() => SettingsButton_Click(this, new RoutedEventArgs());

    public void ActivateLastProjectForTray()
    {
        var project = _settings.Projects.FirstOrDefault(x => x.Id == _settings.LastActiveProjectId);
        if (project is not null) ActivateProject(project);
    }

    public void ToggleRecording()
    {
        if (_recording is not null) { _ = StopAndProcessAsync(); return; }
        if (_busy) { if (!_awaitingDecision) _operationCancellation?.Cancel(); return; }
        if (_activeProject is null)
        {
            ShowFromTray();
            ShowProjectHub();
            EmptyProjectsText.Text = "Сначала выберите рабочий проект.";
            EmptyProjectsText.Visibility = Visibility.Visible;
            return;
        }
        _ = StartRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        try
        {
            if (!_modelAvailable) throw new InvalidOperationException("Выбранная LLM-модель недоступна. Откройте настройки ИИ.");
            if (!_activeProject!.Destinations.Any(x => x.IsEnabled)) throw new InvalidOperationException("В проекте нет активных назначений.");
            var microphone = _microphones.FirstOrDefault(x => x.Id == _settings.Audio.MicrophoneDeviceId) ?? _microphones.FirstOrDefault() ?? throw new InvalidOperationException("Микрофон не найден.");
            var playback = _playbackDevices.FirstOrDefault(x => x.Id == _settings.Audio.PlaybackDeviceId) ?? _playbackDevices.FirstOrDefault() ?? throw new InvalidOperationException("Устройство воспроизведения не найдено.");
            _settings.Audio.MicrophoneDeviceId = microphone.Id;
            _settings.Audio.PlaybackDeviceId = playback.Id;
            await _settingsStore.SaveSettingsAsync(_settings);
            _recording = new AudioRecordingSession(_paths, _settings.Audio);
            _recording.LevelsChanged += OnLevelsChanged;
            _recording.Start();
            StatusText.Text = "Идёт запись микрофона и звука компьютера…";
            EnsureOverlay().ShowRecording();
            RecordingStateChanged?.Invoke(this, true);
        }
        catch (Exception ex) { ShowError(ex); _recording?.Dispose(); _recording = null; }
        finally { UpdateRecordButton(); }
    }

    private async Task StopAndProcessAsync()
    {
        var session = _recording!;
        _recording = null;
        session.LevelsChanged -= OnLevelsChanged;
        RecordingStateChanged?.Invoke(this, false);
        EnsureOverlay().ShowProcessing("Свожу микрофон и звук компьютера…");
        SetBusy(true, "Подготавливаю запись…");
        _operationCancellation = new CancellationTokenSource();
        try
        {
            var recorded = await session.StopAsync(_operationCancellation.Token);
            using var http = DestinationFactory.CreateAiHttpClient(_settings, _secrets, TimeSpan.FromMinutes(20));
            var aiProvider = DestinationFactory.CreateAiProvider(http, _settings);
            var pipeline = new SpeechToIssuesService(_whisper, aiProvider);
            var progress = new Progress<SpeechRecognitionProgress>(x => { StatusText.Text = x.Message; EnsureOverlay().UpdateProcessing(x.Message); });
            var result = await pipeline.ProcessAsync(recorded.Samples, progress, _operationCancellation.Token);
            _currentDrafts = result.Drafts.ToList();
            _currentHistories.Clear();
            _routes.Clear();
            var first = _activeProject!.Destinations.First(x => x.IsEnabled);
            foreach (var draft in _currentDrafts)
            {
                var draftRoutes = new Dictionary<string, RoutingDecision>(StringComparer.Ordinal);
                foreach (var binding in _activeProject.Destinations.Where(x => x.IsEnabled && x.Kind == DestinationKind.Planka && x.Planka is not null))
                {
                    var targets = binding.Planka!.AllowedTargets.Select(ToDestinationTarget).ToArray();
                    draftRoutes[binding.Id] = await aiProvider.RoutePlankaAsync(draft, targets, binding.Planka.FallbackListId, _operationCancellation.Token);
                }
                _routes[draft.Id] = draftRoutes;

                var history = new HistoryEntry(draft.Id, draft.CreatedAt, first.Kind, _activeProject.Name, HistoryStatus.Pending,
                    draft.Title, result.Transcript, JsonSerializer.Serialize(draft, JsonOptions), null, null, recorded.WavPath, _activeProject.Id);
                _currentHistories[draft.Id] = history;
                await _history.UpsertAsync(history, _operationCancellation.Token);
                foreach (var binding in _activeProject.Destinations.Where(x => x.IsEnabled))
                {
                    var target = ResolveTarget(binding, draft.Id);
                    await _history.UpsertDeliveryAsync(new DeliveryAttempt(draft.Id, binding.Id, binding.Kind, target.Id, target.Name, HistoryStatus.Pending, null, null));
                }
            }
            BeginCountdown();
        }
        catch (OperationCanceledException) { StatusText.Text = "Обработка отменена."; EnsureOverlay().ShowCanceled("Обработка записи остановлена."); }
        catch (Exception ex) { ShowError(ex); }
        finally { session.Dispose(); if (!_awaitingDecision) SetBusy(false, StatusText.Text); UpdateRecordButton(); }
    }

    private void BeginCountdown()
    {
        _countdown = Math.Max(1, _settings.CountdownSeconds);
        _awaitingDecision = true;
        var summary = string.Join(" · ", _activeProject!.Destinations.Where(x => x.IsEnabled).Select(x => x.Kind));
        var route = _routes.Values.SelectMany(x => x.Values).FirstOrDefault();
        if (route is not null) summary += route.UsedFallback
            ? $"\n⚠ PLANKA → {route.DisplayName} (резервный список)"
            : $"\nPLANKA → {route.DisplayName}";
        EnsureOverlay().ShowDrafts(_currentDrafts, summary, _countdown);
        _countdownTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, async (_, _) =>
        {
            _countdown--;
            _overlay?.UpdateCountdown(_countdown);
            if (_countdown <= 0) { StopCountdown(); await SubmitCurrentAsync(); }
        }, Dispatcher);
        _countdownTimer.Start();
        SetBusy(true, _currentDrafts.Count == 1
            ? "Задача подготовлена. Ожидаю автоотправку…"
            : $"Подготовлено задач: {_currentDrafts.Count}. Ожидаю автоотправку…");
    }

    private async Task SubmitCurrentAsync()
    {
        if (_currentDrafts.Count == 0 || _activeProject is null) return;
        StopCountdown();
        _operationCancellation = new CancellationTokenSource();
        SetBusy(true, _currentDrafts.Count == 1 ? "Отправляю задачу в подключённые платформы…" : $"Отправляю {_currentDrafts.Count} задач…");
        EnsureOverlay().ShowSending(string.Join(", ", _activeProject.Destinations.Where(x => x.IsEnabled).Select(x => x.Kind)));
        try
        {
            var totalSent = 0;
            var totalFailed = 0;
            var totalDeliveries = 0;
            var allSent = true;
            string? audioPath = null;
            foreach (var draft in _currentDrafts)
            {
                var history = _currentHistories[draft.Id];
                audioPath ??= history.AudioPath;
                var existing = (await _history.GetDeliveriesAsync(draft.Id)).ToDictionary(x => x.BindingId);
                var pending = _activeProject.Destinations.Where(x => x.IsEnabled && (!existing.TryGetValue(x.Id, out var attempt) || attempt.Status != HistoryStatus.Sent)).ToArray();
                var tasks = pending.Select(binding => SendToDestinationAsync(draft, binding, existing.GetValueOrDefault(binding.Id), _operationCancellation.Token)).ToArray();
                var results = await Task.WhenAll(tasks);
                foreach (var result in results) await _history.UpsertDeliveryAsync(result);
                var deliveries = await _history.GetDeliveriesAsync(draft.Id);
                var sent = deliveries.Count(x => x.Status == HistoryStatus.Sent);
                var failed = deliveries.Count(x => x.Status == HistoryStatus.Failed);
                var status = DeliveryCoordinator.AggregateStatus(deliveries);
                totalSent += sent;
                totalFailed += failed;
                totalDeliveries += deliveries.Count;
                allSent &= status == HistoryStatus.Sent;
                history = history with
                {
                    Status = status,
                    DraftJson = JsonSerializer.Serialize(draft, JsonOptions),
                    Title = draft.Title,
                    ExternalUrl = deliveries.FirstOrDefault(x => x.Status == HistoryStatus.Sent)?.ExternalUrl,
                    Error = failed > 0 ? $"Не отправлено: {failed} из {deliveries.Count}" : null,
                    AudioPath = status == HistoryStatus.Sent ? null : history.AudioPath,
                };
                _currentHistories[draft.Id] = history;
                await _history.UpsertAsync(history);
            }
            if (allSent && audioPath is { } path && File.Exists(path)) File.Delete(path);
            if (allSent)
            {
                StatusText.Text = _currentDrafts.Count == 1
                    ? $"Задача создана во всех назначениях ({totalSent})."
                    : $"Создано задач: {_currentDrafts.Count}. Успешных отправок: {totalSent}.";
                EnsureOverlay().ShowSuccess(StatusText.Text, _currentDrafts.Count);
            }
            else
            {
                StatusText.Text = $"Частичный результат: отправлено {totalSent}, ошибок {totalFailed}. Повтор доступен в истории.";
                EnsureOverlay().ShowPartialSuccess(totalSent, totalDeliveries, StatusText.Text);
            }
            NotificationRequested?.Invoke(this, StatusText.Text);
        }
        catch (OperationCanceledException) { EnsureOverlay().ShowCanceled("Отправка остановлена."); }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false, StatusText.Text); }
    }

    private async Task<DeliveryAttempt> SendToDestinationAsync(TaskDraft draft, DestinationBinding binding, DeliveryAttempt? previous, CancellationToken cancellationToken)
    {
        var target = ResolveTarget(binding, draft.Id, previous?.TargetId);
        try
        {
            var destination = DestinationFactory.Create(binding, _settings, _secrets, target.Id);
            var result = await destination.CreateAsync(draft, cancellationToken);
            return new(draft.Id, binding.Id, binding.Kind, target.Id, target.Name, HistoryStatus.Sent, result.Url, null);
        }
        catch (Exception ex)
        {
            return new(draft.Id, binding.Id, binding.Kind, target.Id, target.Name, HistoryStatus.Failed, previous?.ExternalUrl, ex.Message);
        }
    }

    private (string Id, string Name) ResolveTarget(DestinationBinding binding, string draftId, string? storedTargetId = null)
    {
        return binding.Kind switch
        {
            DestinationKind.Planka => ResolvePlankaTarget(binding, draftId, storedTargetId),
            DestinationKind.GitHub => (binding.GitHub?.Repository ?? string.Empty, binding.GitHub?.Repository ?? "GitHub"),
            DestinationKind.Obsidian => (binding.Obsidian?.ProjectFile ?? binding.Obsidian?.OutputFolder ?? string.Empty, binding.DisplayName),
            DestinationKind.Webhook => (_settings.Webhook.Url, binding.Webhook?.DisplayName ?? "Webhook"),
            _ => (string.Empty, binding.DisplayName),
        };
    }

    private (string Id, string Name) ResolvePlankaTarget(DestinationBinding binding, string draftId, string? storedTargetId)
    {
        var settings = binding.Planka!;
        var id = storedTargetId ?? (_routes.TryGetValue(draftId, out var draftRoutes) && draftRoutes.TryGetValue(binding.Id, out var route) ? route.ListId : settings.FallbackListId);
        var target = settings.AllowedTargets.FirstOrDefault(x => x.ListId == id) ?? settings.AllowedTargets.First();
        return (target.ListId, target.DisplayName);
    }

    private async Task ValidateSelectedModelAsync()
    {
        try
        {
            using var http = DestinationFactory.CreateAiHttpClient(_settings, _secrets, TimeSpan.FromSeconds(20));
            var provider = DestinationFactory.CreateAiProvider(http, _settings);
            var check = await provider.CheckAsync();
            _modelAvailable = check.IsSuccess;
            ModelWarning.Visibility = _modelAvailable ? Visibility.Collapsed : Visibility.Visible;
            ModelWarningText.Text = _modelAvailable ? string.Empty : check.Message;
            StatusDot.Fill = (Brush)FindResource(_modelAvailable ? "SuccessBrush" : "DangerBrush");
        }
        catch (Exception ex)
        {
            _modelAvailable = false;
            ModelWarning.Visibility = Visibility.Visible;
            ModelWarningText.Text = $"Провайдер ИИ недоступен: {ex.Message}";
            StatusDot.Fill = (Brush)FindResource("DangerBrush");
        }
        UpdateRecordButton();
    }

    private void RefreshProjectCards()
    {
        var query = ProjectSearchBox?.Text?.Trim() ?? string.Empty;
        var items = _settings.Projects.Where(x => string.IsNullOrWhiteSpace(query) || x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(x => x.Id == _settings.LastActiveProjectId).ThenByDescending(x => x.LastUsedAt)
            .Select(x => new ProjectCard(x, string.Join(" · ", x.Destinations.Where(d => d.IsEnabled).Select(d => DisplayKind(d.Kind))), x.Id == _settings.LastActiveProjectId ? Visibility.Visible : Visibility.Collapsed)).ToArray();
        ProjectItems.ItemsSource = items;
        EmptyProjectsText.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshConnectionPrompt()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(_secrets.PlankaApiKey)) missing.Add("PLANKA");
        if (!_githubConnected && string.IsNullOrWhiteSpace(_secrets.GitHubToken) && !IsRepository(_settings.GitHub.Repository)) missing.Add("GitHub");
        if (string.IsNullOrWhiteSpace(_settings.Obsidian.VaultPath)) missing.Add("Obsidian");
        if (string.IsNullOrWhiteSpace(_settings.Webhook.Url)) missing.Add("Webhook");
        ConnectionsPrompt.Visibility = missing.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ConnectionsText.Text = missing.Count == 0 ? string.Empty : string.Join("  ·  ", missing);
    }

    private async Task LoadExternalSuggestionsAsync()
    {
        var candidates = new List<ExternalProjectCandidate>();
        try
        {
            if (!string.IsNullOrWhiteSpace(_secrets.PlankaApiKey))
            {
                try
                {
                    var destination = new PlankaDestination(DestinationFactory.CreateHttpClient(timeout: TimeSpan.FromSeconds(12)), _settings.Planka, _secrets.PlankaApiKey);
                    candidates.AddRange(await new PlankaCatalog(destination).DiscoverProjectsAsync());
                }
                catch { /* PLANKA may be unavailable; keep other catalogs visible. */ }
            }
            if (!string.IsNullOrWhiteSpace(_settings.Obsidian.VaultPath) && Directory.Exists(_settings.Obsidian.VaultPath))
            {
                try
                {
                    candidates.AddRange(await new ObsidianCatalog(new ObsidianDestination(_settings.Obsidian)).DiscoverProjectsAsync());
                }
                catch { /* Obsidian may be unavailable; keep other catalogs visible. */ }
            }
            try
            {
                var github = await new GitHubCatalog(DestinationFactory.CreateHttpClient(timeout: TimeSpan.FromSeconds(15)), _secrets.GitHubToken).DiscoverProjectsAsync();
                candidates.AddRange(github);
                _githubConnected = true;
            }
            catch
            {
                // GitHub is optional; unavailable CLI authorization must not block the hub.
            }
            var imported = _settings.Projects.SelectMany(x => x.ExternalLinks).Select(x => (x.Kind, x.ExternalId)).ToHashSet();
            var linkedPlankaNames = _settings.Projects.SelectMany(x => x.Destinations).Where(x => x.Kind == DestinationKind.Planka).Select(x => x.Planka?.ProjectName).Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            var visible = candidates.Where(x => !imported.Contains((x.Kind, x.ExternalId)) && (x.Kind != DestinationKind.Planka || !linkedPlankaNames.Contains(x.DisplayName))).ToArray();
            ImportItems.ItemsSource = visible;
            ImportSection.Visibility = visible.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            RefreshConnectionPrompt();
        }
        catch { ImportSection.Visibility = Visibility.Collapsed; }
    }

    private async void ProjectCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProjectProfile project })
        {
            ActivateProject(project);
            await _settingsStore.SaveSettingsAsync(_settings);
        }
    }

    private void ActivateProject(ProjectProfile project)
    {
        _activeProject = project;
        project.LastUsedAt = DateTimeOffset.UtcNow;
        _settings.LastActiveProjectId = project.Id;
        ProjectHub.Visibility = Visibility.Collapsed;
        WorkspacePanel.Visibility = Visibility.Visible;
        ActiveProjectName.Text = project.Name;
        DestinationBadges.ItemsSource = project.Destinations.Where(x => x.IsEnabled).Select(x => DisplayKind(x.Kind)).ToArray();
        HotkeyText.Text = $"Запись: {_settings.Hotkey.Display}";
        StatusText.Text = project.Destinations.Any(x => x.IsEnabled) ? "Готово к записи" : "Добавьте хотя бы одно назначение проекта.";
        UpdateRecordButton();
    }

    private void ShowProjectHub()
    {
        _activeProject = null;
        ProjectHub.Visibility = Visibility.Visible;
        WorkspacePanel.Visibility = Visibility.Collapsed;
        RefreshProjectCards();
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var window = new ProjectWindow(_settings, _secrets) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _settings.Projects.Add(window.Project);
            await _settingsStore.SaveSettingsAsync(_settings);
            RefreshProjectCards();
        }
    }

    private async void ConfigureProject_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProject is null) return;
        var window = new ProjectWindow(_settings, _secrets, _activeProject) { Owner = this };
        if (window.ShowDialog() == true)
        {
            var index = _settings.Projects.FindIndex(x => x.Id == _activeProject.Id);
            if (index >= 0) _settings.Projects[index] = window.Project;
            _activeProject = window.Project;
            await _settingsStore.SaveSettingsAsync(_settings);
            ActivateProject(_activeProject);
        }
    }

    private async void ImportProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ExternalProjectCandidate candidate }) return;
        var project = new ProjectProfile { Name = candidate.DisplayName, ExternalLinks = [new ExternalProjectLink { Kind = candidate.Kind, ExternalId = candidate.ExternalId, DisplayName = candidate.DisplayName }] };
        if (candidate.Kind == DestinationKind.Planka)
        {
            var fallback = candidate.Targets.FirstOrDefault(x => x.Id == _settings.Planka.ListId) ?? candidate.Targets.First();
            project.Destinations.Add(new DestinationBinding { Kind = DestinationKind.Planka, DisplayName = "PLANKA", Planka = new PlankaProjectBinding { ProjectId = candidate.ExternalId, ProjectName = candidate.DisplayName, FallbackListId = fallback.Id, AllowedTargets = candidate.Targets.Select(x => new PlankaTargetBinding { ListId = x.Id, DisplayName = x.DisplayName, BoardId = x.Parent }).ToList() } });
        }
        else if (candidate.Kind == DestinationKind.Obsidian)
            project.Destinations.Add(new DestinationBinding { Kind = DestinationKind.Obsidian, DisplayName = "Obsidian", Obsidian = new ObsidianProjectBinding { ProjectFile = candidate.ExternalId, OutputFolder = _settings.Obsidian.OutputFolder, InboxFile = _settings.Obsidian.InboxFile } });
        else if (candidate.Kind == DestinationKind.GitHub)
            project.Destinations.Add(new DestinationBinding { Kind = DestinationKind.GitHub, DisplayName = "GitHub", GitHub = new GitHubProjectBinding { Repository = candidate.ExternalId } });
        _settings.Projects.Add(project);
        await _settingsStore.SaveSettingsAsync(_settings);
        RefreshProjectCards();
        await LoadExternalSuggestionsAsync();
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _createdTaskCount = await _history.CountCreatedTasksAsync();
        _settings.Theme = ThemeService.Normalize(_settings.Theme, _createdTaskCount);
        var window = new SettingsWindow(_paths, _settings, _secrets, _createdTaskCount) { Owner = this };
        window.ThemeChanged += async themeId =>
        {
            _settings.Theme = themeId;
            ApplyThemeIcon();
            try
            {
                await _settingsStore.SaveSettingsAsync(_settings);
            }
            catch (Exception ex)
            {
                NotificationRequested?.Invoke(this, $"Не удалось сохранить тему: {ex.Message}");
            }
        };
        if (window.ShowDialog() == true)
        {
            _whisper.Dispose();
            _settings = window.Settings;
            _secrets = window.Secrets;
            _whisper = CreateWhisperTranscriber();
            await _settingsStore.SaveSettingsAsync(_settings);
            await _settingsStore.SaveSecretsAsync(_secrets);
            ThemeService.Apply(_settings.Theme, _createdTaskCount);
            ApplyThemeIcon();
            LoadDevices(); RefreshProjectCards(); RefreshConnectionPrompt(); HotKeyChanged?.Invoke(this, EventArgs.Empty);
            await ValidateSelectedModelAsync();
            await LoadExternalSuggestionsAsync();
        }
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new HistoryWindow(_history) { Owner = this };
        window.RetryRequested += async (_, entry) => await RetryHistoryAsync(entry);
        window.ShowDialog();
    }

    private async Task RetryHistoryAsync(HistoryEntry entry)
    {
        var draft = JsonSerializer.Deserialize<TaskDraft>(entry.DraftJson, JsonOptions);
        var project = _settings.Projects.FirstOrDefault(x => x.Id == entry.ProjectId) ?? _settings.Projects.FirstOrDefault(x => x.Name == entry.Target);
        if (draft is null || project is null) { ShowError(new InvalidOperationException("Проект или черновик больше не существует.")); return; }
        ActivateProject(project);
        _currentDrafts = [draft];
        _currentHistories.Clear();
        _currentHistories[draft.Id] = entry;
        _routes.Clear();
        var draftRoutes = new Dictionary<string, RoutingDecision>(StringComparer.Ordinal);
        foreach (var attempt in await _history.GetDeliveriesAsync(entry.Id))
            if (project.Destinations.FirstOrDefault(x => x.Id == attempt.BindingId) is { Kind: DestinationKind.Planka } binding)
                draftRoutes[binding.Id] = new RoutingDecision(attempt.TargetId, attempt.Target, false);
        _routes[draft.Id] = draftRoutes;
        BeginCountdown();
    }

    private RecordingOverlayWindow EnsureOverlay()
    {
        if (_overlay is not null) return _overlay;
        _overlay = new RecordingOverlayWindow();
        _overlay.SendRequested += async (_, _) => { if (_awaitingDecision) { StopCountdown(); await SubmitCurrentAsync(); } };
        _overlay.CancelRequested += async (_, _) => await CancelPendingDraftAsync();
        _overlay.EditRequested += async (_, _) => await EditPendingDraftAsync();
        return _overlay;
    }

    private async Task EditPendingDraftAsync()
    {
        if (!_awaitingDecision || _currentDrafts.Count == 0 || _activeProject is null) return;
        _countdownTimer?.Stop(); _countdownTimer = null; _overlay?.HideAnimated();
        var planka = _activeProject.Destinations.FirstOrDefault(x => x.IsEnabled && x.Kind == DestinationKind.Planka && x.Planka is not null);
        var targets = planka?.Planka?.AllowedTargets.Select(ToDestinationTarget).ToArray() ?? [];
        ShowFromTray();
        var editedDrafts = new List<TaskDraft>(_currentDrafts.Count);
        for (var index = 0; index < _currentDrafts.Count; index++)
        {
            var draft = _currentDrafts[index];
            var selected = planka is not null && _routes.TryGetValue(draft.Id, out var draftRoutes) && draftRoutes.TryGetValue(planka.Id, out var route)
                ? route.ListId
                : planka?.Planka?.FallbackListId;
            var editor = new DraftEditorWindow(draft, targets, selected) { Owner = this };
            if (_currentDrafts.Count > 1) editor.Title = $"Черновик {index + 1} из {_currentDrafts.Count}";
            if (editor.ShowDialog() != true)
            {
                await CancelPendingDraftAsync();
                return;
            }
            var edited = editor.Draft;
            editedDrafts.Add(edited);
            if (planka is not null && editor.SelectedPlankaTargetId is { } targetId)
            {
                var target = targets.First(x => x.Id == targetId);
                if (!_routes.TryGetValue(edited.Id, out var routes)) _routes[edited.Id] = routes = new(StringComparer.Ordinal);
                routes[planka.Id] = new RoutingDecision(target.Id, target.DisplayName, false, "Выбрано вручную.");
            }
            var history = _currentHistories[draft.Id] with { Title = edited.Title, DraftJson = JsonSerializer.Serialize(edited, JsonOptions) };
            _currentHistories[edited.Id] = history;
            await _history.UpsertAsync(history);
        }
        _currentDrafts = editedDrafts;
        StopCountdown();
        await SubmitCurrentAsync();
    }

    private async Task CancelPendingDraftAsync()
    {
        StopCountdown();
        foreach (var draft in _currentDrafts)
        {
            if (!_currentHistories.TryGetValue(draft.Id, out var history)) continue;
            history = history with { Status = HistoryStatus.Canceled };
            _currentHistories[draft.Id] = history;
            await _history.UpsertAsync(history);
        }
        SetBusy(false, _currentDrafts.Count == 1 ? "Черновик сохранён в истории и не отправлен." : "Черновики сохранены в истории и не отправлены.");
        EnsureOverlay().ShowCanceled(StatusText.Text);
    }

    private void StopCountdown() { _countdownTimer?.Stop(); _countdownTimer = null; _awaitingDecision = false; UpdateRecordButton(); }
    private void ApplyThemeIcon() { var icon = ThemeService.GetIcon(_settings.Theme); Icon = icon; BrandIcon.Source = icon; }
    private void SetBusy(bool busy, string text) { _busy = busy; StatusText.Text = text; SettingsButton.IsEnabled = !busy; ProcessingProgress.Visibility = busy && !_awaitingDecision ? Visibility.Visible : Visibility.Collapsed; UpdateRecordButton(); }
    private void UpdateRecordButton()
    {
        if (RecordButton is null) return;
        if (_recording is not null) { RecordButton.IsEnabled = true; RecordButton.Content = "\uE71A"; RecordButton.Background = (Brush)FindResource("DangerBrush"); RecordButton.ToolTip = "Остановить запись"; }
        else if (_busy) { RecordButton.IsEnabled = !_awaitingDecision; RecordButton.Content = "\uE711"; RecordButton.Background = (Brush)FindResource("DangerBrush"); RecordButton.ToolTip = "Отменить обработку"; }
        else { RecordButton.IsEnabled = _activeProject is not null && _modelAvailable && _activeProject.Destinations.Any(x => x.IsEnabled); RecordButton.Content = "\uE720"; RecordButton.Background = (Brush)FindResource("AccentBrush"); RecordButton.ToolTip = $"Начать запись ({_settings.Hotkey.Display})"; }
    }
    private void LoadDevices() { _microphones = AudioDeviceService.GetMicrophones(); _playbackDevices = AudioDeviceService.GetPlaybackDevices(); }
    private WhisperTranscriber CreateWhisperTranscriber() => new(_paths.ModelsDirectory, _settings.SpeechRecognition, _paths.RuntimeDirectory);
    private void OnLevelsChanged(float microphone, float playback) => Dispatcher.BeginInvoke(() => _overlay?.UpdateAudioLevel(Math.Sqrt(Math.Max(Math.Max(0, microphone), Math.Max(0, playback)))));
    private void ShowError(Exception ex) { StatusText.Text = $"Ошибка: {ex.Message}"; EnsureOverlay().ShowError(ex.Message); NotificationRequested?.Invoke(this, StatusText.Text); }
    private static DestinationTarget ToDestinationTarget(PlankaTargetBinding x) => new(x.ListId, x.DisplayName, x.BoardId);
    private static bool IsRepository(string value) { var parts = value.Trim().Trim('/').Split('/'); return parts.Length == 2 && parts.All(x => !string.IsNullOrWhiteSpace(x)); }
    private static string DisplayKind(DestinationKind kind) => kind switch { DestinationKind.Planka => "PLANKA", DestinationKind.GitHub => "GitHub", DestinationKind.Obsidian => "Obsidian", DestinationKind.Webhook => "Webhook", _ => kind.ToString() };

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) { _operationCancellation?.Cancel(); _countdownTimer?.Stop(); _recording?.Dispose(); _whisper.Dispose(); _overlay?.CloseImmediately(); return; }
        e.Cancel = true; if (_settings.CloseToTray) HideToTray(); else RequestClose();
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; else if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    private void RecordButton_Click(object sender, RoutedEventArgs e) => ToggleRecording();
    private void HideToTray_Click(object sender, RoutedEventArgs e) => HideToTray();
    private void ChangeProject_Click(object sender, RoutedEventArgs e) => ShowProjectHub();
    private void ProjectSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshProjectCards();
    public sealed record ProjectCard(ProjectProfile Project, string DestinationSummary, Visibility LastUsedVisibility);
}
