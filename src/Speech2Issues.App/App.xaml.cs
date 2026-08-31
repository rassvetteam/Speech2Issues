using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using System.Windows.Interop;
using System.IO;
using System.Net.Http;
using Speech2Issues.App.Services;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Storage;
using Speech2Issues.Core.Services;

namespace Speech2Issues.App;

public partial class App : Application
{
    private AppPaths _paths = null!;
    private SettingsStore _settingsStore = null!;
    private HistoryRepository _history = null!;
    private AppSettings _settings = null!;
    private AppSecrets _secrets = null!;
    private MainWindow? _mainWindow;
    private TrayIconService? _tray;
    private HotKeyService? _hotKey;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--whisper-selftest", StringComparison.Ordinal))
        {
            await RunWhisperSelfTestAsync(e.Args);
            return;
        }
        try
        {
            _paths = new AppPaths();
            _paths.EnsureCreated();
            _settingsStore = new SettingsStore(_paths);
            _history = new HistoryRepository(_paths);
            await _history.InitializeAsync();
            _settings = await _settingsStore.LoadSettingsAsync();
            _secrets = await _settingsStore.LoadSecretsAsync();
            var settingsChanged = SettingsMigration.Migrate(_settings);
            if (_settings.CountdownSeconds == 10)
            {
                _settings.CountdownSeconds = 5;
                settingsChanged = true;
            }
            var createdTaskCount = await _history.CountCreatedTasksAsync();
            var availableTheme = ThemeService.Normalize(_settings.Theme, createdTaskCount);
            if (!string.Equals(_settings.Theme, availableTheme, StringComparison.OrdinalIgnoreCase))
            {
                _settings.Theme = availableTheme;
                settingsChanged = true;
            }
            if (settingsChanged)
            {
                await _settingsStore.SaveSettingsAsync(_settings);
            }

            ThemeService.Apply(_settings.Theme, createdTaskCount);
            using (var componentClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            {
                var componentInstaller = new WhisperComponentInstaller(_paths, componentClient);
                if (!componentInstaller.IsSetupComplete(_settings))
                {
                    var setupWindow = new FirstRunWindow(
                        _paths,
                        _settings,
                        _secrets,
                        recoveryMode: _settings.SetupVersion >= WhisperComponentInstaller.CurrentSetupVersion);
                    MainWindow = setupWindow;
                    if (setupWindow.ShowDialog() != true)
                    {
                        Shutdown();
                        return;
                    }

                    _settings = setupWindow.Settings;
                    _secrets = setupWindow.Secrets;
                    await _settingsStore.SaveSettingsAsync(_settings);
                    await _settingsStore.SaveSecretsAsync(_secrets);
                }
            }

            _mainWindow = new MainWindow(_paths, _settingsStore, _history, _settings, _secrets, createdTaskCount);
            MainWindow = _mainWindow;
            _mainWindow.Closed += (_, _) => Shutdown();
            _mainWindow.Show();

            _tray = new TrayIconService(_settings.Theme);
            _tray.ShowRequested += (_, _) => _mainWindow.ShowFromTray();
            _tray.ToggleRecordingRequested += (_, _) => _mainWindow.ToggleRecording();
            _tray.SettingsRequested += (_, _) => _mainWindow.OpenSettings();
            _tray.ExitRequested += (_, _) => ExitApplication();

            _mainWindow.RecordingStateChanged += (_, recording) => _tray.SetRecording(recording);
            _mainWindow.NotificationRequested += (_, message) => _tray.ShowBalloon("Speech2Issues", message);
            _mainWindow.HotKeyChanged += (_, _) => ReapplyHotKey();

            _hotKey = new HotKeyService();
            _hotKey.Attach(new WindowInteropHelper(_mainWindow).Handle);
            _hotKey.Pressed += (_, _) => _mainWindow.ToggleRecording();
            ReapplyHotKey();

            SessionEnding += (_, _) => _mainWindow.RequestClose();

            if (_settings.StartMinimized)
            {
                _mainWindow.ActivateLastProjectForTray();
                _mainWindow.HideToTray();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Speech2Issues: ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task RunWhisperSelfTestAsync(string[] args)
    {
        var exitCode = 1;
        string? resultPath = args.Length >= 5 ? args[4] : null;
        try
        {
            if (args.Length < 5) throw new ArgumentException("Недостаточно аргументов самопроверки Whisper.");
            var paths = new AppPaths(args[1]);
            if (!Enum.TryParse<WhisperRuntimeKind>(args[3], true, out var runtime))
                throw new ArgumentException("Неизвестный Whisper runtime.");
            using var migrationClient = new HttpClient();
            new WhisperComponentInstaller(paths, migrationClient).MigrateInstalledRuntimeLayout(runtime);
            var transcriber = new WhisperTranscriber(
                paths.ModelsDirectory,
                new SpeechRecognitionSettings { Model = args[2], Runtime = runtime, Language = "auto" },
                paths.RuntimeDirectory);
            var message = await transcriber.PrepareAsync();
            await AtomicFile.WriteTextAsync(resultPath!, message);
            exitCode = 0;
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(resultPath))
            {
                try { await AtomicFile.WriteTextAsync(resultPath, ex.ToString()); } catch { }
            }
        }
        finally
        {
            Shutdown(exitCode);
        }
    }

    private void ReapplyHotKey()
    {
        _hotKey?.Register(_settings.Hotkey);
    }

    private void ExitApplication()
    {
        if (_mainWindow is not null)
        {
            _mainWindow.RequestClose();
        }
        else
        {
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _hotKey?.Dispose();
        base.OnExit(e);
    }
}
