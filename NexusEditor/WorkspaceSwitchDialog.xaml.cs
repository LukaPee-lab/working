using System.Windows;
using System.Windows.Input;

namespace NexusEditor;

public partial class WorkspaceSwitchDialog : Window
{
    public WorkspaceSwitchDialog(string? message = null)
    {
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(message))
        {
            MessageText.Text = message;
        }
    }

    public WorkspaceSwitchResult Result { get; private set; } = WorkspaceSwitchResult.Cancel;

    public static WorkspaceSwitchResult Request(Window owner, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var dialog = new WorkspaceSwitchDialog(message)
        {
            Owner = owner
        };
        dialog.ShowDialog();
        return dialog.Result;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ExportAndSwitchButton.Focus();
        Keyboard.Focus(ExportAndSwitchButton);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Complete(WorkspaceSwitchResult.Cancel, false);
    }

    private void DiscardButton_Click(object sender, RoutedEventArgs e)
    {
        Complete(WorkspaceSwitchResult.DiscardChanges, true);
    }

    private void ExportAndSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        Complete(WorkspaceSwitchResult.ExportAndSwitch, true);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Complete(WorkspaceSwitchResult.Cancel, false);
    }

    private void Complete(WorkspaceSwitchResult result, bool dialogResult)
    {
        Result = result;
        DialogResult = dialogResult;
    }
}
