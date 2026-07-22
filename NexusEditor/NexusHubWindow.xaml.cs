using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace NexusEditor;

public partial class NexusHubWindow : Window
{
    private static readonly Brush ReadyBrush = new SolidColorBrush(Color.FromRgb(0x55, 0xc5, 0x8a));
    private static readonly Brush UnavailableBrush = new SolidColorBrush(Color.FromRgb(0x8b, 0x8b, 0x8b));
    private readonly Action<WorkspaceMode>? _onWorkspaceSelected;
    private bool _selectionInProgress;

    public NexusHubWindow()
        : this(
            WorkspaceCardState.Unavailable("Event DB 상태를 확인해 주세요."),
            WorkspaceCardState.Unavailable("Research DB 상태를 확인해 주세요."))
    {
    }

    public NexusHubWindow(
        WorkspaceCardState eventEditor,
        WorkspaceCardState researchEditor,
        Action<WorkspaceMode>? onWorkspaceSelected = null)
    {
        ArgumentNullException.ThrowIfNull(eventEditor);
        ArgumentNullException.ThrowIfNull(researchEditor);

        InitializeComponent();
        WindowState = WindowState.Maximized;

        _onWorkspaceSelected = onWorkspaceSelected;
        ApplyCardState(EventCardButton, EventStatusIndicator, EventStatusText, eventEditor);
        ApplyCardState(ResearchCardButton, ResearchStatusIndicator, ResearchStatusText, researchEditor);
    }

    public WorkspaceMode? SelectedWorkspace { get; private set; }

    public event EventHandler<WorkspaceRequestedEventArgs>? WorkspaceRequested;

    private static void ApplyCardState(
        FrameworkElement card,
        System.Windows.Shapes.Shape indicator,
        System.Windows.Controls.TextBlock statusText,
        WorkspaceCardState state)
    {
        card.IsEnabled = true;
        card.Opacity = state.IsAvailable ? 1.0 : 0.86;
        card.ToolTip = state.IsAvailable
            ? state.StatusText
            : $"{state.StatusText}\n선택하면 경로를 지정할 수 있습니다.";
        indicator.Fill = state.IsAvailable ? ReadyBrush : UnavailableBrush;
        statusText.Text = state.StatusText;
    }

    private void EventCardButton_Click(object sender, RoutedEventArgs e)
    {
        SelectWorkspace(WorkspaceMode.Event);
    }

    private void ResearchCardButton_Click(object sender, RoutedEventArgs e)
    {
        SelectWorkspace(WorkspaceMode.Research);
    }

    private void SelectWorkspace(WorkspaceMode workspace)
    {
        if (_selectionInProgress)
            return;

        _selectionInProgress = true;
        EventCardButton.IsEnabled = false;
        ResearchCardButton.IsEnabled = false;
        SelectedWorkspace = workspace;
        try
        {
            WorkspaceRequested?.Invoke(this, new WorkspaceRequestedEventArgs(workspace));
            if (_onWorkspaceSelected is not null)
                _onWorkspaceSelected(workspace);
            else
                Close();
        }
        catch
        {
            _selectionInProgress = false;
            EventCardButton.IsEnabled = true;
            ResearchCardButton.IsEnabled = true;
            throw;
        }
    }

    public void ResetSelection()
    {
        _selectionInProgress = false;
        SelectedWorkspace = null;
        EventCardButton.IsEnabled = true;
        ResearchCardButton.IsEnabled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close();
    }
}
