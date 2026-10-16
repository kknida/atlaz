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

public class ScheduleConfig
{
    public bool IsEnabled { get; set; } = true;
    public TimeSpan ScheduledTime { get; set; } = new TimeSpan(8, 0, 0); // 08:00
    public bool RunOnMonday { get; set; } = true;
    public bool RunOnTuesday { get; set; } = true;
    public bool RunOnWednesday { get; set; } = true;
    public bool RunOnThursday { get; set; } = true;
    public bool RunOnFriday { get; set; } = true;
    public bool RunOnSaturday { get; set; } = false;
    public bool RunOnSunday { get; set; } = false;
}

public class SapConnectionConfig
{
    public string SystemId { get; set; } = "PRD";
    public string Client { get; set; } = "100";
    public string Username { get; set; } = "BOT_USER";
    public string EncryptedPassword { get; set; } = string.Empty;
    public string TransactionCode { get; set; } = "ZFM001";
    public string FiscalYear { get; set; } = DateTime.Now.Year.ToString();
    public string Period { get; set; } = DateTime.Now.Month.ToString("D2");
}

public class WebDarsanaConfig
{
    public string TransferProtocol { get; set; } = "SFTP"; // SFTP / API
    public string Host { get; set; } = "srv.darsana.id";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "u_darsana";
    public string EncryptedPasswordOrToken { get; set; } = string.Empty;
    public string RemoteStagingPath { get; set; } = "/home/u_darsana/public_html/storage/app/import_staging";
}
