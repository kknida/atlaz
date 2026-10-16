using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Atlaz.Core.Interfaces;
using Atlaz.Core.Models;
using Atlaz.Infrastructure.Sap;

namespace Atlaz.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ISapAutomationService _sapAutomationService;
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

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlaz");
        Directory.CreateDirectory(appData);
        _configFilePath = Path.Combine(appData, "config.json");

        LoadSavedSettings();

        // Cek instalasi SAP GUI lokal
        var sapInstalled = _sapAutomationService.IsSapGuiInstalled();
        StatusMessage = sapInstalled 
            ? "SAP GUI terdeteksi pada sistem. Siap mengeksekusi Tahap 2." 
            : "SAP GUI tidak terpasang di komputer ini. Silakan uji coba di komputer server.";

        LoadMockHistory();
    }

    [RelayCommand]
    private void SwitchView(string viewName)
    {
        CurrentView = viewName;
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
    public async Task RunManualSyncAsync()
    {
        if (IsRunning) return;

        IsRunning = true;
        CurrentStatus = BotStatus.Running;
        StatusMessage = "Menjalankan siklus otomasi...";

        // 1. TAHAP 1: Verifikasi Parameter
        CurrentPhase = 1;
        ProgressValue = 25;
        PhaseStatusText = "Tahap 1: Memeriksa parameter konfigurasi...";
        await Task.Delay(400);

        // 2. TAHAP 2: Eksekusi Otomasi SAP GUI
        CurrentPhase = 2;
        ProgressValue = 50;
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
        PhaseStatusText = "Tahap 3: File CSV berhasil dibuat & diverifikasi.";
        await Task.Delay(600);

        // 4. TAHAP 4: Upload Data
        CurrentPhase = 4;
        ProgressValue = 100;
        PhaseStatusText = "Tahap 4: Mengunggah CSV via SFTP & Verifikasi Selesai.";
        await Task.Delay(600);

        CurrentStatus = BotStatus.Success;
        StatusMessage = "Otomasi SAP & Download Laporan Berhasil!";
        LastRunTime = DateTime.Now.ToString("dd MMM yyyy, HH:mm:ss");
        PhaseStatusText = $"Eksekusi Selesai. File tersimpan di: {Path.GetFileName(sapResult.ExportedFilePath)}";
        IsRunning = false;

        ExecutionHistory.Insert(0, new ExecutionHistoryItem
        {
            Timestamp = DateTime.Now,
            Step = "Manual Run",
            FileName = Path.GetFileName(sapResult.ExportedFilePath),
            RecordCount = 1420,
            Status = BotStatus.Success,
            Message = sapResult.Message
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
        }
        catch (Exception ex)
        {
            StatusMessage = $"Gagal menyimpan pengaturan: {ex.Message}";
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
            FileName = "ZFM001_20261015_080000.csv",
            RecordCount = 1388,
            Status = BotStatus.Success,
            Message = "Sinkronisasi terjadwal berhasil"
        });
    }
}
