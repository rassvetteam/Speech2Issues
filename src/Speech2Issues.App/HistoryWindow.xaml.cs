using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Speech2Issues.Core.Models;
using Speech2Issues.Core.Storage;

namespace Speech2Issues.App;

public partial class HistoryWindow : Window
{
    private readonly HistoryRepository _history;
    public HistoryWindow(HistoryRepository history)
    {
        _history = history;
        InitializeComponent();
        Loaded += async (_, _) => HistoryGrid.ItemsSource = (await _history.GetRecentAsync()).Select(x => new HistoryRow(x)).ToArray();
    }

    public event EventHandler<HistoryEntry>? RetryRequested;

    private async void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DeliveryGrid.ItemsSource = HistoryGrid.SelectedItem is HistoryRow row ? await _history.GetDeliveriesAsync(row.Entry.Id) : null;
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        var url = (DeliveryGrid.SelectedItem as DeliveryAttempt)?.ExternalUrl ?? (HistoryGrid.SelectedItem as HistoryRow)?.Entry.ExternalUrl;
        if (!string.IsNullOrWhiteSpace(url)) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is HistoryRow row) { RetryRequested?.Invoke(this, row.Entry); Close(); }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
    public sealed record HistoryRow(HistoryEntry Entry) { public DateTimeOffset CreatedAt => Entry.CreatedAt.ToLocalTime(); }
}
