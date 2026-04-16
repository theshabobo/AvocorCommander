namespace AvocorCommander.Models;

public sealed class MacroStep
{
    public int    Id           { get; set; }
    public int    MacroId      { get; set; }
    public int    StepOrder    { get; set; }
    public int    CommandId    { get; set; }
    public string CommandName  { get; set; } = string.Empty;
    public string SeriesPattern { get; set; } = string.Empty;
    public int    DelayAfterMs { get; set; }

    /// <summary>"command" (run a DeviceList command) or "prompt" (pause for user).</summary>
    public string StepType     { get; set; } = "command";

    /// <summary>Message shown to the user when StepType == "prompt".</summary>
    public string PromptText   { get; set; } = string.Empty;

    public bool   IsPrompt  => string.Equals(StepType, "prompt", StringComparison.OrdinalIgnoreCase);
    public bool   IsCommand => !IsPrompt;

    public string DelayDisplay => DelayAfterMs == 0 ? "—" : $"{DelayAfterMs} ms";
    public string Display      => IsPrompt
        ? $"{StepOrder}.  [Prompt] {PromptText}"
        : $"{StepOrder}.  {CommandName}   {DelayDisplay}";
}
