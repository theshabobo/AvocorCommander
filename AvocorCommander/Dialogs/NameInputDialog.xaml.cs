using System.Windows;
using WpfKey = System.Windows.Input.Key;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace AvocorCommander.Dialogs;

public partial class NameInputDialog : Window
{
    public string Result { get; private set; } = "";

    public NameInputDialog(string prompt = "Name:", string initial = "")
    {
        InitializeComponent();
        TxtPrompt.Text = prompt;
        TxtInput.Text  = initial;
        Loaded += (_, _) => { TxtInput.Focus(); TxtInput.SelectAll(); };
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtInput.Text)) return;
        Result       = TxtInput.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TxtInput_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == WpfKey.Enter) OK_Click(sender, e);
        if (e.Key == WpfKey.Escape) Cancel_Click(sender, e);
    }
}
