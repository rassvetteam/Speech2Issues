using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Speech2Issues.App.Services;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Models;
using Speech2Issues.Core.Services;
using Speech2Issues.Core.Storage;

namespace Speech2Issues.App;

public partial class FirstRunWindow : Window
{
    private static readonly JsonSerializerOptions CloneOptions = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths;
    private readonly bool _recoveryMode;
    private readonly FrameworkElement[] _pages;
    private readonly int[] _stepSequence;
    private CancellationTokenSource _cancellation = new();
    private int _sequenceIndex;
    private bool _busy;
    private bool _transitioning;
    private bool _installationFailed;
    private bool _loadingModels;
    private bool _providerSelectionReady;

    public FirstRunWindow(AppPaths paths, AppSettings settings, AppSecrets secrets, bool recoveryMode)
    {
        _paths = paths;
        _recoveryMode = recoveryMode;
        Settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, CloneOptions), CloneOptions) ?? new AppSettings();
        Secrets = JsonSerializer.Deserialize<AppSecrets>(JsonSerializer.Serialize(secrets, CloneOptions), CloneOptions) ?? new AppSecrets();
        InitializeComponent();

        _pages = [WelcomePage, RuntimePage, SpeechPage, ProviderPage, ConnectionPage, InstallPage, SuccessPage];
        _stepSequence = recoveryMode ? [1, 2, 5, 6] : [0, 1, 2, 3, 4, 5, 6];

        var hasNvidia = HardwareDetection.HasNvidiaGpu();
        RuntimeCombo.ItemsSource = new[]
        {
            new RuntimeChoice(
                WhisperRuntimeKind.Cuda,
                "CUDA для NVIDIA",
                "Максимальная скорость на совместимой видеокарте; CPU устанавливается как запасной вариант.",
                "около 155 МБ"),
            new RuntimeChoice(
                WhisperRuntimeKind.Cpu,
                "Процессор (CPU)",
                "Работает на любом компьютере и требует меньше загрузки, но распознаёт медленнее.",
                "около 18 МБ"),
        };
        var preferredRuntime = settings.SetupVersion == 0 && hasNvidia
            ? WhisperRuntimeKind.Cuda
            : settings.SpeechRecognition.Runtime;
        RuntimeCombo.SelectedItem = ((IEnumerable<RuntimeChoice>)RuntimeCombo.ItemsSource).First(item => item.Kind == preferredRuntime);
        HardwareHintText.Text = hasNvidia
            ? "Обнаружена видеокарта NVIDIA — рекомендуется CUDA."
            : "NVIDIA не обнаружена — рекомендуется CPU.";

        WhisperModelCombo.ItemsSource = ModelChoice.All;
        WhisperModelCombo.SelectedItem = ModelChoice.All.FirstOrDefault(item => item.Id == settings.SpeechRecognition.Model) ?? ModelChoice.All[0];
        ProviderCombo.ItemsSource = ProviderChoice.All;
        ProviderCombo.SelectedItem = ProviderChoice.All.First(item => item.Kind == settings.AiProvider.Kind);
        ProviderUrlBox.Text = settings.AiProvider.BaseUrl;
        ProviderModelCombo.Text = settings.AiProvider.Model;
        UpdateStoredTokenText();
        InstallProgress.IsIndeterminate = false;

        if (recoveryMode)
        {
            HeaderModeText.Text = "Восстановление компонентов";
            InstallTitleText.Text = "Восстанавливаем компоненты";
            SuccessSummaryText.Text = "Недостающие компоненты восстановлены. Остальные настройки не изменились.";
        }

        foreach (var page in _pages) page.Visibility = Visibility.Collapsed;
        CurrentPage.Visibility = Visibility.Visible;
        SetupProgress.Maximum = Math.Max(1, _stepSequence.Length - 1);
        _providerSelectionReady = true;
        UpdateNavigation();
        Loaded += (_, _) => AnimatePageIn(CurrentPage, 34);
    }

    public AppSettings Settings { get; }
    public AppSecrets Secrets { get; }
    private int CurrentStep => _stepSequence[_sequenceIndex];
    private FrameworkElement CurrentPage => _pages[CurrentStep];

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_providerSelectionReady || ProviderCombo.SelectedItem is not ProviderChoice choice) return;
        ProviderUrlBox.Text = choice.Kind switch
        {
            AiProviderKind.Ollama => "http://127.0.0.1:11434",
            AiProviderKind.LmStudio => "http://127.0.0.1:1234/v1",
            _ => ProviderUrlBox.Text.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                 !ProviderUrlBox.Text.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                ? ProviderUrlBox.Text
                : "https://api.example.com/v1",
        };
        var currentModel = ProviderModelCombo.Text;
        ProviderModelCombo.ItemsSource = null;
        ProviderModelCombo.SelectedItem = null;
        ProviderModelCombo.Text = choice.Kind == AiProviderKind.Ollama && string.IsNullOrWhiteSpace(currentModel)
            ? "gemma4:12b"
            : currentModel;
        ConnectionTitleText.Text = $"Подключите {choice.DisplayName}";
        ProviderStatusText.Text = string.Empty;
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e) => await LoadProviderModelsAsync(openDropDown: true);

    private async Task LoadProviderModelsAsync(bool openDropDown)
    {
        if (_loadingModels) return;
        try
        {
            _loadingModels = true;
            UpdateNavigation();
            ApplyForm();
            RefreshModelsButton.IsEnabled = false;
            ProviderStatusText.Foreground = (Brush)FindResource("MutedBrush");
            ProviderStatusText.Text = "Проверяю провайдера и получаю модели…";
            using var client = DestinationFactory.CreateAiHttpClient(Settings, Secrets, TimeSpan.FromSeconds(30));
            var provider = DestinationFactory.CreateAiProvider(client, Settings);
            var models = await provider.ListModelsAsync(_cancellation.Token);
            var requestedModel = ProviderModelCombo.Text.Trim();
            ProviderModelCombo.ItemsSource = models;
            var selected = models.FirstOrDefault(model => string.Equals(model.Name, requestedModel, StringComparison.OrdinalIgnoreCase))
                ?? models.FirstOrDefault();
            if (selected is not null)
            {
                ProviderModelCombo.SelectedItem = selected;
                ProviderModelCombo.Text = selected.Name;
            }
            ProviderStatusText.Text = models.Count == 0
                ? "Провайдер не вернул список моделей — ID можно ввести вручную."
                : $"Провайдер доступен. Найдено моделей: {models.Count}.";
            if (openDropDown && models.Count > 0)
            {
                ProviderModelCombo.Focus();
                ProviderModelCombo.IsDropDownOpen = true;
            }
        }
        catch (Exception ex)
        {
            ProviderStatusText.Foreground = (Brush)FindResource("DangerBrush");
            ProviderStatusText.Text = "Провайдер недоступен: " + ex.Message;
        }
        finally
        {
            _loadingModels = false;
            RefreshModelsButton.IsEnabled = true;
            UpdateNavigation();
        }
    }

    private void ClearToken_Click(object sender, RoutedEventArgs e)
    {
        ProviderTokenBox.Clear();
        Secrets.AiApiToken = string.Empty;
        UpdateStoredTokenText();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _transitioning) return;
        if (CurrentStep == 6)
        {
            DialogResult = true;
            return;
        }
        if (CurrentStep == 5 && _installationFailed)
        {
            await InstallAsync();
            return;
        }

        try
        {
            ValidateCurrentStep();
        }
        catch (Exception ex)
        {
            ShowCurrentStepError(ex.Message);
            return;
        }

        if (_sequenceIndex >= _stepSequence.Length - 1) return;
        await NavigateAsync(_sequenceIndex + 1, 1);
        if (CurrentStep == 4) await LoadProviderModelsAsync(openDropDown: true);
        if (CurrentStep == 5) await InstallAsync();
    }

    private async void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _transitioning || _sequenceIndex == 0) return;
        await NavigateAsync(_sequenceIndex - 1, -1);
    }

    private void ValidateCurrentStep()
    {
        switch (CurrentStep)
        {
            case 1 when RuntimeCombo.SelectedItem is null:
                throw new InvalidOperationException("Выберите способ ускорения Whisper.");
            case 2 when WhisperModelCombo.SelectedItem is null:
                throw new InvalidOperationException("Выберите модель распознавания речи.");
            case 3 when ProviderCombo.SelectedItem is null:
                throw new InvalidOperationException("Выберите провайдера ИИ.");
            case 4:
                ApplyForm();
                break;
        }
    }

    private void ShowCurrentStepError(string message)
    {
        if (CurrentStep == 4)
        {
            ProviderStatusText.Foreground = (Brush)FindResource("DangerBrush");
            ProviderStatusText.Text = message;
        }
    }

    private async Task InstallAsync()
    {
        try
        {
            ApplyForm();
            _busy = true;
            _installationFailed = false;
            SetControlsEnabled(false);
            StartSpinner();
            StatusText.Foreground = (Brush)FindResource("MutedBrush");
            StatusText.Text = "Проверяю выбранные компоненты…";
            InstallProgress.Value = 0;

            using var downloadClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            var installer = new WhisperComponentInstaller(_paths, downloadClient);
            var componentProgress = new Progress<ComponentInstallProgress>(progress =>
            {
                StatusText.Text = progress.Message;
                if (progress.Percentage is { } percentage)
                {
                    InstallProgress.IsIndeterminate = false;
                    InstallProgress.Value = percentage;
                }
                else
                {
                    InstallProgress.IsIndeterminate = true;
                }
            });
            await installer.EnsureRuntimeAsync(Settings.SpeechRecognition.Runtime, componentProgress, _cancellation.Token);

            InstallProgress.IsIndeterminate = true;
            var speechProgress = new Progress<SpeechRecognitionProgress>(progress => StatusText.Text = progress.Message);
            var transcriber = new WhisperTranscriber(_paths.ModelsDirectory, Settings.SpeechRecognition, _paths.RuntimeDirectory);
            await transcriber.DownloadModelAsync(speechProgress, _cancellation.Token);

            StatusText.Text = $"Проверяю {Settings.SpeechRecognition.Runtime} runtime…";
            var runtimeError = await RunWhisperSelfTestAsync(Settings.SpeechRecognition.Runtime, _cancellation.Token);
            if (runtimeError is not null)
            {
                if (Settings.SpeechRecognition.Runtime != WhisperRuntimeKind.Cuda)
                    throw new InvalidOperationException("CPU runtime Whisper не прошёл самопроверку: " + SummarizeSelfTestError(runtimeError));

                Settings.SpeechRecognition.Runtime = WhisperRuntimeKind.Cpu;
                RuntimeCombo.SelectedItem = ((IEnumerable<RuntimeChoice>)RuntimeCombo.ItemsSource).First(item => item.Kind == WhisperRuntimeKind.Cpu);
                StatusText.Text = "CUDA недоступна — безопасно переключаюсь на CPU…";
                var cpuError = await RunWhisperSelfTestAsync(WhisperRuntimeKind.Cpu, _cancellation.Token);
                if (cpuError is not null)
                    throw new InvalidOperationException($"Ни CUDA, ни CPU runtime Whisper не прошли самопроверку. CPU: {SummarizeSelfTestError(cpuError)}");
            }

            IAiTaskProvider? provider = null;
            if (!_recoveryMode)
            {
                StatusText.Text = "Проверяю подключение и структурированный ответ ИИ…";
                using var aiClient = DestinationFactory.CreateAiHttpClient(Settings, Secrets, TimeSpan.FromMinutes(3));
                provider = DestinationFactory.CreateAiProvider(aiClient, Settings);
                var check = await provider.CheckAsync(_cancellation.Token);
                if (!check.IsSuccess) throw new InvalidOperationException(check.Message);
                var drafts = await provider.BuildDraftsAsync("Проверка подключения: создать тестовую задачу без отправки.", _cancellation.Token);
                if (drafts.Count == 0) throw new InvalidDataException("Провайдер не вернул тестовую задачу.");
            }

            Settings.SetupVersion = WhisperComponentInstaller.CurrentSetupVersion;
            await installer.SaveManifestAsync(Settings, _cancellation.Token);
            InstallProgress.IsIndeterminate = false;
            InstallProgress.Value = 100;
            SuccessDetailsText.Text = provider is null
                ? $"Whisper {Settings.SpeechRecognition.Model} · {Settings.SpeechRecognition.Runtime}"
                : $"Whisper {Settings.SpeechRecognition.Model} · {provider.DisplayName} · {provider.Model}";
            StopSpinner();
            _busy = false;
            SetControlsEnabled(true);
            await NavigateAsync(_sequenceIndex + 1, 1);
        }
        catch (OperationCanceledException)
        {
            StopSpinner();
            _busy = false;
            DialogResult = false;
        }
        catch (Exception ex)
        {
            StopSpinner();
            _busy = false;
            _installationFailed = true;
            InstallProgress.IsIndeterminate = false;
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
            StatusText.Text = "Не удалось завершить настройку: " + ex.Message;
            SetControlsEnabled(true);
            UpdateNavigation();
        }
    }

    private void ApplyForm()
    {
        if (RuntimeCombo.SelectedItem is not RuntimeChoice runtime) throw new InvalidOperationException("Выберите Whisper runtime.");
        if (WhisperModelCombo.SelectedItem is not ModelChoice speechModel) throw new InvalidOperationException("Выберите модель Whisper.");
        if (ProviderCombo.SelectedItem is not ProviderChoice provider) throw new InvalidOperationException("Выберите провайдера ИИ.");
        var url = ProviderUrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Укажите корректный HTTP/HTTPS Base URL провайдера.");
        if (string.IsNullOrWhiteSpace(ProviderModelCombo.Text)) throw new InvalidOperationException("Укажите модель ИИ.");

        Settings.SpeechRecognition.Runtime = runtime.Kind;
        Settings.SpeechRecognition.Model = speechModel.Id;
        Settings.AiProvider.Kind = provider.Kind;
        Settings.AiProvider.BaseUrl = url.TrimEnd('/');
        Settings.AiProvider.Model = ProviderModelCombo.Text.Trim();
        if (!string.IsNullOrWhiteSpace(ProviderTokenBox.Password)) Secrets.AiApiToken = ProviderTokenBox.Password;
    }

    private async Task NavigateAsync(int newSequenceIndex, int direction)
    {
        if (_transitioning || newSequenceIndex < 0 || newSequenceIndex >= _stepSequence.Length) return;
        _transitioning = true;
        var outgoing = CurrentPage;
        AnimatePageOut(outgoing, direction > 0 ? -54 : 54);
        await Task.Delay(150);
        outgoing.Visibility = Visibility.Collapsed;
        ResetPageAnimation(outgoing);

        _sequenceIndex = newSequenceIndex;
        var incoming = CurrentPage;
        incoming.Visibility = Visibility.Visible;
        AnimatePageIn(incoming, direction > 0 ? 54 : -54);
        AnimateSetupProgress();
        UpdateNavigation();
        await Task.Delay(220);
        _transitioning = false;
        UpdateNavigation();
    }

    private static void AnimatePageOut(FrameworkElement page, double targetX)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = easing });
        ((TranslateTransform)page.RenderTransform).BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(0, targetX, TimeSpan.FromMilliseconds(160)) { EasingFunction = easing });
    }

    private static void AnimatePageIn(FrameworkElement page, double fromX)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = easing });
        ((TranslateTransform)page.RenderTransform).BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = easing });
    }

    private static void ResetPageAnimation(FrameworkElement page)
    {
        page.BeginAnimation(OpacityProperty, null);
        page.Opacity = 1;
        var transform = (TranslateTransform)page.RenderTransform;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
    }

    private void AnimateSetupProgress()
    {
        SetupProgress.BeginAnimation(
            RangeBase.ValueProperty,
            new DoubleAnimation(SetupProgress.Value, _sequenceIndex, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void UpdateNavigation()
    {
        BackButton.Visibility = _sequenceIndex == 0 || CurrentStep is 5 or 6 ? Visibility.Collapsed : Visibility.Visible;
        BackButton.IsEnabled = !_busy && !_transitioning;
        NextButton.IsEnabled = !_busy && !_transitioning && !_loadingModels;
        NextButton.Visibility = Visibility.Visible;
        NextButton.Content = CurrentStep switch
        {
            0 => "Начать",
            4 => "Установить",
            5 when _installationFailed => "Повторить",
            5 => "Устанавливаю…",
            6 => "Открыть Speech2Issues",
            2 when _recoveryMode => "Восстановить",
            _ => "Далее",
        };
        if (CurrentStep == 5 && !_installationFailed) NextButton.Visibility = Visibility.Collapsed;
        if (CurrentStep == 5 && _installationFailed) BackButton.Visibility = _sequenceIndex > 0 ? Visibility.Visible : Visibility.Collapsed;

        StepLabel.Text = CurrentStep switch
        {
            0 => "НАЧАЛО",
            5 => "УСТАНОВКА",
            6 => "ГОТОВО",
            _ => $"ШАГ {_sequenceIndex + 1} ИЗ {_stepSequence.Length}",
        };
    }

    private void StartSpinner()
    {
        ProgressSpinnerRotate.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            });
    }

    private void StopSpinner()
    {
        ProgressSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        ProgressSpinnerRotate.Angle = 0;
    }

    private async Task<string?> RunWhisperSelfTestAsync(WhisperRuntimeKind runtime, CancellationToken cancellationToken)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь приложения.");
        var resultPath = Path.Combine(_paths.DownloadsDirectory, $"whisper-selftest-{Guid.NewGuid():N}.txt");
        try
        {
            var start = new ProcessStartInfo(processPath) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--whisper-selftest");
            start.ArgumentList.Add(_paths.Root);
            start.ArgumentList.Add(Settings.SpeechRecognition.Model);
            start.ArgumentList.Add(runtime.ToString());
            start.ArgumentList.Add(resultPath);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить самопроверку Whisper.");
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            if (process.ExitCode == 0) return null;
            return File.Exists(resultPath)
                ? await File.ReadAllTextAsync(resultPath, cancellationToken)
                : $"дочерний процесс завершился с кодом {process.ExitCode}";
        }
        finally
        {
            if (File.Exists(resultPath)) File.Delete(resultPath);
        }
    }

    private static string SummarizeSelfTestError(string error)
    {
        var firstLine = error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(firstLine) ? "неизвестная ошибка" : firstLine;
    }

    private void UpdateStoredTokenText() => StoredTokenText.Text = string.IsNullOrWhiteSpace(Secrets.AiApiToken)
        ? "Токен не сохранён. Для локальных серверов поле можно оставить пустым."
        : "Сохранённый токен будет использован, если не введён новый.";

    private void SetControlsEnabled(bool enabled)
    {
        RuntimeCombo.IsEnabled = enabled;
        WhisperModelCombo.IsEnabled = enabled;
        ProviderCombo.IsEnabled = enabled;
        ProviderUrlBox.IsEnabled = enabled;
        ProviderModelCombo.IsEnabled = enabled;
        ProviderTokenBox.IsEnabled = enabled;
        RefreshModelsButton.IsEnabled = enabled;
        ClearTokenButton.IsEnabled = enabled;
        CloseButton.IsEnabled = true;
        UpdateNavigation();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            StatusText.Text = "Останавливаю настройку…";
            _cancellation.Cancel();
        }
        else Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            _cancellation.Cancel();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private sealed record RuntimeChoice(WhisperRuntimeKind Kind, string DisplayName, string Description, string SizeLabel)
    {
        public override string ToString() => $"{DisplayName} · {SizeLabel}\n{Description}";
    }

    private sealed record ModelChoice(string Id, string DisplayName, string Description, string SizeLabel)
    {
        public static readonly ModelChoice[] All =
        [
            new("LargeV3Turbo", "LargeV3Turbo", "Лучшее качество для русской и смешанной речи · рекомендуется", "≈ 1,6 ГБ"),
            new("Medium", "Medium", "Высокая точность, но медленнее LargeV3Turbo", "≈ 1,5 ГБ"),
            new("Small", "Small", "Баланс качества, скорости и размера", "≈ 466 МБ"),
            new("Base", "Base", "Быстрая загрузка для коротких и чётких записей", "≈ 142 МБ"),
            new("Tiny", "Tiny", "Минимальный размер для быстрых заметок", "≈ 75 МБ"),
        ];
    }

    private sealed record ProviderChoice(AiProviderKind Kind, string DisplayName, string Description, string Glyph)
    {
        public static readonly ProviderChoice[] All =
        [
            new(AiProviderKind.Ollama, "Ollama", "Локальные модели через нативный API", "\uE756"),
            new(AiProviderKind.LmStudio, "LM Studio", "Локальный OpenAI-compatible сервер", "\uE943"),
            new(AiProviderKind.OpenAiCompatible, "API с токеном", "Облачный или собственный endpoint", "\uE753"),
        ];
    }
}
