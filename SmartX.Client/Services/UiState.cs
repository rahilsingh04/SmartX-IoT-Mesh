namespace SmartX.Client.Services;

/// <summary>
/// Scoped (one per browser circuit) UI state. Because it outlives individual pages, the filters,
/// selected device and command choices survive navigating between modules and back again.
/// </summary>
public class UiState
{
    public string SearchText { get; set; } = "";
    public string CategoryFilter { get; set; } = "All";
    public string StateFilter { get; set; } = "All";
    public string? SelectedMac { get; set; }
    public string CommandTarget { get; set; } = "ALL_ACTUATORS";
    public string CommandAction { get; set; } = "Off";
}
