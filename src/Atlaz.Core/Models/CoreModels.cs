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

public partial class ScheduleConfig : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled = true;

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
    private string _fundsCenter = "A022020000";

    [ObservableProperty]
    private string _commitmentItem = string.Empty;
}

public partial class WebDarsanaConfig : ObservableObject
{
    [ObservableProperty]
    private string _transferProtocol = "SFTP";

    [ObservableProperty]
    private string _host = "srv.darsana.id";

    [ObservableProperty]
    private int _port = 22;

    [ObservableProperty]
    private string _username = "u_darsana";

    [ObservableProperty]
    private string _encryptedPasswordOrToken = string.Empty;

    [ObservableProperty]
    private string _remoteStagingPath = "/home/u_darsana/public_html/storage/app/import_staging";
}
