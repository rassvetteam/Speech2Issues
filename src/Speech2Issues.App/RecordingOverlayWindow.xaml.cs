using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Speech2Issues.Core.Models;
using MediaColor = System.Windows.Media.Color;

namespace Speech2Issues.App;

public partial class RecordingOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private readonly DispatcherTimer _recordingTimer;
    private readonly ScaleTransform[] _barScales;
    private Storyboard? _hideStoryboard;
    private DateTimeOffset _recordingStartedAt;
    private bool _isHiding;

    public RecordingOverlayWindow()
    {
        InitializeComponent();
        _barScales = [BarScale0, BarScale1, BarScale2, BarScale3, BarScale4, BarScale5, BarScale6];
        _recordingTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateDuration(), Dispatcher);
    }

    public event EventHandler? SendRequested;
    public event EventHandler? EditRequested;
    public event EventHandler? CancelRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var currentStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new(currentStyle | WsExNoActivate | WsExToolWindow));
    }

    public void ShowRecording()
    {
        StopTransientTimers();
        SetPanel(RecordingPanel);
        _recordingStartedAt = DateTimeOffset.UtcNow;
        RecordingDurationText.Text = "00:00";
        _recordingTimer.Start();
        ((Storyboard)FindResource("RecordingPulse")).Begin(this, true);
        ShowOverlay();
    }

    public void ShowProcessing(string message)
    {
        _recordingTimer.Stop();
        ((Storyboard)FindResource("RecordingPulse")).Remove(this);
        ProcessingText.Text = message;
        SetPanel(ProcessingPanel);
        ((Storyboard)FindResource("ProcessingSpin")).Begin(this, true);
        ShowOverlay();
    }

    public void UpdateProcessing(string message)
    {
        if (ProcessingPanel.Visibility != Visibility.Visible)
        {
            ShowProcessing(message);
            return;
        }
        ProcessingText.Text = message;
    }

    public void ShowDrafts(IReadOnlyList<TaskDraft> drafts, string destination, int countdownSeconds)
    {
        ((Storyboard)FindResource("ProcessingSpin")).Remove(this);
        var first = drafts[0];
        DraftTitleText.Text = drafts.Count == 1 ? first.Title : $"Подготовлено задач: {drafts.Count}";
        DraftDescriptionText.Text = drafts.Count == 1
            ? Shorten(first.Description, 180)
            : Shorten(string.Join(" · ", drafts.Select(x => x.Title)), 180);
        DraftDestinationText.Text = destination;
        CountdownProgress.Maximum = Math.Max(1, countdownSeconds);
        UpdateCountdown(countdownSeconds);
        SetPanel(DraftPanel);
        ShowOverlay();
    }

    public void UpdateCountdown(int seconds)
    {
        CountdownText.Text = $"Автоотправка через {Math.Max(0, seconds)} сек.";
        CountdownProgress.Value = Math.Max(0, seconds);
    }

    public void ShowSending(string destination)
    {
        ShowProcessing($"Отправляю в {destination}…");
    }

    public void ShowSuccess(string message, int taskCount = 1)
    {
        ResultIconBorder.Background = new SolidColorBrush(MediaColor.FromRgb(33, 56, 47));
        ResultIconText.Foreground = new SolidColorBrush(MediaColor.FromRgb(105, 216, 174));
        ResultIconText.Text = "\uE73E";
        ResultTitleText.Text = taskCount == 1 ? "Задача отправлена" : "Задачи отправлены";
        ResultMessageText.Text = message;
        SetPanel(ResultPanel);
        ShowOverlay();
        HideAnimated(TimeSpan.FromSeconds(1.8));
    }

    public void ShowPartialSuccess(int sent, int total, string message)
    {
        SetPanel(ResultPanel);
        ResultIconBorder.Background = new SolidColorBrush(MediaColor.FromRgb(60, 45, 25));
        ResultIconText.Text = "\uE814";
        ResultIconText.Foreground = new SolidColorBrush(MediaColor.FromRgb(244, 190, 105));
        ResultTitleText.Text = $"Отправлено {sent} из {total}";
        ResultMessageText.Text = Shorten(message, 180);
        ShowOverlay();
        HideAnimated(TimeSpan.FromSeconds(8));
    }

    public void ShowCanceled(string message)
    {
        ResultIconBorder.Background = new SolidColorBrush(MediaColor.FromRgb(42, 46, 56));
        ResultIconText.Foreground = new SolidColorBrush(MediaColor.FromRgb(154, 164, 179));
        ResultIconText.Text = "\uE711";
        ResultTitleText.Text = "Отправка отменена";
        ResultMessageText.Text = message;
        SetPanel(ResultPanel);
        ShowOverlay();
        HideAnimated(TimeSpan.FromSeconds(1.35));
    }

    public void ShowError(string message)
    {
        StopTransientTimers();
        ResultIconBorder.Background = new SolidColorBrush(MediaColor.FromRgb(57, 33, 38));
        ResultIconText.Foreground = new SolidColorBrush(MediaColor.FromRgb(255, 128, 132));
        ResultIconText.Text = "\uEA39";
        ResultTitleText.Text = "Не удалось выполнить действие";
        ResultMessageText.Text = Shorten(message, 220);
        SetPanel(ResultPanel);
        ShowOverlay();
        HideAnimated(TimeSpan.FromSeconds(4));
    }

    public void UpdateAudioLevel(double level)
    {
        if (RecordingPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        var normalized = Math.Clamp(level, 0, 1);
        var wave = new[] { 0.45, 0.72, 0.9, 1.0, 0.86, 0.68, 0.42 };
        for (var i = 0; i < _barScales.Length; i++)
        {
            var target = 0.16 + normalized * wave[i] * 0.84;
            _barScales[i].BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(110))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                });
        }
    }

    public void HideAnimated(TimeSpan? delay = null)
    {
        if (!IsVisible || _isHiding)
        {
            return;
        }

        _isHiding = true;
        StopTransientTimers();
        var storyboard = new Storyboard { BeginTime = delay ?? TimeSpan.Zero };
        _hideStoryboard = storyboard;
        var opacity = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(opacity, RootCard);
        Storyboard.SetTargetProperty(opacity, new PropertyPath(OpacityProperty));
        storyboard.Children.Add(opacity);

        var movement = new DoubleAnimation(-32, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(movement, RootTranslate);
        Storyboard.SetTargetProperty(movement, new PropertyPath(TranslateTransform.YProperty));
        storyboard.Children.Add(movement);
        storyboard.Completed += (_, _) =>
        {
            Hide();
            _isHiding = false;
            _hideStoryboard = null;
        };
        storyboard.Begin(this, true);
    }

    public void CloseImmediately()
    {
        StopTransientTimers();
        Close();
    }

    private void ShowOverlay()
    {
        var wasVisible = IsVisible;
        _hideStoryboard?.Remove(this);
        _hideStoryboard = null;
        _isHiding = false;
        PositionAtTopCenter();
        if (!wasVisible)
        {
            Show();
        }

        if (wasVisible)
        {
            RootCard.BeginAnimation(OpacityProperty, null);
            RootTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            RootCard.Opacity = 1;
            RootTranslate.Y = 0;
            return;
        }

        RootCard.BeginAnimation(OpacityProperty, null);
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        RootCard.Opacity = 0;
        RootTranslate.Y = -28;

        RootCard.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(230)));
        RootTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(360))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void SetPanel(UIElement panel)
    {
        RecordingPanel.Visibility = panel == RecordingPanel ? Visibility.Visible : Visibility.Collapsed;
        ProcessingPanel.Visibility = panel == ProcessingPanel ? Visibility.Visible : Visibility.Collapsed;
        DraftPanel.Visibility = panel == DraftPanel ? Visibility.Visible : Visibility.Collapsed;
        ResultPanel.Visibility = panel == ResultPanel ? Visibility.Visible : Visibility.Collapsed;

        ContentHost.BeginAnimation(OpacityProperty, null);
        ContentHost.Opacity = 0;
        ContentHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
    }

    private void PositionAtTopCenter()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + 18;
    }

    private void UpdateDuration()
    {
        var elapsed = DateTimeOffset.UtcNow - _recordingStartedAt;
        RecordingDurationText.Text = elapsed.ToString(elapsed.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
    }

    private void StopTransientTimers()
    {
        _recordingTimer.Stop();
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => SendRequested?.Invoke(this, EventArgs.Empty);

    private void EditButton_Click(object sender, RoutedEventArgs e) => EditRequested?.Invoke(this, EventArgs.Empty);

    private void CancelButton_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

    private static string Shorten(string? value, int maximumLength)
    {
        var normalized = string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength ? normalized : normalized[..(maximumLength - 1)].TrimEnd() + "…";
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint newValue);
}
