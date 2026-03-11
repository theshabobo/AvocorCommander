using AvocorCommander.Core;
using System.Collections.ObjectModel;

namespace AvocorCommander.Models;

/// <summary>Mirrors the Groups table row, with its member devices.</summary>
public sealed class GroupEntry : BaseViewModel
{
    private int    _id;
    private string _groupName = string.Empty;
    private string _notes     = string.Empty;

    public int    Id        { get => _id;        set => Set(ref _id, value); }
    public string GroupName { get => _groupName; set => Set(ref _groupName, value); }
    public string Notes     { get => _notes;     set => Set(ref _notes, value); }

    /// <summary>Device IDs that belong to this group (from GroupMembers).</summary>
    public ObservableCollection<int> MemberDeviceIds { get; } = [];

    public string MemberSummary => MemberDeviceIds.Count == 1
        ? "1 device"
        : $"{MemberDeviceIds.Count} devices";
}
