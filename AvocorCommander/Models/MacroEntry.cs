using AvocorCommander.Core;

namespace AvocorCommander.Models;

public sealed class MacroEntry : BaseViewModel
{
    private int    _id;
    private string _macroName = string.Empty;
    private string _notes     = string.Empty;

    public int    Id        { get => _id;        set => Set(ref _id, value); }
    public string MacroName { get => _macroName; set => Set(ref _macroName, value); }
    public string Notes     { get => _notes;     set => Set(ref _notes, value); }

    public List<MacroStep> Steps { get; set; } = [];

    public string StepSummary => Steps.Count == 0
        ? "No steps"
        : $"{Steps.Count} step{(Steps.Count == 1 ? "" : "s")}";
}
