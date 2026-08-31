using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Speech2Issues.App.Services;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Destinations;
using Speech2Issues.Core.Models;

namespace Speech2Issues.App;

public partial class ProjectWindow : Window
{
    private readonly AppSettings _settings;
    private readonly AppSecrets _secrets;
    private readonly ProjectProfile _project;
    private readonly DispatcherTimer _plankaRefreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource _windowCancellation = new();
    private IReadOnlyList<DestinationTarget> _plankaTargets = [];
    private bool _isRefreshingPlanka;
    private bool _updatingFallback;
    private string? _preferredFallbackId;

    public ProjectWindow(AppSettings settings, AppSecrets secrets, ProjectProfile? project = null)
    {
        _settings = settings;
        _secrets = secrets;
        _project = project is null
            ? new ProjectProfile()
            : JsonSerializer.Deserialize<ProjectProfile>(JsonSerializer.Serialize(project)) ?? new ProjectProfile();
        InitializeComponent();
        Populate();
        Loaded += ProjectWindow_Loaded;
        Closed += ProjectWindow_Closed;
        _plankaRefreshTimer.Tick += PlankaRefreshTimer_Tick;
    }

    public ProjectProfile Project => _project;

    private void Populate()
    {
        NameBox.Text = _project.Name;
        var planka = _project.Destinations.FirstOrDefault(x => x.Kind == DestinationKind.Planka);
        PlankaCheck.IsChecked = planka?.IsEnabled == true;
        if (planka?.Planka is { } plankaSettings)
        {
            var targets = plankaSettings.AllowedTargets.Select(ToTarget).ToArray();
            SetPlankaCatalog(targets, new HashSet<string>(StringComparer.Ordinal), targets.Select(x => x.Id).ToHashSet(StringComparer.Ordinal), plankaSettings.FallbackListId);
        }
        var github = _project.Destinations.FirstOrDefault(x => x.Kind == DestinationKind.GitHub);
        GitHubCheck.IsChecked = github?.IsEnabled == true;
        GitHubRepoBox.Text = github?.GitHub?.Repository ?? _settings.GitHub.Repository;
        var obsidian = _project.Destinations.FirstOrDefault(x => x.Kind == DestinationKind.Obsidian);
        ObsidianCheck.IsChecked = obsidian?.IsEnabled == true;
        ObsidianProjectBox.Text = obsidian?.Obsidian?.ProjectFile ?? _settings.Obsidian.ProjectFile;
        ObsidianFolderBox.Text = obsidian?.Obsidian?.OutputFolder ?? _settings.Obsidian.OutputFolder;
        ObsidianInboxBox.Text = obsidian?.Obsidian?.InboxFile ?? _settings.Obsidian.InboxFile;
        WebhookCheck.IsChecked = _project.Destinations.Any(x => x.Kind == DestinationKind.Webhook && x.IsEnabled);
    }

