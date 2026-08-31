using System.Windows;
using System.Windows.Input;
using Speech2Issues.Core.Destinations;
using Speech2Issues.Core.Models;

namespace Speech2Issues.App;

public partial class DraftEditorWindow : Window
{
    private readonly TaskDraft _draft;
    public DraftEditorWindow(TaskDraft draft, IReadOnlyList<DestinationTarget> plankaTargets, string? selectedTargetId)
    {
        _draft = draft;
        InitializeComponent();
        TitleBox.Text = draft.Title;
        DescriptionBox.Text = TaskMarkdown.BuildBody(draft, includeTranscript: false, includeMarker: false);
        TranscriptBox.Text = draft.Transcript;
        PlankaTargetCombo.ItemsSource = plankaTargets;
        PlankaTargetCombo.SelectedItem = plankaTargets.FirstOrDefault(x => x.Id == selectedTargetId) ?? plankaTargets.FirstOrDefault();
        PlankaRoutePanel.Visibility = plankaTargets.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public TaskDraft Draft { get; private set; } = new();
    public string? SelectedPlankaTargetId => (PlankaTargetCombo.SelectedItem as DestinationTarget)?.Id;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text)) return;
        var description = DescriptionBox.Text;
        var marker = description.IndexOf("## Критерии готовности", StringComparison.Ordinal);
        Draft = _draft with { Title = TitleBox.Text.Trim(), Description = (marker >= 0 ? description[..marker] : description).Replace(TaskMarkdown.Marker(_draft), string.Empty).Trim() };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}
