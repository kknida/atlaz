namespace Atlaz.Core.Interfaces;

public class Stream2DownloadResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> DownloadedFiles { get; set; } = new();
    public int TotalDownloaded => DownloadedFiles.Count;
}

public interface IStream2AutomationService
{
    /// <summary>
    /// Login ke portal Stream2 dan mengunduh laporan H-1 (Terminal Charge, Enroute Charge, Data Movement)
    /// </summary>
    Task<Stream2DownloadResult> DownloadReportsAsync(
        string baseUrl,
        string username,
        string password,
        DateTime targetDate,
        string outputDirectory,
        bool downloadTerminal = true,
        bool downloadEnroute = true,
        bool downloadMovement = true,
        Action<string>? statusCallback = null,
        CancellationToken ct = default);
}
