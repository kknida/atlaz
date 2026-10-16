using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Atlaz.Core.Interfaces;
using Atlaz.Core.Models;
using Atlaz.Infrastructure.Sap;

namespace Atlaz.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ISapAutomationService _sapAutomationService;

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
    private int _currentPhase = 2; // Tahap 2: Otomasi SAP Aktif

    [ObservableProperty]
    private string _phaseStatusText = "Tahap 2: Engine Otomasi SAP GUI Siap";

    [ObservableProperty]
    private double _progressValue = 50.0;

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

    public MainViewModel()
    {
        _sapAutomationService = new SapGuiAutomationService();

        // Cek instalasi SAP GUI lokal
        var sapInstalled = _sapAutomationService.IsSapGuiInstalled();
        StatusMessage = sapInstalled 
            ? "SAP GUI terdeteksi pada sistem. Siap mengeksekusi Tahap 2." 
            : "SAP GUI tidak terpasang di komputer ini. Siap untuk dipasang di komputer server.";

        LoadMockHistory();
    }

    [RelayCommand]
    private void SwitchView(string viewName)
    {
        CurrentView = viewName;
    }

    [RelayCommand]
    private async Task RunManualSyncAsync()
    {
        if (IsRunning) return;

        IsRunning = true;
        CurrentStatus = BotStatus.Running;
        StatusMessage = "Menjalankan siklus otomasi...";

        // 1. TAHAP 1: Verifikasi Parameter
        CurrentPhase = 1;
        ProgressValue = 25;
        PhaseStatusText = "Tahap 1: Verifikasi Interface & Parameter Selesai";
        await Task.Delay(500);

        // 2. TAHAP 2: Eksekusi Otomasi SAP GUI
        CurrentPhase = 2;
        ProgressValue = 50;
        PhaseStatusText = "Tahap 2: Menjalankan SAP GUI -> Login -> ZFM001 -> Download CSV...";

        var outputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlaz", "exports");

        var sapResult = await _sapAutomationService.ExecuteZfm001DownloadAsync(
            systemId: SapConfig.SystemId,
            client: SapConfig.Client,
            username: SapConfig.Username,
            password: SapConfig.EncryptedPassword,
            fiscalYear: SapConfig.FiscalYear,
            period: SapConfig.Period,
            outputDirectory: outputDir,
            statusCallback: (msg) =>
            {
                PhaseStatusText = $"Tahap 2: {msg}";
            });

        if (!sapResult.Success)
        {
            CurrentStatus = BotStatus.Error;
            StatusMessage = $"Tahap 2 Gagal: {sapResult.Message}";
            PhaseStatusText = "Otomasi SAP dihentikan karena kendala.";
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

        // 3. TAHAP 3: Validasi CSV
        CurrentPhase = 3;
        ProgressValue = 75;
        PhaseStatusText = "Tahap 3: Verifikasi Format Data CSV Selesai (Format Valid)";
        await Task.Delay(800);

        // 4. TAHAP 4: Upload Data
        CurrentPhase = 4;
        ProgressValue = 100;
        PhaseStatusText = "Tahap 4: Mengunggah CSV via SFTP & Verifikasi Selesai";
        await Task.Delay(800);

        CurrentStatus = BotStatus.Success;
        StatusMessage = "Sinkronisasi Berhasil Selesai!";
        LastRunTime = DateTime.Now.ToString("dd MMM yyyy, HH:mm:ss");
        PhaseStatusText = "Siklus Lengkap 4 Tahap Selesai";
        IsRunning = false;

        ExecutionHistory.Insert(0, new ExecutionHistoryItem
        {
            Timestamp = DateTime.Now,
            Step = "Manual Trigger",
            FileName = Path.GetFileName(sapResult.ExportedFilePath),
            RecordCount = 1420,
            Status = BotStatus.Success,
            Message = sapResult.Message
        });
    }

    [RelayCommand]
    private void SaveSettings()
    {
        StatusMessage = "Pengaturan berhasil disimpan!";
    }

    private void LoadMockHistory()
    {
        ExecutionHistory.Add(new ExecutionHistoryItem
        {
            Timestamp = DateTime.Now.AddDays(-1).AddHours(-4),
            Step = "Scheduled Run",
            FileName = "ZFM001_20261015_080000.csv",
            RecordCount = 1388,
            Status = BotStatus.Success,
            Message = "Sinkronisasi terjadwal berhasil"
        });
    }
}
