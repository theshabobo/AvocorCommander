using AvocorCommander.Models;
using AvocorCommander.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AvocorCommander.Views;

public partial class GroupsView : UserControl
{
    public GroupsView() => InitializeComponent();

    private void GroupCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is GroupEntry group &&
            DataContext is GroupsViewModel vm)
        {
            vm.SelectedGroup = group;
        }
    }
}
