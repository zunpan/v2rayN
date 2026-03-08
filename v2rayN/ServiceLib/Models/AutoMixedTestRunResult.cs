namespace ServiceLib.Models;

public class AutoMixedTestRunResult
{
    public int ScannedCount { get; set; }
    public int DeletedCount { get; set; }
    public int SortedCount { get; set; }
    public bool Skipped { get; set; }
    public string? SkipReason { get; set; }
    public string? SwitchedToIndexId { get; set; }
    public List<string> Errors { get; set; } = [];
}
