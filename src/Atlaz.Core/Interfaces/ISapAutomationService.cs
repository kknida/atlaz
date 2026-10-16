namespace Atlaz.Core.Interfaces;

public class SapExecutionResult
{
    public bool Success { get; set; }
    public string ExportedFilePath { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
}

public interface ISapAutomationService
{
    /// <summary>
    /// Menjalankan siklus otomasi SAP GUI: Login -> Buka ZFM001 -> Download Report CSV -> Logout
    /// </summary>
    Task<SapExecutionResult> ExecuteZfm001DownloadAsync(
        string systemId,
        string client,
        string username,
        string password,
        string fiscalYear,
        string period,
        string outputDirectory,
        Action<string>? statusCallback = null,
        CancellationToken ct = default);

    /// <summary>
    /// Memeriksa apakah SAP GUI terpasang di komputer saat ini
    /// </summary>
    bool IsSapGuiInstalled();
}

