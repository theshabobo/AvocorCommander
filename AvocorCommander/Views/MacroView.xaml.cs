using AvocorCommander.Models;
using AvocorCommander.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AvocorCommander.Views;

public partial class MacroView : UserControl
{
    public MacroView() => InitializeComponent();

    // Clicking a macro card selects it (sets SelectedMacro on the VM)
    private void MacroCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MacroEntry macro &&
            DataContext is MacroViewModel vm)
        {
            vm.SelectedMacro = macro;
        }
    }
}
