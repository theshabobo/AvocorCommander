using AvocorCommander.Core;
using AvocorCommander.Models;
using AvocorCommander.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace AvocorCommander.ViewModels;

public sealed class GroupsViewModel : BaseViewModel
{
    private readonly DatabaseService   _db;
    private readonly ConnectionManager _connMgr;

    public ObservableCollection<GroupEntry>  Groups     { get; } = [];
    public ObservableCollection<DeviceEntry> AllDevices { get; } = [];

    // ── Selected group ────────────────────────────────────────────────────────

    private GroupEntry? _selectedGroup;
    public GroupEntry? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            Set(ref _selectedGroup, value);
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(MemberDevices));
            OnPropertyChanged(nameof(NonMemberDevices));
            if (value != null)
            {
                _editName  = value.GroupName;
                _editNotes = value.Notes;
                OnPropertyChanged(nameof(EditName));
                OnPropertyChanged(nameof(EditNotes));
            }
            DeviceToAdd = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSelection => SelectedGroup != null;

    // ── Inline edit fields ────────────────────────────────────────────────────

    private string _editName = string.Empty;
    public string EditName
    {
        get => _editName;
        set => Set(ref _editName, value);
    }

    private string _editNotes = string.Empty;
    public string EditNotes
    {
        get => _editNotes;
        set => Set(ref _editNotes, value);
    }

    // ── Member / non-member device lists ──────────────────────────────────────

    public List<DeviceEntry> MemberDevices => SelectedGroup == null
        ? []
        : AllDevices.Where(d => SelectedGroup.MemberDeviceIds.Contains(d.Id)).ToList();

    public List<DeviceEntry> NonMemberDevices => SelectedGroup == null
        ? [..AllDevices]
        : AllDevices.Where(d => !SelectedGroup.MemberDeviceIds.Contains(d.Id)).ToList();

    private DeviceEntry? _deviceToAdd;
    public DeviceEntry? DeviceToAdd
    {
        get => _deviceToAdd;
        set { Set(ref _deviceToAdd, value); CommandManager.InvalidateRequerySuggested(); }
    }

    // ── Status ────────────────────────────────────────────────────────────────

    private string _statusMessage = "Select a group to manage its members.";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    // ── Commands ─────────────────────────────────────────────────────────────

    public ICommand AddGroupCommand        { get; }
    public ICommand SaveGroupCommand       { get; }
    public ICommand DeleteGroupCommand     { get; }
    public ICommand AddMemberCommand       { get; }
    public ICommand RemoveMemberCommand    { get; }
    public ICommand ConnectGroupCommand    { get; }
    public ICommand DisconnectGroupCommand { get; }

    public GroupsViewModel(DatabaseService db, ConnectionManager connMgr)
    {
        _db      = db;
        _connMgr = connMgr;

        AddGroupCommand        = new RelayCommand(AddGroup);
        SaveGroupCommand       = new RelayCommand(SaveGroup,    () => SelectedGroup != null);
        DeleteGroupCommand     = new RelayCommand<GroupEntry>(DeleteGroup,   g => g != null);
        AddMemberCommand       = new RelayCommand(AddMember,    () => SelectedGroup != null && DeviceToAdd != null);
        RemoveMemberCommand    = new RelayCommand<DeviceEntry>(RemoveMember, d => d != null);
        ConnectGroupCommand    = new AsyncRelayCommand<GroupEntry>(ConnectGroupAsync,    g => g != null);
        DisconnectGroupCommand = new AsyncRelayCommand<GroupEntry>(DisconnectGroupAsync, g => g != null);
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    public void LoadData()
    {
        var groups  = _db.GetAllGroups();
        var devices = _db.GetAllDevices();

        var prevId = SelectedGroup?.Id;

        Groups.Clear();
        foreach (var g in groups) Groups.Add(g);

        AllDevices.Clear();
        foreach (var d in devices) AllDevices.Add(d);

        SelectedGroup = prevId.HasValue
            ? Groups.FirstOrDefault(g => g.Id == prevId.Value)
            : null;

        StatusMessage = $"{Groups.Count} group(s) loaded.";
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    private void AddGroup()
    {
        var group = new GroupEntry { GroupName = $"New Group {Groups.Count + 1}" };
        int id = _db.InsertGroup(group);
        group.Id = id;
        Groups.Add(group);
        SelectedGroup = group;
        StatusMessage = $"Added: {group.GroupName}  — rename it in the editor on the right.";
    }

    private void SaveGroup()
    {
        if (SelectedGroup == null) return;
        var name = EditName.Trim();
        if (string.IsNullOrWhiteSpace(name)) { StatusMessage = "Name cannot be empty."; return; }

        SelectedGroup.GroupName = name;
        SelectedGroup.Notes     = EditNotes;
        _db.UpdateGroup(SelectedGroup);
        StatusMessage = $"Saved: {SelectedGroup.GroupName}";
    }

    private void DeleteGroup(GroupEntry? group)
    {
        if (group == null) return;
        if (MessageBox.Show($"Delete group '{group.GroupName}'?", "Confirm Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _db.DeleteGroup(group.Id);
        Groups.Remove(group);
        if (SelectedGroup == group) SelectedGroup = null;
        StatusMessage = $"Deleted: {group.GroupName}";
    }

    private void AddMember()
    {
        if (SelectedGroup == null || DeviceToAdd == null) return;
        if (SelectedGroup.MemberDeviceIds.Contains(DeviceToAdd.Id)) return;

        SelectedGroup.MemberDeviceIds.Add(DeviceToAdd.Id);
        _db.SetGroupMembers(SelectedGroup.Id, SelectedGroup.MemberDeviceIds);

        var added = DeviceToAdd;
        DeviceToAdd = null;

        OnPropertyChanged(nameof(MemberDevices));
        OnPropertyChanged(nameof(NonMemberDevices));
        StatusMessage = $"Added {added.DeviceName} to {SelectedGroup.GroupName}.";
    }

    private void RemoveMember(DeviceEntry? device)
    {
        if (SelectedGroup == null || device == null) return;
        SelectedGroup.MemberDeviceIds.Remove(device.Id);
        _db.SetGroupMembers(SelectedGroup.Id, SelectedGroup.MemberDeviceIds);

        OnPropertyChanged(nameof(MemberDevices));
        OnPropertyChanged(nameof(NonMemberDevices));
        StatusMessage = $"Removed {device.DeviceName} from {SelectedGroup.GroupName}.";
    }

    private async Task ConnectGroupAsync(GroupEntry? group)
    {
        if (group == null) return;
        var devices = _db.GetAllDevices().Where(d => group.MemberDeviceIds.Contains(d.Id)).ToList();
        StatusMessage = $"Connecting {devices.Count} device(s) in '{group.GroupName}'…";
        int ok = 0;
        foreach (var d in devices)
            if (await _connMgr.ConnectAsync(d)) ok++;
        StatusMessage = $"{ok}/{devices.Count} connected in '{group.GroupName}'";
    }

    private async Task DisconnectGroupAsync(GroupEntry? group)
    {
        if (group == null) return;
        var devices = _db.GetAllDevices().Where(d => group.MemberDeviceIds.Contains(d.Id)).ToList();
        foreach (var d in devices)
            if (_connMgr.IsConnected(d.Id)) await _connMgr.DisconnectAsync(d);
        StatusMessage = $"Disconnected all in '{group.GroupName}'";
    }
}
