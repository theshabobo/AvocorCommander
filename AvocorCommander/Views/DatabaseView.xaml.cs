using AvocorCommander.Models;
using AvocorCommander.ViewModels;
using System.Windows.Controls;
using System.Windows.Input;

namespace AvocorCommander.Views;

public partial class DatabaseView : UserControl
{
    public DatabaseView() => InitializeComponent();

    // Toggle group membership when user clicks a device row in the Groups tab
    private void DeviceList_Toggle(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is DatabaseViewModel vm && vm.SelectedGroup != null &&
            DeviceList.SelectedItem is DeviceEntry device)
        {
            vm.ToggleGroupMemberCmd.Execute(device);
            DeviceList.SelectedItem = null; // clear selection after toggle
        }
    }
}
