namespace Sphene.SpheneConfiguration;

public class ImportResult
{
    public int RestoredCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }
    public string? ErrorMessage { get; set; }
    public bool Success => string.IsNullOrEmpty(ErrorMessage);
}
