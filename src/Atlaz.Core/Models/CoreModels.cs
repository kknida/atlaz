using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atlaz.Core.Models;

public enum BotStatus
{
    Idle,
    Running,
    Success,
    Warning,
    Error
}

public class ExecutionHistoryItem
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Step { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int RecordCount { get; set; }
    public BotStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
}

public enum ScheduleIntervalType
{
    Hourly, // Setiap Jam / Hari tertentu
    EveryNMinutes // Setiap Berapa Menit (Uji Coba / Cepat)
}

public partial class ScheduleConfig : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHourly))]
    [NotifyPropertyChangedFor(nameof(IsEveryNMinutes))]
    private ScheduleIntervalType _intervalType = ScheduleIntervalType.Hourly;

    [ObservableProperty]
    private int _intervalMinutes = 5; // Default 5 menit untuk pengujian cepat

    [ObservableProperty]
    private string _scheduledTimeString = "08:00";

    [ObservableProperty]
    private bool _runOnMonday = true;

    [ObservableProperty]
    private bool _runOnTuesday = true;

    [ObservableProperty]
    private bool _runOnWednesday = true;

    [ObservableProperty]
    private bool _runOnThursday = true;

    [ObservableProperty]
    private bool _runOnFriday = true;

    [ObservableProperty]
    private bool _runOnSaturday = false;

    [ObservableProperty]
    private bool _runOnSunday = false;

    public bool IsHourly => IntervalType == ScheduleIntervalType.Hourly;
    public bool IsEveryNMinutes => IntervalType == ScheduleIntervalType.EveryNMinutes;
}

public partial class SapConnectionConfig : ObservableObject
{
    [ObservableProperty]
    private string _systemId = "INTERNET PROD - AEP";

    [ObservableProperty]
    private string _client = "100";

    [ObservableProperty]
    private string _username = "BOT_USER";

    [ObservableProperty]
    private string _encryptedPassword = string.Empty;

    [ObservableProperty]
    private string _transactionCode = "ZFM001";

    [ObservableProperty]
    private string _fmArea = "1000";

    [ObservableProperty]
    private string _budgetCategory = "9F";

    [ObservableProperty]
    private string _version = "0";

    [ObservableProperty]
    private string _fiscalYear = "2026";

    [ObservableProperty]
    private string _fromPeriod = "1";

    [ObservableProperty]
    private string _toPeriod = "12";

    [ObservableProperty]
    private string _fundsCenterFrom = "A022020000";

    [ObservableProperty]
    private string _fundsCenterTo = "A022020005";

    [ObservableProperty]
    private string _commitmentItem = string.Empty;
}

public partial class WebDarsanaConfig : ObservableObject
{
    /// <summary>
    /// Pilihan protokol: "SFTP" (Default: Kebal Imunify360 WAF), "API_GZIP", atau "API_MULTIPART".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSftp))]
    [NotifyPropertyChangedFor(nameof(IsApi))]
    private string _transferProtocol = "SFTP";

    [ObservableProperty]
    private string _host = "darsana.airnavsub.id";

    [ObservableProperty]
    private int _port = 22;

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _encryptedPasswordOrToken = string.Empty;

    [ObservableProperty]
    private string _remoteStagingPath = "storage/app/sap/incoming";

    [ObservableProperty]
    private string _dashboardUrl = "https://darsana.airnavsub.id";

    [ObservableProperty]
    private string _apiToken = string.Empty;

    public bool IsSftp => TransferProtocol == "SFTP";
    public bool IsApi => TransferProtocol != "SFTP";
}

public partial class Stream2Config : ObservableObject
{
    [ObservableProperty]
    private string _url = "https://stream2.airnavindonesia.co.id/";

    [ObservableProperty]
    private string _username = "WARR4";

    [ObservableProperty]
    private string _encryptedPassword = "123";

    [ObservableProperty]
    private int _targetDaysAgo = 1; // H-1 (Hari Sebelumnya)

    [ObservableProperty]
    private string _branchCode = "WARR";

    [ObservableProperty]
    private bool _downloadTerminalCharge = true;

    [ObservableProperty]
    private bool _downloadEnrouteCharge = true;

    [ObservableProperty]
    private bool _downloadMovement = true;
}

/// <summary>
/// Representasi 1 baris tabel budget_realisasi di database Web Darsana.
/// </summary>
public class BudgetRealisasiRecord
{
    [JsonPropertyName("report_date")]
    public string ReportDate { get; set; } = string.Empty;

    [JsonPropertyName("branch_code")]
    public string? BranchCode { get; set; }

    [JsonPropertyName("item_code")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("level")]
    public string Level { get; set; } = "item"; // "item", "cabang", atau "total"

    [JsonPropertyName("branch_name")]
    public string? BranchName { get; set; }

    [JsonPropertyName("item_name")]
    public string? ItemName { get; set; }

    [JsonPropertyName("rkap")]
    public long Rkap { get; set; }

    [JsonPropertyName("release_budget")]
    public long ReleaseBudget { get; set; }

    [JsonPropertyName("commitment")]
    public long Commitment { get; set; }

    [JsonPropertyName("total_consume")]
    public long TotalConsume { get; set; }

    [JsonPropertyName("available_budget")]
    public long AvailableBudget { get; set; }
}

public class CsvValidationResult
{
    public bool IsValid { get; set; }
    public string Message { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ReportDate { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string Sha256Checksum { get; set; } = string.Empty;
    public string DetectedDelimiter { get; set; } = ",";
    public int RowsImported { get; set; }
    public int BranchesCount { get; set; }
    public int ItemsCount { get; set; }
    public int SkippedCount { get; set; }
    public List<BudgetRealisasiRecord> Records { get; set; } = new();
}

public class TransferResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int RowsImportedOnServer { get; set; }
    public string ArchivedFilePath { get; set; } = string.Empty;
    public string ProtocolUsed { get; set; } = string.Empty;
}
