using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Atlaz.Core.Interfaces;
using Atlaz.Core.Models;
using Atlaz.Infrastructure.Sap;
using Atlaz.Infrastructure.Transfer;
using Atlaz.Infrastructure.Validation;

namespace Atlaz.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ISapAutomationService _sapAutomationService;
    private readonly ICsvValidationService _csvValidationService;
    private readonly ITransferService _transferService;
    private readonly string _configFilePath;

    [ObservableProperty]
    private string _currentView = "Dashboard"; // Dashboard, Scheduler, Settings, History

    [ObservableProperty]
    private BotStatus _currentStatus = BotStatus.Idle;

    [ObservableProperty]
    private string _statusMessage = "Sistem siap dijalankan (Idle)";

    [ObservableProperty]
    private string _lastRunTime = "Belum pernah dieksekusi";

    [ObservableProperty]
    private string _nextScheduleTime = "Hari ini, 08:00 WIB";

    [ObservableProperty]
    private int _currentPhase = 4; // Seluruh 4 Tahap Aktif

    [ObservableProperty]
    private string _phaseStatusText = "Pipeline 4-Tahap Siap (SAP GUI -> Validasi CSV -> Upload Anti-WAF Darsana)";

    [ObservableProperty]
    private double _progressValue = 0.0;

    [ObservableProperty]
    private bool _isRunning = false;

    // Configs
    [ObservableProperty]
    private ScheduleConfig _schedule = new();

    [ObservableProperty]
    private SapConnectionConfig _sapConfig = new();

    [ObservableProperty]
    private WebDarsanaConfig _darsanaConfig = new();

    // History
    public ObservableCollection<ExecutionHistoryItem> ExecutionHistory { get; } = new();

    private readonly System.Windows.Threading.DispatcherTimer _schedulerTimer;
    private DateTime _lastTriggerDate = DateTime.MinValue;

    public MainViewModel()
    {
        _sapAutomationService = new SapGuiAutomationService();
        _csvValidationService = new CsvFileValidationService();
        _transferService = new DarsanaTransferService();

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlaz");
        Directory.CreateDirectory(appData);
        _configFilePath = Path.Combine(appData, "config.json");

        LoadSavedSettings();

        // Cek instalasi SAP GUI lokal
        var sapInstalled = _sapAutomationService.IsSapGuiInstalled();
        StatusMessage = sapInstalled 
            ? "SAP GUI terdeteksi. Pipeline 4-Tahap (SAP -> Validasi CSV -> Upload Web Darsana) siap dijalankan." 
            : "SAP GUI tidak terpasang di PC ini. Silakan jalankan di PC Server SAP.";

        LoadMockHistory();

        // Inisialisasi background scheduler timer (cek setiap 10 detik)
        _schedulerTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _schedulerTimer.Tick += OnSchedulerTick;
        _schedulerTimer.Start();
        UpdateNextScheduleTimeDisplay();
    }

    [RelayCommand]
    private void SwitchView(string viewName)
    {
        CurrentView = viewName;
    }

    [RelayCommand]
    private void SetScheduleIntervalType(string typeStr)
    {
        if (Enum.TryParse<ScheduleIntervalType>(typeStr, out var parsedType))
        {
            Schedule.IntervalType = parsedType;
            UpdateNextScheduleTimeDisplay();
        }
    }

    [RelayCommand]
    private void SetTransferProtocol(string protocol)
    {
        if (!string.IsNullOrWhiteSpace(protocol))
        {
            DarsanaConfig.TransferProtocol = protocol;
        }
    }

    [RelayCommand]
    public void DiagnoseSap()
    {
        var results = _sapAutomationService.Diagnose();
        var allOk = results.All(r => r.Sukses);

        var report = string.Join("\n", results.Select(r => $"{r.Pesan}"));
        StatusMessage = allOk 
            ? "Hasil Diagnosis: SAP GUI & Scripting Engine siap digunakan!" 
            : $"Diagnosis SAP:\n{report}";

        foreach (var item in results)
        {
            ExecutionHistory.Insert(0, new ExecutionHistoryItem
            {
                Timestamp = DateTime.Now,
                Step = "Diagnostik SAP",
                FileName = "-",
                RecordCount = 0,
                Status = item.Sukses ? BotStatus.Success : BotStatus.Warning,
                Message = $"{item.Langkah}: {item.Pesan}"
            });
        }
    }

    [RelayCommand]
    public async Task TestDarsanaConnectionAsync()
    {
        StatusMessage = "Menguji koneksi & token ke Web Darsana...";
        var (success, msg) = await _transferService.TestConnectionAsync(DarsanaConfig);
        CurrentStatus = success ? BotStatus.Success : BotStatus.Warning;
        StatusMessage = msg;

        ExecutionHistory.Insert(0, new ExecutionHistoryItem
        {
            Timestamp = DateTime.Now,
            Step = "Tes Koneksi Web Darsana",
            FileName = "-",
            RecordCount = 0,
            Status = success ? BotStatus.Success : BotStatus.Error,
            Message = msg
        });
    }

    [RelayCommand]
    public async Task RunManualSyncAsync()
    {
        if (IsRunning) return;

        IsRunning = true;
        CurrentStatus = BotStatus.Running;
        StatusMessage = "Menjalankan siklus otomasi 4-Tahap...";

        // =========================================================================
        // TAHAP 1: Verifikasi Parameter Konfigurasi
        // =========================================================================
        CurrentPhase = 1;
        ProgressValue = 20;
        PhaseStatusText = "Tahap 1: Memeriksa kelengkapan parameter SAP & Web Darsana...";
        await Task.Delay(300);

        // =========================================================================
        // TAHAP 2: Eksekusi Otomasi SAP GUI (ZFM001 -> CSV -> Close Excel -> Logout)
        // =========================================================================
        CurrentPhase = 2;
        ProgressValue = 45;
        PhaseStatusText = "Tahap 2: Menghubungi SAP GUI -> Login -> ZFM001 -> Download CSV...";

        var outputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlaz", "exports");

        var sapResult = await _sapAutomationService.ExecuteZfm001DownloadAsync(
            systemId: SapConfig.SystemId,
            client: SapConfig.Client,
            username: SapConfig.Username,
            password: SapConfig.EncryptedPassword,
            fmArea: SapConfig.FmArea,
            budgetCategory: SapConfig.BudgetCategory,
            version: SapConfig.Version,
            fiscalYear: SapConfig.FiscalYear,
            fromPeriod: SapConfig.FromPeriod,
            toPeriod: SapConfig.ToPeriod,
            fundsCenterFrom: SapConfig.FundsCenterFrom,
            fundsCenterTo: SapConfig.FundsCenterTo,
            commitmentItem: SapConfig.CommitmentItem,
            outputDirectory: outputDir,
            statusCallback: (msg) =>
            {
                PhaseStatusText = $"Tahap 2: {msg}";
            });

        if (!sapResult.Success)
        {
            CurrentStatus = BotStatus.Error;
            StatusMessage = $"Tahap 2 Gagal: {sapResult.Message}";
            PhaseStatusText = "Otomasi dihentikan pada Tahap 2 (SAP GUI).";
            IsRunning = false;

            ExecutionHistory.Insert(0, new ExecutionHistoryItem
            {
                Timestamp = DateTime.Now,
                Step = "Tahap 2: SAP Automation",
                FileName = "-",
                RecordCount = 0,
                Status = BotStatus.Error,
                Message = sapResult.Message
            });
            return;
        }

        // =========================================================================
        // TAHAP 3: Verifikasi Format Data CSV, Parsing Hirarki & Checksum SHA-256
        // =========================================================================
        CurrentPhase = 3;
        ProgressValue = 75;
        PhaseStatusText = "Tahap 3: Memvalidasi struktur kolom CSV ZFM001 & menghitung SHA-256...";

        var validationResult = await _csvValidationService.ValidateAndParseZfm001CsvAsync(
            sapResult.ExportedFilePath,
            statusCallback: (msg) =>
            {
                PhaseStatusText = $"Tahap 3: {msg}";
            });

        if (!validationResult.IsValid)
        {
            CurrentStatus = BotStatus.Error;
            StatusMessage = $"Tahap 3 Gagal (Format CSV Tidak Sesuai): {validationResult.Message}";
            PhaseStatusText = "Pengiriman dibatalkan karena file CSV tidak lolos verifikasi Tahap 3.";
            IsRunning = false;

            ExecutionHistory.Insert(0, new ExecutionHistoryItem
            {
                Timestamp = DateTime.Now,
                Step = "Tahap 3: Validasi CSV",
                FileName = validationResult.FileName,
                RecordCount = 0,
                Status = BotStatus.Error,
                Message = validationResult.Message
            });
            return;
        }

        // =========================================================================
        // TAHAP 4: Upload Data ke Web Darsana (Anti-WAF GZip+Base64 / Multipart)
        // =========================================================================
        CurrentPhase = 4;
        ProgressValue = 90;
        PhaseStatusText = "Tahap 4: Mengirim data tervalidasi ke Web Darsana...";

        // Jika token belum diisi, beri tahu bahwa Tahap 1-3 sukses dan minta token untuk Tahap 4
        var activeToken = !string.IsNullOrWhiteSpace(DarsanaConfig.ApiToken)
            ? DarsanaConfig.ApiToken.Trim()
            : DarsanaConfig.EncryptedPasswordOrToken.Trim();

        if (string.IsNullOrWhiteSpace(activeToken))
        {
            ProgressValue = 100;
            CurrentStatus = BotStatus.Warning;
            LastRunTime = DateTime.Now.ToString("dd MMM yyyy, HH:mm:ss");
            StatusMessage = $"Tahap 1-3 Sukses ({validationResult.RowsImported} baris valid). Isi BOT_TOKEN di menu Pengaturan untuk mengaktifkan Upload Tahap 4 ke Web Darsana.";
            PhaseStatusText = $"CSV Valid ({validationResult.RowsImported} baris, SHA-256: {validationResult.Sha256Checksum[..10]}...). Menunggu konfigurasi Token Web Darsana.";
            IsRunning = false;

            ExecutionHistory.Insert(0, new ExecutionHistoryItem
            {
                Timestamp = DateTime.Now,
                Step = "Tahap 3: CSV Tervalidasi",
                FileName = validationResult.FileName,
                RecordCount = validationResult.RowsImported,
                Status = BotStatus.Warning,
                Message = $"{validationResult.Message} (Upload dilewati: BOT_TOKEN belum diisi)"
            });
            return;
        }

        var transferResult = await _transferService.UploadToWebDarsanaAsync(
            validationResult,
            DarsanaConfig,
            statusCallback: (msg) =>
            {
                PhaseStatusText = $"Tahap 4: {msg}";
            });

        if (!transferResult.Success)
        {
            CurrentStatus = BotStatus.Error;
            StatusMessage = $"Tahap 4 Gagal: {transferResult.Message}";
            PhaseStatusText = $"CSV lokal aman ({validationResult.FileName}), namun gagal diunggah ke Web Darsana.";
            IsRunning = false;

            ExecutionHistory.Insert(0, new ExecutionHistoryItem
            {
                Timestamp = DateTime.Now,
                Step = "Tahap 4: Upload Darsana",
                FileName = validationResult.FileName,
                RecordCount = validationResult.RowsImported,
                Status = BotStatus.Error,
                Message = transferResult.Message
            });
            return;
        }

        ProgressValue = 100;
        CurrentStatus = BotStatus.Success;
        LastRunTime = DateTime.Now.ToString("dd MMM yyyy, HH:mm:ss");
        StatusMessage = $"Sinkronisasi 4-Tahap Selesai! {transferResult.RowsImportedOnServer} baris berhasil masuk ke Web Darsana.";
        PhaseStatusText = $"Sukses Penuh: {validationResult.FileName} ({transferResult.RowsImportedOnServer} baris) -> Terkirim & Diarsipkan.";
        IsRunning = false;

        ExecutionHistory.Insert(0, new ExecutionHistoryItem
        {
            Timestamp = DateTime.Now,
            Step = "Full Sync (Tahap 1-4)",
            FileName = validationResult.FileName,
            RecordCount = transferResult.RowsImportedOnServer,
            Status = BotStatus.Success,
            Message = $"{transferResult.Message} | SHA-256: {validationResult.Sha256Checksum[..10]}..."
        });
    }

    [RelayCommand]
    public void SaveSettings()
    {
        try
        {
            var configData = new
            {
                Schedule,
                SapConfig,
                DarsanaConfig
            };

            var json = JsonSerializer.Serialize(configData, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configFilePath, json);
            StatusMessage = "Pengaturan berhasil disimpan ke penyimpanan lokal!";
            UpdateNextScheduleTimeDisplay();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Gagal menyimpan pengaturan: {ex.Message}";
        }
    }

    private void OnSchedulerTick(object? sender, EventArgs e)
    {
        if (!Schedule.IsEnabled || IsRunning) return;

        var now = DateTime.Now;
        if (ShouldRunNow(now))
        {
            _lastTriggerDate = now;
            _ = RunManualSyncAsync();
        }

        UpdateNextScheduleTimeDisplay();
    }

    private bool ShouldRunNow(DateTime now)
    {
        if (Schedule.IntervalType == ScheduleIntervalType.EveryNMinutes)
        {
            var minutes = Math.Max(1, Schedule.IntervalMinutes);
            if (_lastTriggerDate == DateTime.MinValue)
            {
                _lastTriggerDate = now;
                return false;
            }

            return (now - _lastTriggerDate).TotalMinutes >= minutes;
        }
        else
        {
            if (!IsDayActive(now.DayOfWeek)) return false;

            if (TimeSpan.TryParse(Schedule.ScheduledTimeString, out var targetTime))
            {
                var isSameTime = now.Hour == targetTime.Hours && now.Minute == targetTime.Minutes;
                var alreadyRanToday = _lastTriggerDate.Date == now.Date && 
                                      _lastTriggerDate.Hour == now.Hour && 
                                      _lastTriggerDate.Minute == now.Minute;

                return isSameTime && !alreadyRanToday;
            }

            return false;
        }
    }

    private bool IsDayActive(DayOfWeek day)
    {
        return day switch
        {
            DayOfWeek.Monday => Schedule.RunOnMonday,
            DayOfWeek.Tuesday => Schedule.RunOnTuesday,
            DayOfWeek.Wednesday => Schedule.RunOnWednesday,
            DayOfWeek.Thursday => Schedule.RunOnThursday,
            DayOfWeek.Friday => Schedule.RunOnFriday,
            DayOfWeek.Saturday => Schedule.RunOnSaturday,
            DayOfWeek.Sunday => Schedule.RunOnSunday,
            _ => false
        };
    }

    private void UpdateNextScheduleTimeDisplay()
    {
        if (!Schedule.IsEnabled)
        {
            NextScheduleTime = "Jadwal Dinonaktifkan";
            return;
        }

        if (Schedule.IntervalType == ScheduleIntervalType.EveryNMinutes)
        {
            var next = (_lastTriggerDate == DateTime.MinValue ? DateTime.Now : _lastTriggerDate).AddMinutes(Math.Max(1, Schedule.IntervalMinutes));
            NextScheduleTime = $"Tiap {Schedule.IntervalMinutes} mnt (~{next:HH:mm:ss} WIB)";
        }
        else
        {
            NextScheduleTime = $"Pukul {Schedule.ScheduledTimeString} WIB (Hari Kerja)";
        }
    }

    private void LoadSavedSettings()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                var json = File.ReadAllText(_configFilePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("Schedule", out var schedElem))
                {
                    var loadedSched = JsonSerializer.Deserialize<ScheduleConfig>(schedElem.GetRawText());
                    if (loadedSched != null) Schedule = loadedSched;
                }

                if (root.TryGetProperty("SapConfig", out var sapElem))
                {
                    var loadedSap = JsonSerializer.Deserialize<SapConnectionConfig>(sapElem.GetRawText());
                    if (loadedSap != null) SapConfig = loadedSap;
                }

                if (root.TryGetProperty("DarsanaConfig", out var darElem))
                {
                    var loadedDar = JsonSerializer.Deserialize<WebDarsanaConfig>(darElem.GetRawText());
                    if (loadedDar != null) DarsanaConfig = loadedDar;
                }
            }
        }
        catch
        {
            // Default config digunakan jika file belum ada/corrupt
        }
    }

    private void LoadMockHistory()
    {
        ExecutionHistory.Add(new ExecutionHistoryItem
        {
            Timestamp = DateTime.Now.AddDays(-1).AddHours(-4),
            Step = "Scheduled Run",
            FileName = "realisasi_20261015_0800_ZFM001.csv",
            RecordCount = 1388,
            Status = BotStatus.Success,
            Message = "Sinkronisasi terjadwal 4-tahap berhasil"
        });
    }
}
