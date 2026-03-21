namespace AvocorCommander.Models;

/// <summary>
/// Holds per-button state for the Virtual Remote view:
/// which command is resolved, and whether a flash feedback is active.
/// </summary>
public sealed class RemoteButtonState : Core.BaseViewModel
{
    private CommandEntry? _command;
    private bool _flashSuccess;
    private bool _flashError;

    /// <summary>The resolved DB command to send. Null = button grayed out.</summary>
    public CommandEntry? Command
    {
        get => _command;
        set
        {
            Set(ref _command, value);
            OnPropertyChanged(nameof(IsAvailable));
        }
    }

    /// <summary>True when a command exists for the selected device's series.</summary>
    public bool IsAvailable => _command != null;

    /// <summary>Green flash — set after a successful send, auto-cleared by the ViewModel timer.</summary>
    public bool FlashSuccess
    {
        get => _flashSuccess;
        set => Set(ref _flashSuccess, value);
    }

    /// <summary>Red flash — set after a failed send, auto-cleared by the ViewModel timer.</summary>
    public bool FlashError
    {
        get => _flashError;
        set => Set(ref _flashError, value);
    }

    public void ClearFlash()
    {
        FlashSuccess = false;
        FlashError   = false;
    }
}
