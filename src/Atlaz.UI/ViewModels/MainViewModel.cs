using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Atlaz.Core.Models;

namespace Atlaz.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
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
    private int _currentPhase = 1; // 1: Interface, 2: SAP, 3: CSV, 4: Web Darsana

    [ObservableProperty]
    private string _phaseStatusText = "Tahap 1: Desain Interface & Konfigurasi Siap";

    [ObservableProperty]
    private double _progressValue = 25.0;

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
        // Mock data untuk demonstrasi Tahap 1
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

        // Simulasi Alur 4 Tahap
        CurrentPhase = 1;
        ProgressValue = 25;
        PhaseStatusText = "Tahap 1: Verifikasi Interface & Parameter Konfigurasi...";
        await Task.Delay(1000);

        CurrentPhase = 2;
        ProgressValue = 50;
        PhaseStatusText = "Tahap 2: Buka SAP GUI -> Eksekusi ZFM001 -> Ekspor CSV Lokal...";
        await Task.Delay(1500);

        CurrentPhase = 3;
        ProgressValue = 75;
        PhaseStatusText = "Tahap 3: Validasi Format CSV (Header, Baris >= 1, Checksum)...";
        await Task.Delay(1200);

        CurrentPhase = 4;
        ProgressValue = 100;
        PhaseStatusText = "Tahap 4: Mengunggah CSV via SFTP & Verifikasi...";
        await Task.Delay(1500);

        CurrentStatus = BotStatus.Success;
        StatusMessage = "Sinkronisasi Berhasil Selesai!";
        LastRunTime = DateTime.Now.ToString("dd MMM yyyy, HH:mm:ss");
        PhaseStatusText = "Siklus Lengkap 4 Tahap Selesai";
        IsRunning = false;

        ExecutionHistory.Insert(0, new ExecutionHistoryItem
        {
            Timestamp = DateTime.Now,
            Step = "Manual Trigger",
            FileName = $"ZFM001_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            RecordCount = 1420,
            Status = BotStatus.Success,
            Message = "Data sukses diekspor dan ditransfer ke server"
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

        ExecutionHistory.Add(new ExecutionHistoryItem
        {
            Timestamp = DateTime.Now.AddDays(-2).AddHours(-4),
            Step = "Scheduled Run",
            FileName = "ZFM001_20261014_080000.csv",
            RecordCount = 1350,
            Status = BotStatus.Success,
            Message = "Sinkronisasi terjadwal berhasil"
        });
    }
}

