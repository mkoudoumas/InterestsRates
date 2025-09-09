namespace Rates.Cli;

public sealed class Settings
{
    public string Source { get; set; } = "Local";  // Local | Online | Special
    public string? LocalHtmlPath { get; set; }     // Used when Source=Local
}
