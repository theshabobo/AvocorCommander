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

    public string DelayDisplay => DelayAfterMs == 0 ? "—" : $"{DelayAfterMs} ms";
    public string Display      => $"{StepOrder}.  {CommandName}   {DelayDisplay}";
}