    private async void ProjectWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshPlankaCatalogAsync();
        _plankaRefreshTimer.Start();
    }

    private async void PlankaRefreshTimer_Tick(object? sender, EventArgs e) => await RefreshPlankaCatalogAsync();

    private void ProjectWindow_Closed(object? sender, EventArgs e)
    {
        _plankaRefreshTimer.Stop();
        _windowCancellation.Cancel();
    }

    private async Task RefreshPlankaCatalogAsync()
    {
        if (_isRefreshingPlanka) return;
        _isRefreshingPlanka = true;
        PlankaStatusText.Text = "Обновляю проекты, доски и списки PLANKA…";
        try
        {
            var destination = new PlankaDestination(DestinationFactory.CreateHttpClient(), _settings.Planka, _secrets.PlankaApiKey);
            var loadedTargets = await destination.LoadTargetsAsync(_windowCancellation.Token);
            var targets = LimitToCurrentProject(loadedTargets);
            var selectedKeys = PlankaTargetsList.SelectedItems.Cast<PlankaTargetOption>().Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            var selectedIds = ExpandSelectedTargets().Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            var fallbackId = (PlankaFallbackCombo.SelectedItem as DestinationTarget)?.Id ?? _preferredFallbackId ?? _settings.Planka.ListId;
            SetPlankaCatalog(targets, selectedKeys, selectedIds, fallbackId);
            if (_project.Destinations.Count == 0 && targets.Count > 0) PlankaCheck.IsChecked = true;
            var boardCount = targets.Select(x => x.Parent).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Count();
            PlankaStatusText.Text = $"Обновлено автоматически: досок — {boardCount}, списков — {targets.Count}.";
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            PlankaStatusText.Text = $"Не удалось обновить автоматически: {ex.Message} Сохранённые варианты оставлены без изменений.";
        }
        finally { _isRefreshingPlanka = false; }
    }

    private IReadOnlyList<DestinationTarget> LimitToCurrentProject(IReadOnlyList<DestinationTarget> targets)
    {
        var binding = _project.Destinations.FirstOrDefault(x => x.Kind == DestinationKind.Planka)?.Planka;
        IEnumerable<DestinationTarget> matching = [];
        if (!string.IsNullOrWhiteSpace(binding?.ProjectId))
            matching = targets.Where(x => string.Equals(x.ProjectId, binding.ProjectId, StringComparison.Ordinal));
        if (!matching.Any())
        {
            var projectName = !string.IsNullOrWhiteSpace(binding?.ProjectName) ? binding.ProjectName : _project.Name;
            matching = targets.Where(x => string.Equals(x.ProjectName, projectName, StringComparison.CurrentCultureIgnoreCase));
        }
        var result = matching.ToArray();
        return result.Length > 0 ? result : targets;
    }

    private void SetPlankaCatalog(
        IReadOnlyList<DestinationTarget> targets,
        IReadOnlySet<string> selectedKeys,
        IReadOnlySet<string> selectedTargetIds,
        string? fallbackId)
    {
        _plankaTargets = targets.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var options = BuildPlankaOptions(_plankaTargets);
        PlankaTargetsList.ItemsSource = options;

        foreach (var option in options)
        {
            var explicitlySelected = selectedKeys.Contains(option.Key);
            var allChildrenSelected = option.IsBoard && option.Targets.All(x => selectedTargetIds.Contains(x.Id));
            var listSelected = !option.IsBoard && selectedTargetIds.Contains(option.Targets[0].Id);
            if (explicitlySelected || (selectedKeys.Count == 0 && (allChildrenSelected || listSelected)))
                PlankaTargetsList.SelectedItems.Add(option);
        }

        _preferredFallbackId = fallbackId;
        ApplyFallbackFilter();
    }

    private static IReadOnlyList<PlankaTargetOption> BuildPlankaOptions(IReadOnlyList<DestinationTarget> targets)
    {
        var result = new List<PlankaTargetOption>();
        foreach (var group in targets.GroupBy(x => x.Parent ?? $"list:{x.Id}"))
        {
            var lists = group.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
            var first = lists[0];
            if (!string.IsNullOrWhiteSpace(first.Parent))
            {
                var boardName = string.IsNullOrWhiteSpace(first.BoardName) ? BoardPath(first.DisplayName) : $"{first.ProjectName} / {first.BoardName}";
                result.Add(new($"board:{first.Parent}", $"{boardName} — вся доска (список выберет ИИ)", lists, true));
            }
            result.AddRange(lists.Select(x => new PlankaTargetOption($"list:{x.Id}", x.DisplayName, [x], false)));
        }
        return result;
    }

    private static string BoardPath(string displayName)
    {
        var separator = displayName.LastIndexOf(" / ", StringComparison.Ordinal);
        return separator > 0 ? displayName[..separator] : displayName;
    }

    private DestinationTarget[] ExpandSelectedTargets() => PlankaTargetsList.SelectedItems
        .Cast<PlankaTargetOption>()
        .SelectMany(x => x.Targets)
        .DistinctBy(x => x.Id, StringComparer.Ordinal)
        .ToArray();

    private void PlankaFallbackSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFallbackFilter();

    private void ApplyFallbackFilter()
    {
        if (PlankaFallbackCombo is null || PlankaFallbackSearchBox is null) return;
        var selectedId = (PlankaFallbackCombo.SelectedItem as DestinationTarget)?.Id ?? _preferredFallbackId;
        var query = PlankaFallbackSearchBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _plankaTargets
            : _plankaTargets.Where(x => x.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        _updatingFallback = true;
        PlankaFallbackCombo.ItemsSource = filtered;
        PlankaFallbackCombo.SelectedItem = filtered.FirstOrDefault(x => x.Id == selectedId) ?? filtered.FirstOrDefault();
        _updatingFallback = false;
    }

    private void PlankaFallbackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingFallback && PlankaFallbackCombo.SelectedItem is DestinationTarget target)
            _preferredFallbackId = target.Id;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText.Text = "Укажите название проекта.";
            return;
        }

        var destinations = new List<DestinationBinding>();
        if (PlankaCheck.IsChecked == true)
        {
            var targets = ExpandSelectedTargets();
            if (targets.Length == 0)
            {
                StatusText.Text = "Выберите хотя бы один список PLANKA.";
                return;
            }
            var fallback = PlankaFallbackCombo.SelectedItem as DestinationTarget
                ?? targets.FirstOrDefault(x => x.Id == _preferredFallbackId);
            if (fallback is null || targets.All(x => x.Id != fallback.Id)) fallback = targets[0];
            var previous = _project.Destinations.FirstOrDefault(x => x.Kind == DestinationKind.Planka);
            destinations.Add(new DestinationBinding
            {
                Id = previous?.Id ?? $"binding-{Guid.NewGuid():N}",
                Kind = DestinationKind.Planka,
                DisplayName = "PLANKA",
                Planka = new PlankaProjectBinding
                {
                    ProjectId = targets.Select(x => x.ProjectId).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty,
                    ProjectName = targets.Select(x => x.ProjectName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? name,
                    FallbackListId = fallback.Id,
                    AllowedTargets = targets.Select(x => new PlankaTargetBinding { ListId = x.Id, DisplayName = x.DisplayName, BoardId = x.Parent }).ToList(),
                },
            });
        }
        AddSimpleDestination(destinations, DestinationKind.GitHub, GitHubCheck.IsChecked == true, x => x.GitHub = new GitHubProjectBinding { Repository = GitHubRepoBox.Text.Trim() });
        AddSimpleDestination(destinations, DestinationKind.Obsidian, ObsidianCheck.IsChecked == true, x => x.Obsidian = new ObsidianProjectBinding { ProjectFile = ObsidianProjectBox.Text.Trim(), OutputFolder = ObsidianFolderBox.Text.Trim(), InboxFile = ObsidianInboxBox.Text.Trim() });
        AddSimpleDestination(destinations, DestinationKind.Webhook, WebhookCheck.IsChecked == true, x => x.Webhook = new WebhookProjectBinding());
        _project.Name = name;
        _project.Destinations = destinations;
        DialogResult = true;
    }

    private void AddSimpleDestination(List<DestinationBinding> list, DestinationKind kind, bool enabled, Action<DestinationBinding> configure)
    {
        if (!enabled) return;
        var previous = _project.Destinations.FirstOrDefault(x => x.Kind == kind);
        var binding = new DestinationBinding { Id = previous?.Id ?? $"binding-{Guid.NewGuid():N}", Kind = kind, DisplayName = kind.ToString() };
        configure(binding);
        list.Add(binding);
    }

    private static DestinationTarget ToTarget(PlankaTargetBinding value) => new(value.ListId, value.DisplayName, value.BoardId);
    private sealed record PlankaTargetOption(string Key, string DisplayName, IReadOnlyList<DestinationTarget> Targets, bool IsBoard)
    {
        public override string ToString() => DisplayName;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}
