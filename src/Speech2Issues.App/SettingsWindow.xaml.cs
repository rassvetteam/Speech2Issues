using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Microsoft.Win32;
using Speech2Issues.App.Audio;
using Speech2Issues.App.Services;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Models;
using Speech2Issues.Core.Services;
using Speech2Issues.Core.Storage;

namespace Speech2Issues.App;

public partial class SettingsWindow : Window
{
    private static readonly JsonSerializerOptions CloneOptions = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths;
    private readonly int _createdTaskCount;
    private bool _capturingHotKey;
    private bool _themeSelectionReady;
    private bool _providerSelectionReady;
    private IReadOnlyList<AiModelInfo> _models = [];

    public SettingsWindow(AppPaths paths, AppSettings settings, AppSecrets secrets, int createdTaskCount)
    {
        _paths = paths;
        _createdTaskCount = createdTaskCount;
        Settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, CloneOptions), CloneOptions) ?? new AppSettings();
        Settings.Theme = ThemeService.Normalize(Settings.Theme, _createdTaskCount);
        Secrets = JsonSerializer.Deserialize<AppSecrets>(JsonSerializer.Serialize(secrets, CloneOptions), CloneOptions) ?? new AppSecrets();
        InitializeComponent();
        Icon = ThemeService.GetIcon(Settings.Theme);
        ThemeCombo.ItemsSource = ThemeService.GetAvailableThemes(_createdTaskCount);
        WhisperModelCombo.ItemsSource = new[] { "Tiny", "Base", "Small", "Medium", "LargeV3", "LargeV3Turbo" };
        AiProviderCombo.ItemsSource = AiProviderChoice.All;
        ObsidianProfileCombo.ItemsSource = new[] { "ProjectManager", "TaskNotes", "Tasks", "Markdown" };
        PopulateForm();
        _themeSelectionReady = true;
        _providerSelectionReady = true;
        Loaded += async (_, _) => await LoadModelsAsync();
    }

    public AppSettings Settings { get; private set; }
    public AppSecrets Secrets { get; private set; }
    public event Action<string>? ThemeChanged;

    private void PopulateForm()
    {
        CloseToTrayCheck.IsChecked = Settings.CloseToTray; StartMinimizedCheck.IsChecked = Settings.StartMinimized;
        ThemeCombo.SelectedValue = ThemeService.Normalize(Settings.Theme, _createdTaskCount);
        OutputLanguageBox.Text = Settings.OutputLanguage; CountdownBox.Text = Settings.CountdownSeconds.ToString();
        HotKeyBox.Text = Settings.Hotkey.Display; WhisperModelCombo.SelectedItem = Settings.SpeechRecognition.Model; WhisperLanguageBox.Text = Settings.SpeechRecognition.Language;
        WhisperUnloadDelayBox.Text = Settings.SpeechRecognition.ModelUnloadDelayMinutes.ToString();
        AiProviderCombo.SelectedItem = AiProviderChoice.All.First(x => x.Kind == Settings.AiProvider.Kind);
        AiUrlBox.Text = Settings.AiProvider.BaseUrl;
        AiModelBox.Text = Settings.AiProvider.Model;
        AiTokenStatusText.Text = string.IsNullOrWhiteSpace(Secrets.AiApiToken) ? "Токен не сохранён" : "Токен сохранён в Windows DPAPI";
        var microphones = AudioDeviceService.GetMicrophones(); var playback = AudioDeviceService.GetPlaybackDevices();
        MicrophoneCombo.ItemsSource = microphones; PlaybackCombo.ItemsSource = playback;
        MicrophoneCombo.SelectedItem = microphones.FirstOrDefault(x => x.Id == Settings.Audio.MicrophoneDeviceId) ?? microphones.FirstOrDefault();
        PlaybackCombo.SelectedItem = playback.FirstOrDefault(x => x.Id == Settings.Audio.PlaybackDeviceId) ?? playback.FirstOrDefault();
        PlankaUrlBox.Text = Settings.Planka.BaseUrl; GitHubRepoBox.Text = Settings.GitHub.Repository;
        VaultPathBox.Text = Settings.Obsidian.VaultPath; ObsidianProfileCombo.SelectedItem = Settings.Obsidian.Profile;
        WebhookUrlBox.Text = Settings.Webhook.Url; WebhookHeadersBox.Text = FormatHeaders(Settings.Webhook.Headers);
    }

    private void ApplyForm()
    {
        Settings.CloseToTray = CloseToTrayCheck.IsChecked == true; Settings.StartMinimized = StartMinimizedCheck.IsChecked == true;
        Settings.Theme = ThemeService.Normalize(ThemeCombo.SelectedValue?.ToString(), _createdTaskCount);
        Settings.OutputLanguage = string.IsNullOrWhiteSpace(OutputLanguageBox.Text) ? "Russian" : OutputLanguageBox.Text.Trim();
        Settings.CountdownSeconds = int.TryParse(CountdownBox.Text, out var seconds) ? Math.Clamp(seconds, 1, 60) : 5;
        Settings.SpeechRecognition.Model = WhisperModelCombo.SelectedItem?.ToString() ?? "LargeV3Turbo";
        Settings.SpeechRecognition.Language = string.IsNullOrWhiteSpace(WhisperLanguageBox.Text) ? "auto" : WhisperLanguageBox.Text.Trim();
        if (!int.TryParse(WhisperUnloadDelayBox.Text, out var unloadDelayMinutes) || unloadDelayMinutes is < 0 or > 1440)
            throw new InvalidOperationException("Время удержания модели должно быть от 0 до 1440 минут.");
        Settings.SpeechRecognition.ModelUnloadDelayMinutes = unloadDelayMinutes;
        Settings.AiProvider.Kind = (AiProviderCombo.SelectedItem as AiProviderChoice)?.Kind ?? AiProviderKind.Ollama;
        Settings.AiProvider.BaseUrl = AiUrlBox.Text.Trim().TrimEnd('/');
        Settings.AiProvider.Model = string.IsNullOrWhiteSpace(AiModelBox.Text) ? Settings.AiProvider.Model : AiModelBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(AiTokenBox.Password)) Secrets.AiApiToken = AiTokenBox.Password;
        if (MicrophoneCombo.SelectedItem is AudioDeviceInfo mic) Settings.Audio.MicrophoneDeviceId = mic.Id;
        if (PlaybackCombo.SelectedItem is AudioDeviceInfo playback) Settings.Audio.PlaybackDeviceId = playback.Id;
        Settings.Planka.BaseUrl = PlankaUrlBox.Text.Trim(); Settings.GitHub.Repository = GitHubRepoBox.Text.Trim();
        Settings.Obsidian.VaultPath = VaultPathBox.Text.Trim(); Settings.Obsidian.Profile = ObsidianProfileCombo.SelectedItem?.ToString() ?? "ProjectManager";
        Settings.Webhook.Url = WebhookUrlBox.Text.Trim(); Settings.Webhook.Headers = ParseHeaders(WebhookHeadersBox.Text);
        if (!string.IsNullOrWhiteSpace(PlankaKeyBox.Password)) Secrets.PlankaApiKey = PlankaKeyBox.Password;
        if (!string.IsNullOrWhiteSpace(GitHubTokenBox.Password)) Secrets.GitHubToken = GitHubTokenBox.Password;
        if (!string.IsNullOrWhiteSpace(WebhookSecretHeadersBox.Text)) Secrets.WebhookHeaders = ParseHeaders(WebhookSecretHeadersBox.Text);
    }

    private async Task LoadModelsAsync()
    {
        CheckStatusText.Text = "Загружаю модели провайдера…";
        try
        {
            ApplyAiForm();
            using var client = DestinationFactory.CreateAiHttpClient(Settings, Secrets, TimeSpan.FromSeconds(30));
            var provider = DestinationFactory.CreateAiProvider(client, Settings);
            _models = await provider.ListModelsAsync();
            AiModelsCombo.ItemsSource = _models;
            AiModelsCombo.SelectedItem = _models.FirstOrDefault(x => string.Equals(x.Name, Settings.AiProvider.Model, StringComparison.OrdinalIgnoreCase));
            AiModelsCombo.Visibility = _models.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            CheckStatusText.Text = _models.Count == 0 ? "Список моделей пуст; ID можно ввести вручную." : string.Empty;
        }
        catch (Exception ex)
        {
            _models = [];
            AiModelsCombo.ItemsSource = _models;
            AiModelsCombo.Visibility = Visibility.Collapsed;
            CheckStatusText.Text = ex.Message;
        }
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e) => await LoadModelsAsync();
    private async void TestAi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyForm();
            using var client = DestinationFactory.CreateAiHttpClient(Settings, Secrets, TimeSpan.FromMinutes(3));
            var provider = DestinationFactory.CreateAiProvider(client, Settings);
            var check = await provider.CheckAsync();
            if (!check.IsSuccess) throw new InvalidOperationException(check.Message);
            var drafts = await provider.BuildDraftsAsync("Проверка подключения: создать тестовую задачу без отправки.");
            CheckStatusText.Text = drafts.Count > 0 ? $"{provider.DisplayName} · {provider.Model}: проверка успешна." : "Провайдер не вернул тестовую задачу.";
        }
        catch (Exception ex) { CheckStatusText.Text = ex.Message; }
    }
    private async void TestWhisper_Click(object sender, RoutedEventArgs e) { ApplyForm(); try { var progress = new Progress<SpeechRecognitionProgress>(x => CheckStatusText.Text = x.Message); CheckStatusText.Text = await new WhisperTranscriber(_paths.ModelsDirectory, Settings.SpeechRecognition, _paths.RuntimeDirectory).PrepareAsync(progress); } catch (Exception ex) { CheckStatusText.Text = ex.Message; } }
    private void AiModelsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (AiModelsCombo.SelectedItem is AiModelInfo model) AiModelBox.Text = model.Name; }
    private void AiProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_providerSelectionReady || AiProviderCombo.SelectedItem is not AiProviderChoice choice) return;
        AiUrlBox.Text = choice.Kind switch
        {
            AiProviderKind.Ollama => "http://127.0.0.1:11434",
            AiProviderKind.LmStudio => "http://127.0.0.1:1234/v1",
            _ => AiUrlBox.Text.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) ? "https://api.example.com/v1" : AiUrlBox.Text,
        };
        AiModelsCombo.Visibility = Visibility.Collapsed;
    }
    private void ClearAiToken_Click(object sender, RoutedEventArgs e) { AiTokenBox.Clear(); Secrets.AiApiToken = string.Empty; AiTokenStatusText.Text = "Токен удалён"; }
    private void ApplyAiForm()
    {
        Settings.AiProvider.Kind = (AiProviderCombo.SelectedItem as AiProviderChoice)?.Kind ?? AiProviderKind.Ollama;
        Settings.AiProvider.BaseUrl = AiUrlBox.Text.Trim().TrimEnd('/');
        Settings.AiProvider.Model = AiModelBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(AiTokenBox.Password)) Secrets.AiApiToken = AiTokenBox.Password;
    }
    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_themeSelectionReady || ThemeCombo.SelectedValue is not string themeId) return;
        Settings.Theme = ThemeService.Normalize(themeId, _createdTaskCount);
        ThemeService.Apply(Settings.Theme, _createdTaskCount);
        ThemeChanged?.Invoke(Settings.Theme);
        CheckStatusText.Text = $"Тема «{ThemeService.Find(Settings.Theme, _createdTaskCount).DisplayName}» применена и сохранена.";
    }
    private void NavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (GeneralPanel is null) return; var panels = new[] { GeneralPanel, AudioPanel, AiPanel, ConnectionsPanel }; for (var i = 0; i < panels.Length; i++) panels[i].Visibility = NavigationList.SelectedIndex == i ? Visibility.Visible : Visibility.Collapsed; }

    private void CaptureHotKey_Click(object sender, RoutedEventArgs e) { _capturingHotKey = !_capturingHotKey; CaptureHotKeyButton.Content = _capturingHotKey ? "Нажмите…" : "Изменить"; HotKeyBox.Text = _capturingHotKey ? "Ожидание сочетания…" : Settings.Hotkey.Display; CaptureHotKeyButton.Focus(); }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotKey) return; var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = Keyboard.Modifiers; e.Handled = true;
        if (modifiers == ModifierKeys.None) { HotKeyBox.Text = "Нужен Ctrl, Alt, Shift или Win."; return; }
        Settings.Hotkey.Ctrl = modifiers.HasFlag(ModifierKeys.Control); Settings.Hotkey.Alt = modifiers.HasFlag(ModifierKeys.Alt); Settings.Hotkey.Shift = modifiers.HasFlag(ModifierKeys.Shift); Settings.Hotkey.Win = modifiers.HasFlag(ModifierKeys.Windows); Settings.Hotkey.Key = key.ToString();
        _capturingHotKey = false; CaptureHotKeyButton.Content = "Изменить"; HotKeyBox.Text = Settings.Hotkey.Display;
    }

    private void BrowseVault_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog { Title = "Выберите Obsidian vault" }; if (dialog.ShowDialog(this) == true) VaultPathBox.Text = dialog.FolderName; }
    private void Save_Click(object sender, RoutedEventArgs e) { try { ApplyForm(); if (!Uri.TryCreate(Settings.AiProvider.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("Некорректный URL провайдера ИИ."); if (string.IsNullOrWhiteSpace(Settings.AiProvider.Model)) throw new InvalidOperationException("Укажите модель ИИ."); DialogResult = true; } catch (Exception ex) { CheckStatusText.Text = ex.Message; } }
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
    private static Dictionary<string, string> ParseHeaders(string text) { var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { var split = line.IndexOf(':'); if (split <= 0) throw new FormatException($"Некорректный заголовок: {line}"); result[line[..split].Trim()] = line[(split + 1)..].Trim(); } return result; }
    private static string FormatHeaders(IReadOnlyDictionary<string, string> headers) => string.Join(Environment.NewLine, headers.Select(x => $"{x.Key}: {x.Value}"));

    private sealed record AiProviderChoice(AiProviderKind Kind, string DisplayName)
    {
        public static readonly AiProviderChoice[] All =
        [
            new(AiProviderKind.Ollama, "Ollama"),
            new(AiProviderKind.LmStudio, "LM Studio"),
            new(AiProviderKind.OpenAiCompatible, "OpenAI-compatible API"),
        ];
    }
}
