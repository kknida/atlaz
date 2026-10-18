using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Atlaz.Core.Interfaces;
using Atlaz.Core.Models;
using Atlaz.Infrastructure.Sap;
using Atlaz.Infrastructure.Stream2;
using Atlaz.Infrastructure.Transfer;
using Atlaz.Infrastructure.Validation;

namespace Atlaz.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ISapAutomationService _sapAutomationService;
    private readonly ICsvValidationService _csvValidationService;
    private readonly ITransferService _darsanaApiService;
    private readonly ITransferService _sftpTransferService;
    private readonly ITransferService _ftpTransferService;
    private readonly IStream2AutomationService _stream2AutomationService;
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
    private string _phaseStatusText = "Pipeline 4-Tahap Siap (SAP GUI -> Validasi CSV -> Upload SFTP cPanel / API)";

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

    [ObservableProperty]
    private Stream2Config _stream2Config = new();

    /// <summary>
    /// Kontrol penampil tombol-tombol uji coba (Tahap 1-3, Tahap 4, dan Stream2).
    /// Dapat disembunyikan (false) saat aplikasi sudah masuk tahap produksi stabil.
    /// </summary>
    [ObservableProperty]
    private bool _isTestControlsVisible = true;

    // History
    public ObservableCollection<ExecutionHistoryItem> ExecutionHistory { get; } = new();

    private readonly System.Windows.Threading.DispatcherTimer _schedulerTimer;
    private DateTime _lastTriggerDate = DateTime.MinValue;

    public MainViewModel()
    {
        _sapAutomationService = new SapGuiAutomationService();
        _csvValidationService = new CsvFileValidationService();
        _darsanaApiService = new DarsanaTransferService();
        _sftpTransferService = new SftpTransferService();
        _ftpTransferService = new FtpTransferService();
        _stream2AutomationService = new Stream2AutomationService();

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

    private ITransferService GetActiveTransferService()
    {
        if (DarsanaConfig.IsSftp)
        {
            // Port 21 = FTP standar cPanel (FluentFTP), Port 22 = SFTP SSH (SSH.NET)
            return DarsanaConfig.Port == 21 ? _ftpTransferService : _sftpTransferService;
        }
        return _darsanaApiService;
    }

    [RelayCommand]
    public async Task TestDarsanaConnectionAsync()
    {
        var service = GetActiveTransferService();
        var protocolName = DarsanaConfig.IsSftp ? (DarsanaConfig.Port == 21 ? "FTP (Port 21)" : "SFTP (Port 22)") : "API";

        StatusMessage = $"Menguji koneksi {protocolName} ke server cPanel...";
        var (success, msg) = await service.TestConnectionAsync(DarsanaConfig);
        CurrentStatus = success ? BotStatus.Success : BotStatus.Warning;
        StatusMessage = msg;

        ExecutionHistory.Insert(0, new ExecutionHistoryItem
        {
            Timestamp = DateTime.Now,
            Step = $"Tes Koneksi {protocolName}",
            FileName = "-",
            RecordCount = 0,
            Status = success ? BotStatus.Success : BotStatus.Error,
            Message = msg
        });
    }

    /// <summary>
    /// TOMBOL 1: Uji coba Tahap 1 s/d 3 saja (Download dari SAP & Validasi CSV Lokal).
    /// Berhenti di lokal, tanpa melakukan upload ke hosting sama sekali.
    /// </summary>
    [RelayCommand]
    public async Task RunPhase1To3Async()
    {
        if (IsRunning) return;

        IsRunning = true;
        CurrentStatus = BotStatus.Running;
        StatusMessage = "Menjalankan Uji Coba Tahap 1 s.d 3 (SAP -> CSV Lokal)...";

        // Tahap 1
        CurrentPhase = 1;
        ProgressValue = 25;
        PhaseStatusText = "Tahap 1: Memeriksa parameter konfigurasi SAP...";
        await Task.Delay(300);

        // Tahap 2
        CurrentPhase = 2;
        ProgressValue = 55;
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
            statusCallback: (msg) => { PhaseStatusText = $"Tahap 2: {msg}"; });

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

        // Tahap 3
        CurrentPhase = 3;
        ProgressValue = 100;
        PhaseStatusText = "Tahap 3: Memvalidasi CSV & menghitung SHA-256...";

        var validationResult = await _csvValidationService.ValidateAndParseZfm001CsvAsync(
            sapResult.ExportedFilePath,
            statusCallback: (msg) => { PhaseStatusText = $"Tahap 3: {msg}"; });

        if (!validationResult.IsValid)
        {
            CurrentStatus = BotStatus.Error;
            StatusMessage = $"Tahap 3 Gagal: {validationResult.Message}";
            PhaseStatusText = "File CSV tidak lolos validasi.";
        }
        else
        {
            CurrentStatus = BotStatus.Success;
            LastRunTime = DateTime.Now.ToString("dd MMM yyyy, HH:mm:ss");
            StatusMessage = $"Uji Tahap 1-3 SUKSES! File CSV lokal siap: {validationResult.FileName} ({validationResult.RowsImported} baris).";
            PhaseStatusText = $"Selesai Tahap 1-3. File tersimpan: {validationResult.FileName} (Siap diuji upload pada Tombol 2).";
        }

        IsRunning = false;
        ExecutionHistory.Insert(0, new ExecutionHistoryItem
        {
            Timestamp = DateTime.Now,
            Step = "Uji Tahap 1-3 (SAP -> CSV)",
            FileName = validationResult.FileName,
            RecordCount = validationResult.RowsImported,
            Status = validationResult.IsValid ? BotStatus.Success : BotStatus.Error,
            Message = validationResult.Message
        });
    }

    /// <summary>
    /// TOMBOL 2: Uji coba Tahap 4 Saja (Upload CSV Terakhir ke Hosting via FTP/SFTP).
    /// Mengambil file CSV yang sudah ada di lokal tanpa login ke SAP sama sekali!
    /// </summary>
    [RelayCommand]
    public async Task RunPhase4OnlyAsync()
    {
        if (IsRunning) return;

        var outputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlaz", "exports");
        if (!Directory.Exists(outputDir))
        {
            StatusMessage = "Folder export lokal belum ada. Silakan jalankan Tahap 1 s.d 3 terlebih dahulu.";
            return;
        }

        // Cari file CSV terbaru yang ada di folder exports
        var latestCsv = Directory.GetFiles(outputDir, "*.csv")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault();

        if (latestCsv == null)
        {
            StatusMessage = "Tidak ada file CSV yang ditemukan di folder exports. Jalankan Tahap 1 s.d 3 terlebih dahulu untuk mengunduh dari SAP.";
            return;
        }

        IsRunning = true;
        CurrentStatus = BotStatus.Running;
        StatusMessage = $"Menjalankan Uji Tahap 4 Saja menggunakan file lokal: {latestCsv.Name} (Tanpa login SAP)...";

        // Tahap 3: Validasi file lokal CSV terlebih dahulu
        CurrentPhase = 3;
        ProgressValue = 30;
        PhaseStatusText = $"Membaca & memvalidasi file lokal: {latestCsv.Name}...";

        var validationResult = await _csvValidationService.ValidateAndParseZfm001CsvAsync(
            latestCsv.FullName,
            statusCallback: (msg) => { PhaseStatusText = msg; });

        if (!validationResult.IsValid)
        {
            CurrentStatus = BotStatus.Error;
            StatusMessage = $"Validasi gagal: {validationResult.Message}";
            IsRunning = false;
            return;
        }

        // Tahap 4: Upload ke hosting (CSV SAP + 3 File XLSX Stream2)
        CurrentPhase = 4;
        ProgressValue = 60;
        var service = GetActiveTransferService();
        var protocolName = DarsanaConfig.IsSftp ? (DarsanaConfig.Port == 21 ? "FTP (Port 21)" : "SFTP (Port 22)") : "API";

        PhaseStatusText = $"Mengunggah CSV SAP ({validationResult.FileName}) via {protocolName}...";

        var transferResult = await service.UploadToWebDarsanaAsync(
            validationResult,
            DarsanaConfig,
            statusCallback: (msg) => { PhaseStatusText = msg; });

        var uploadedFilesList = new List<string>();
        var uploadErrors = new List<string>();

        if (transferResult.Success)
        {
            uploadedFilesList.Add(validationResult.FileName);
        }
        else
        {
            uploadErrors.Add($"CSV SAP: {transferResult.Message}");
        }

        // Cari 3 file Excel Stream2 terbaru dari folder exports/stream2
        var stream2Dir = Path.Combine(outputDir, "stream2");
        var stream2Files = new List<FileInfo>();

        if (Directory.Exists(stream2Dir))
        {
            // Cari dari subdirektori tanggal (yyyyMMdd) terbaru
            var latestDateDir = Directory.GetDirectories(stream2Dir)
                .Select(d => new DirectoryInfo(d))
                .OrderByDescending(d => d.Name)
                .FirstOrDefault();

            if (latestDateDir != null && Directory.Exists(latestDateDir.FullName))
            {
                stream2Files = Directory.GetFiles(latestDateDir.FullName, "*.xlsx")
                    .Concat(Directory.GetFiles(latestDateDir.FullName, "*.xls"))
                    .Select(f => new FileInfo(f))
                    .ToList();
            }

            // Fallback juga cari langsung di root folder stream2 jika ada
            if (stream2Files.Count == 0)
            {
                stream2Files = Directory.GetFiles(stream2Dir, "*.xlsx", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(stream2Dir, "*.xls", SearchOption.AllDirectories))
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(3)
                    .ToList();
            }
        }

        if (stream2Files.Count > 0)
        {
            ProgressValue = 75;
            PhaseStatusText = $"Mengunggah {stream2Files.Count} file Excel Stream2 ke hosting...";

            foreach (var sFile in stream2Files)
            {
                PhaseStatusText = $"Mengunggah {sFile.Name} via {protocolName}...";
                var upRes = await service.UploadFileAsync(sFile.FullName, DarsanaConfig, (msg) => PhaseStatusText = msg);
                if (upRes.Success)
                {
                    uploadedFilesList.Add(sFile.Name);
                }
                else
                {
                    uploadErrors.Add($"{sFile.Name}: {upRes.Message}");
                }
            }
        }
        else
        {
            uploadErrors.Add("File Excel Stream2 belum ada di folder exports/stream2 (Jalankan Tombol 3 terlebih dahulu untuk unduh)");
        }

        ProgressValue = 100;
        IsRunning = false;
        LastRunTime = DateTime.Now.ToString("dd MMM yyyy, HH:mm:ss");

        if (uploadErrors.Count == 0)
        {
            CurrentStatus = BotStatus.Success;
            StatusMessage = $"Uji Upload SUKSES! {uploadedFilesList.Count} file (1 CSV SAP + {stream2Files.Count} XLSX Stream2) berhasil diunggah ke hosting.";
            PhaseStatusText = $"Upload Selesai: {string.Join(", ", uploadedFilesList)}";
        }
        else if (uploadedFilesList.Count > 0)
        {
            CurrentStatus = BotStatus.Warning;
            StatusMessage = $"Upload Sebagian Berhasil ({uploadedFilesList.Count} file): {string.Join(", ", uploadedFilesList)}. Catatan: {string.Join("; ", uploadErrors)}";
            PhaseStatusText = $"Upload sebagian sukses ({uploadedFilesList.Count} file terunggah).";
        }
        else
        {
            CurrentStatus = BotStatus.Error;
            StatusMessage = $"Gagal unggah ke server hosting: {string.Join("; ", uploadErrors)}";
            PhaseStatusText = "Gagal unggah ke server hosting.";
        }

        ExecutionHistory.Insert(0, new ExecutionHistoryItem
        {
            Timestamp = DateTime.Now,
            Step = $"Uji Upload Hosting (CSV + Stream2)",
            FileName = string.Join(", ", uploadedFilesList.Count > 0 ? uploadedFilesList : new List<string> { validationResult.FileName }),
            RecordCount = validationResult.RowsImported,
            Status = CurrentStatus,
            Message = StatusMessage
        });
    }

    /// <summary>
    /// TOMBOL 3: Ekstraksi Nyata Web Portal STREAM2 AirNav (Terminal Charge, Enroute Charge, Movement) periode H-1.
    /// Tombol ini dapat disembunyikan (IsTestControlsVisible = false) setelah masa uji coba selesai.
    /// </summary>
    [RelayCommand]
    public async Task RunStream2DownloadAsync()
    {
        if (IsRunning) return;

        IsRunning = true;
        CurrentStatus = BotStatus.Running;

        var targetDate = DateTime.Today.AddDays(-Stream2Config.TargetDaysAgo);
        StatusMessage = $"Menghubungi Stream2 (H-1: {targetDate:dd MMM yyyy}) dari {Stream2Config.Url}...";
        ProgressValue = 15;
        PhaseStatusText = $"Stream2: Membuka portal & login sebagai {Stream2Config.Username}...";

        var outputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlaz", "exports", "stream2", targetDate.ToString("yyyyMMdd"));

        var streamResult = await _stream2AutomationService.DownloadReportsAsync(
            baseUrl: Stream2Config.Url,
            username: Stream2Config.Username,
            password: Stream2Config.EncryptedPassword,
            targetDate: targetDate,
            outputDirectory: outputDir,
            downloadTerminal: Stream2Config.DownloadTerminalCharge,
            downloadEnroute: Stream2Config.DownloadEnrouteCharge,
            downloadMovement: Stream2Config.DownloadMovement,
            statusCallback: (msg) =>
            {
                PhaseStatusText = msg;
                if (ProgressValue < 70) ProgressValue += 10;
            });

        if (streamResult.Success)
        {
            var uploadedCount = 0;
            var uploadErrors = new List<string>();

            if (streamResult.DownloadedFiles.Count > 0)
            {
                PhaseStatusText = $"Mengunggah {streamResult.DownloadedFiles.Count} file Stream2 ke server hosting...";
                var transferService = GetActiveTransferService();

                foreach (var file in streamResult.DownloadedFiles)
                {
                    var fName = Path.GetFileName(file);
                    PhaseStatusText = $"Mengunggah {fName} ke server hosting...";
                    var uploadResult = await transferService.UploadFileAsync(file, DarsanaConfig, (msg) => PhaseStatusText = msg);
                    if (uploadResult.Success)
                    {
                        uploadedCount++;
                    }
                    else
                    {
                        uploadErrors.Add($"{fName}: {uploadResult.Message}");
                    }
                }
            }

            ProgressValue = 100;
            IsRunning = false;
            LastRunTime = DateTime.Now.ToString("dd MMM yyyy, HH:mm:ss");

            if (uploadErrors.Count == 0)
            {
                CurrentStatus = BotStatus.Success;
                StatusMessage = $"Unduh & Unggah Stream2 SUKSES! {streamResult.TotalDownloaded} file berhasil diunduh dan diunggah ke hosting.";
                PhaseStatusText = $"Selesai ({uploadedCount}/{streamResult.TotalDownloaded} terunggah): {string.Join(", ", streamResult.DownloadedFiles.Select(Path.GetFileName))}";
            }
            else
            {
                CurrentStatus = BotStatus.Warning;
                StatusMessage = $"Unduh Stream2 Sukses, tetapi unggah ke hosting memiliki kendala: {string.Join("; ", uploadErrors)}";
                PhaseStatusText = $"Unduh selesai, upload {uploadedCount}/{streamResult.TotalDownloaded} file sukses.";
            }
        }
        else
        {
            ProgressValue = 100;
            IsRunning = false;
            CurrentStatus = BotStatus.Error;
            StatusMessage = $"Stream2 Gagal: {streamResult.Message}";
            PhaseStatusText = "Gagal mengunduh laporan dari portal Stream2. Pastikan PC terhubung ke intranet AirNav.";
        }

        ExecutionHistory.Insert(0, new ExecutionHistoryItem
        {
            Timestamp = DateTime.Now,
            Step = "Stream2 (Unduh & Unggah Hosting)",
            FileName = streamResult.DownloadedFiles.Count > 0 ? string.Join(", ", streamResult.DownloadedFiles.Select(Path.GetFileName)) : "-",
            RecordCount = streamResult.TotalDownloaded,
            Status = streamResult.Success ? (CurrentStatus == BotStatus.Warning ? BotStatus.Warning : BotStatus.Success) : BotStatus.Error,
            Message = streamResult.Success ? StatusMessage : streamResult.Message
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
        // TAHAP 4: Upload Data ke Web Darsana (SFTP cPanel Bebas WAF / API)
        // =========================================================================
        CurrentPhase = 4;
        ProgressValue = 90;

        ITransferService transferService = GetActiveTransferService();
        var protocolName = DarsanaConfig.IsSftp ? (DarsanaConfig.Port == 21 ? "FTP (Port 21)" : "SFTP (Port 22)") : "API";

        if (DarsanaConfig.IsSftp)
        {
            PhaseStatusText = $"Tahap 4: Mengunggah file CSV via {protocolName} cPanel (Bebas Imunify360)...";

            var host = DarsanaConfig.Host?.Trim();
            var user = DarsanaConfig.Username?.Trim();
            var pass = DarsanaConfig.EncryptedPasswordOrToken?.Trim();

            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            {
                ProgressValue = 100;
                CurrentStatus = BotStatus.Warning;
                LastRunTime = DateTime.Now.ToString("dd MMM yyyy, HH:mm:ss");
                StatusMessage = $"Tahap 1-3 Sukses ({validationResult.RowsImported} baris). Buka menu Konfigurasi Sistem dan isi Host, Username, & Password cPanel SFTP untuk mengunggah.";
                PhaseStatusText = $"CSV Valid ({validationResult.RowsImported} baris). Menunggu kelengkapan akun SFTP cPanel.";
                IsRunning = false;

                ExecutionHistory.Insert(0, new ExecutionHistoryItem
                {
                    Timestamp = DateTime.Now,
                    Step = "Tahap 3: CSV Tervalidasi",
                    FileName = validationResult.FileName,
                    RecordCount = validationResult.RowsImported,
                    Status = BotStatus.Warning,
                    Message = $"{validationResult.Message} (Upload dilewati: Kredensial SFTP cPanel belum lengkap)"
                });
                return;
            }
        }
        else
        {
            PhaseStatusText = "Tahap 4: Mengirim data tervalidasi via API Web Darsana...";
            transferService = _darsanaApiService;

            var activeToken = !string.IsNullOrWhiteSpace(DarsanaConfig.ApiToken)
                ? DarsanaConfig.ApiToken.Trim()
                : DarsanaConfig.EncryptedPasswordOrToken.Trim();

            if (string.IsNullOrWhiteSpace(activeToken))
            {
                ProgressValue = 100;
                CurrentStatus = BotStatus.Warning;
                LastRunTime = DateTime.Now.ToString("dd MMM yyyy, HH:mm:ss");
                StatusMessage = $"Tahap 1-3 Sukses ({validationResult.RowsImported} baris). Isi BOT_TOKEN di menu Pengaturan untuk mengaktifkan Upload API.";
                PhaseStatusText = $"CSV Valid ({validationResult.RowsImported} baris). Menunggu BOT_TOKEN Web Darsana.";
                IsRunning = false;
                return;
            }
        }

        var transferResult = await transferService.UploadToWebDarsanaAsync(
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

        // =========================================================================
        // TAHAP 5 (STREAM2): Unduh 3 Laporan Stream2 (H-1) & Upload ke Hosting
        // =========================================================================
        var stream2UploadedFiles = new List<string>();
        var stream2Errors = new List<string>();

        try
        {
            PhaseStatusText = "Tahap 5: Menghubungi Web Stream2 AirNav (Terminal, Enroute, Movement)...";
            var targetDate = DateTime.Today.AddDays(-Stream2Config.TargetDaysAgo);
            var stream2OutDir = Path.Combine(outputDir, "stream2", targetDate.ToString("yyyyMMdd"));

            var streamResult = await _stream2AutomationService.DownloadReportsAsync(
                baseUrl: Stream2Config.Url,
                username: Stream2Config.Username,
                password: Stream2Config.EncryptedPassword,
                targetDate: targetDate,
                outputDirectory: stream2OutDir,
                downloadTerminal: Stream2Config.DownloadTerminalCharge,
                downloadEnroute: Stream2Config.DownloadEnrouteCharge,
                downloadMovement: Stream2Config.DownloadMovement,
                statusCallback: (msg) => { PhaseStatusText = $"Stream2: {msg}"; });

            if (streamResult.Success && streamResult.DownloadedFiles.Count > 0)
            {
                PhaseStatusText = $"Mengunggah {streamResult.DownloadedFiles.Count} file Stream2 ke server hosting...";
                foreach (var sFile in streamResult.DownloadedFiles)
                {
                    var fName = Path.GetFileName(sFile);
                    PhaseStatusText = $"Mengunggah {fName} ke server hosting...";
                    var upRes = await transferService.UploadFileAsync(sFile, DarsanaConfig, (msg) => PhaseStatusText = msg);
                    if (upRes.Success)
                    {
                        stream2UploadedFiles.Add(fName);
                    }
                    else
                    {
                        stream2Errors.Add($"{fName}: {upRes.Message}");
                    }
                }
            }
            else if (!streamResult.Success)
            {
                stream2Errors.Add($"Stream2: {streamResult.Message}");
            }
        }
        catch (Exception ex)
        {
            stream2Errors.Add($"Stream2 Exception: {ex.Message}");
        }

        ProgressValue = 100;
        IsRunning = false;
        LastRunTime = DateTime.Now.ToString("dd MMM yyyy, HH:mm:ss");

        if (stream2Errors.Count == 0 && stream2UploadedFiles.Count > 0)
        {
            CurrentStatus = BotStatus.Success;
            StatusMessage = $"Sinkronisasi Penuh SUKSES! SAP ({transferResult.RowsImportedOnServer} baris) & {stream2UploadedFiles.Count} file Stream2 berhasil masuk ke server hosting.";
            PhaseStatusText = $"Sukses Total: {validationResult.FileName} + {string.Join(", ", stream2UploadedFiles)} terunggah.";
        }
        else if (stream2Errors.Count > 0)
        {
            CurrentStatus = BotStatus.Warning;
            StatusMessage = $"SAP Sukses ({transferResult.RowsImportedOnServer} baris), namun Stream2 ada catatan: {string.Join("; ", stream2Errors)}";
            PhaseStatusText = $"SAP terunggah. Stream2: {string.Join(", ", stream2Errors)}";
        }
        else
        {
            CurrentStatus = BotStatus.Success;
            StatusMessage = $"Sinkronisasi SAP Selesai ({transferResult.RowsImportedOnServer} baris).";
            PhaseStatusText = $"Sukses: {validationResult.FileName} terunggah.";
        }

        ExecutionHistory.Insert(0, new ExecutionHistoryItem
        {
            Timestamp = DateTime.Now,
            Step = "Full Sync (SAP + Stream2)",
            FileName = validationResult.FileName + (stream2UploadedFiles.Count > 0 ? $" + {stream2UploadedFiles.Count} XLSX Stream2" : ""),
            RecordCount = transferResult.RowsImportedOnServer,
            Status = CurrentStatus,
            Message = StatusMessage
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
                DarsanaConfig,
                Stream2Config,
                IsTestControlsVisible
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

                if (root.TryGetProperty("Stream2Config", out var stElem))
                {
                    var loadedSt = JsonSerializer.Deserialize<Stream2Config>(stElem.GetRawText());
                    if (loadedSt != null) Stream2Config = loadedSt;
                }

                if (root.TryGetProperty("IsTestControlsVisible", out var testElem))
                {
                    IsTestControlsVisible = testElem.GetBoolean();
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
