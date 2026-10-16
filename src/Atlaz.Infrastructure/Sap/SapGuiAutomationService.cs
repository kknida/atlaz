using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Atlaz.Core.Interfaces;

namespace Atlaz.Infrastructure.Sap;

public class SapGuiAutomationService : ISapAutomationService
{
    public bool IsSapGuiInstalled()
    {
        try
        {
            var type = Type.GetTypeFromProgID("SapROTWr.SapROTWrapper");
            if (type != null) return true;

            var defaultPath32 = @"C:\Program Files (x86)\SAP\FrontEnd\SAPgui\saplogon.exe";
            var defaultPath64 = @"C:\Program Files\SAP\FrontEnd\SAPgui\saplogon.exe";
            return File.Exists(defaultPath32) || File.Exists(defaultPath64);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Melakukan diagnosis langkah-per-langkah kesiapan SAP GUI Scripting.
    /// Mengembalikan daftar hasil cek (langkah, status, pesan).
    /// </summary>
    public List<(string Langkah, bool Sukses, string Pesan)> Diagnose()
    {
        var hasil = new List<(string, bool, string)>();

        // Langkah 1: Cek apakah COM class terdaftar
        var rotType = Type.GetTypeFromProgID("SapROTWr.SapROTWrapper");
        bool comTerdaftar = rotType != null;
        hasil.Add(("1. COM SapROTWr terdaftar",
            comTerdaftar,
            comTerdaftar
                ? "✔ Komponen SAP GUI Scripting terdeteksi di registri Windows."
                : "✘ 'SapROTWr.SapROTWrapper' tidak ditemukan. Pastikan SAP GUI sudah terinstal dengan opsi Scripting."));

        if (!comTerdaftar) return hasil;

        // Langkah 2: Cek proses saplogon berjalan
        var proses = Process.GetProcessesByName("saplogon");
        bool sapBerjalan = proses.Length > 0;
        hasil.Add(("2. Proses saplogon.exe berjalan",
            sapBerjalan,
            sapBerjalan
                ? $"✔ SAP Logon sedang berjalan ({proses.Length} proses)."
                : "✘ SAP Logon tidak berjalan. Buka SAP Logon terlebih dahulu sebelum menjalankan Atlaz."));

        if (!sapBerjalan) return hasil;

        // Langkah 3: Cek GetROTEntry("SAPGUI")
        object? rotWrapper = null;
        object? sapRotObj = null;
        try
        {
            rotWrapper = Activator.CreateInstance(rotType!);
            sapRotObj = rotType!.InvokeMember("GetROTEntry",
                System.Reflection.BindingFlags.InvokeMethod, null, rotWrapper, ["SAPGUI"]);

            bool rotBerhasil = sapRotObj != null;
            hasil.Add(("3. GetROTEntry('SAPGUI') berhasil",
                rotBerhasil,
                rotBerhasil
                    ? "✔ SAP GUI ROT entry ditemukan."
                    : "✘ GetROTEntry mengembalikan null. Kemungkinan: (a) Tidak ada koneksi SAP yang terbuka, atau (b) Scripting belum diaktifkan di SAP GUI Options."));

            if (!rotBerhasil) return hasil;

            // Langkah 4: Cek GetScriptingEngine
            var sapRotType = sapRotObj!.GetType();
            var engine = sapRotType.InvokeMember("GetScriptingEngine",
                System.Reflection.BindingFlags.InvokeMethod, null, sapRotObj, null);
            bool engineOk = engine != null;
            hasil.Add(("4. Scripting Engine aktif",
                engineOk,
                engineOk
                    ? "✔ SAP GUI Scripting Engine aktif dan siap digunakan."
                    : "✘ Scripting Engine tidak aktif. Di SAP: Options → Accessibility & Scripting → centang 'Enable Scripting'. Hubungi Basis jika perlu aktivasi server-side (sapgui/user_scripting=TRUE)."));
        }
        catch (Exception ex)
        {
            hasil.Add(("3-4. Uji COM", false, $"✘ Terjadi exception saat uji COM: {ex.Message}"));
        }
        finally
        {
            ReleaseCom(sapRotObj);
            ReleaseCom(rotWrapper);
        }

        return hasil;
    }


    public async Task<SapExecutionResult> ExecuteZfm001DownloadAsync(
        string systemId,
        string client,
        string username,
        string password,
        string fmArea,
        string budgetCategory,
        string version,
        string fiscalYear,
        string fromPeriod,
        string toPeriod,
        string fundsCenterFrom,
        string fundsCenterTo,
        string commitmentItem,
        string outputDirectory,
        Action<string>? statusCallback = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        return await Task.Run(() =>
        {
            object? rotWrapper = null;
            object? sapRotObj = null;
            object? sapGuiApp = null;
            object? connection = null;
            object? session = null;

            try
            {
                statusCallback?.Invoke("Memeriksa proses SAP GUI...");

                // Pastikan folder output ada
                Directory.CreateDirectory(outputDirectory);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var outputFilePath = Path.Combine(outputDirectory, $"ZFM001_{timestamp}.csv");

                // 1. Cek apakah saplogon sedang berjalan, jika belum jalankan
                var sapLogonProcesses = Process.GetProcessesByName("saplogon");
                if (sapLogonProcesses.Length == 0)
                {
                    statusCallback?.Invoke("Membuka aplikasi SAP Logon...");
                    var sapPaths = new[]
                    {
                        @"C:\Program Files (x86)\SAP\FrontEnd\SAPgui\saplogon.exe",
                        @"C:\Program Files\SAP\FrontEnd\SAPgui\saplogon.exe"
                    };

                    var exePath = sapPaths.FirstOrDefault(File.Exists);
                    if (exePath != null)
                    {
                        Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                        Thread.Sleep(5000); // Tunggu GUI terinisialisasi lebih lama
                    }
                    else
                    {
                        throw new FileNotFoundException("SAP Logon (saplogon.exe) tidak ditemukan pada direktori standar. Pastikan SAP GUI sudah terinstal.");
                    }
                }

                statusCallback?.Invoke("Menghubungkan ke SAP GUI Scripting Engine...");

                // 2. Akses COM Scripting Wrapper (Late-binding)
                var rotType = Type.GetTypeFromProgID("SapROTWr.SapROTWrapper")
                    ?? throw new InvalidOperationException("Komponen SAP GUI Scripting (SapROTWr.SapROTWrapper) tidak terdaftar di Windows. Pastikan SAP GUI diinstal beserta komponen SAP GUI Scripting.");

                rotWrapper = Activator.CreateInstance(rotType);

                // Coba retry hingga 5 kali untuk menunggu ROT SAPGUI aktif
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    try
                    {
                        sapRotObj = rotType.InvokeMember("GetROTEntry", System.Reflection.BindingFlags.InvokeMethod, null, rotWrapper, new object[] { "SAPGUI" });
                        if (sapRotObj != null) break;
                    }
                    catch
                    {
                        // Tunggu dan coba kembali
                    }
                    Thread.Sleep(1000);
                }

                if (sapRotObj == null)
                {
                    throw new InvalidOperationException("Gagal mendapatkan entri ROT SAPGUI. Kemungkinan penyebab:\n1. SAP Logon belum terbuka atau baru dibuka (buka jendela SAP Logon terlebih dahulu).\n2. Fitur Scripting belum diaktifkan di SAP GUI (Buka SAP Logon -> Options -> Accessibility & Scripting -> Scripting -> Centang 'Enable scripting').\n3. Aplikasi Atlaz dan SAP GUI berjalan di level hak akses (privilege) yang berbeda (misal yang satu Run as Administrator, yang lain tidak).");
                }

                var sapRotType = sapRotObj.GetType();
                sapGuiApp = sapRotType.InvokeMember("GetScriptingEngine", System.Reflection.BindingFlags.InvokeMethod, null, sapRotObj, null);

                if (sapGuiApp == null)
                {
                    throw new InvalidOperationException("Scripting Support tidak aktif di SAP GUI atau server. Pastikan 'Enable scripting' dicentang di SAP GUI Options dan parameter sapgui/user_scripting=TRUE di server SAP.");
                }


                // 3. Akses atau Buka Koneksi SAP
                var appType = sapGuiApp.GetType();
                var appChildren = appType.InvokeMember("Children", System.Reflection.BindingFlags.GetProperty, null, sapGuiApp, null);
                var appChildrenCount = 0;
                if (appChildren != null)
                {
                    try
                    {
                        appChildrenCount = (int)appChildren.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, appChildren, null)!;
                    }
                    catch { }
                }

                if (appChildrenCount > 0)
                {
                    // Gunakan koneksi yang sudah terbuka
                    statusCallback?.Invoke("Menggunakan koneksi SAP yang sedang terbuka...");
                    connection = appChildren!.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, appChildren, new object[] { 0 });
                }
                else
                {
                    // Buka koneksi baru sesuai nama server/deskripsi di SAP Logon
                    statusCallback?.Invoke($"Membuka koneksi ke sistem SAP: '{systemId}'...");
                    try
                    {
                        connection = appType.InvokeMember("OpenConnection", System.Reflection.BindingFlags.InvokeMethod, null, sapGuiApp, new object[] { systemId, true });
                    }
                    catch (Exception exOpen)
                    {
                        var inner = exOpen.InnerException?.Message ?? exOpen.Message;
                        throw new InvalidOperationException($"Gagal membuka koneksi SAP dengan System ID / Nama Server '{systemId}'. Pastikan nama ini persis sama dengan nama entri di SAP Logon. Detail: {inner}");
                    }
                }

                if (connection == null)
                {
                    throw new InvalidOperationException($"Koneksi ke SAP Server '{systemId}' tidak ditemukan atau gagal dibuat. Pastikan nama koneksi sesuai dengan entri di SAP Logon.");
                }

                // Berikan jeda sejenak agar jendela SAP GUI selesai render
                Thread.Sleep(1500);

                var connType = connection.GetType();
                var children = connType.InvokeMember("Children", System.Reflection.BindingFlags.GetProperty, null, connection, null);
                var childrenType = children!.GetType();
                session = childrenType.InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, children, new object[] { 0 });

                if (session == null)
                {
                    throw new InvalidOperationException("Gagal memperoleh sesi SAP GUI yang aktif dari koneksi yang dipilih.");
                }

                var sessionType = session.GetType();

                // 4. Proses Login (hanya jika masih di layar login SAP wnd[0]/usr/txtRSYST-BNAME)
                bool isLoginScreen = false;
                try
                {
                    var userBox = sessionType.InvokeMember("FindById", System.Reflection.BindingFlags.InvokeMethod, null, session, new object[] { "wnd[0]/usr/txtRSYST-BNAME" });
                    isLoginScreen = userBox != null;
                }
                catch { }

                if (isLoginScreen)
                {
                    statusCallback?.Invoke($"Melakukan login otomatis (Client: {client}, User: {username})...");
                    FindAndSetText(session, sessionType, "wnd[0]/usr/txtRSYST-MANDT", client);
                    FindAndSetText(session, sessionType, "wnd[0]/usr/txtRSYST-BNAME", username);
                    FindAndSetText(session, sessionType, "wnd[0]/usr/pwdRSYST-BCODE", password);
                    FindAndSetText(session, sessionType, "wnd[0]/usr/txtRSYST-LANGU", "EN"); // Default English

                    // Tekan Enter untuk Login
                    SendVKey(session, sessionType, 0); // VKey 0 = Enter

                    // Handle Dialog Informasi / Lisensi multi-logon jika ada
                    HandlePossiblePopups(session, sessionType);
                }
                else
                {
                    statusCallback?.Invoke("Sesi SAP sudah dalam posisi login. Melanjutkan langsung ke transaksi...");
                }

                // 5. Buka Transaksi ZFM001
                statusCallback?.Invoke("Membuka transaksi /nZFM001...");
                FindAndSetText(session, sessionType, "wnd[0]/tbar[0]/okcd", "/nZFM001");
                SendVKey(session, sessionType, 0); // Enter

                // Handle jika ada popup dialog informasi (misal: 'Username/Funds center anda belum di maintain')
                Thread.Sleep(1000);
                HandlePossiblePopups(session, sessionType);

                // 6. Masukkan Parameter Transaksi Sesuai Form 'Report FM for AirNav 2024: Selection'
                statusCallback?.Invoke($"Memasukkan parameter transaksi (FM Area: {fmArea}, Tahun: {fiscalYear}, Funds Center: {fundsCenter})...");
                
                // Selection values
                TrySetTextIfExists(session, sessionType, "wnd[0]/usr/ctxtP_FIKRS", fmArea);           // Financial Management Area
                TrySetTextIfExists(session, sessionType, "wnd[0]/usr/ctxtP_FIKRS-LOW", fmArea);
                TrySetTextIfExists(session, sessionType, "wnd[0]/usr/ctxtP_FONDS", budgetCategory);  // Budget Category (9F)
                TrySetTextIfExists(session, sessionType, "wnd[0]/usr/txtP_VERSN", version);          // Version (0)
                TrySetTextIfExists(session, sessionType, "wnd[0]/usr/txtP_GJAHR", fiscalYear);       // Fiscal Year (2026)
                TrySetTextIfExists(session, sessionType, "wnd[0]/usr/txtP_PERIO", fromPeriod);       // From Period (1)
                TrySetTextIfExists(session, sessionType, "wnd[0]/usr/txtP_PERIOT", toPeriod);        // To Period (12)
                TrySetTextIfExists(session, sessionType, "wnd[0]/usr/txtP_PERTO", toPeriod);

                // Selection groups (Funds Center & Commitment Item)
                TrySetTextIfExists(session, sessionType, "wnd[0]/usr/ctxtS_FICTR-LOW", fundsCenterFrom); // Funds Center (From)
                TrySetTextIfExists(session, sessionType, "wnd[0]/usr/ctxtS_FICTR-HIGH", fundsCenterTo);   // Funds Center (To)
                TrySetTextIfExists(session, sessionType, "wnd[0]/usr/ctxtS_FIPEX-LOW", commitmentItem);  // Commitment Item jika diisi
                
                // 7. Eksekusi Report (F8)
                statusCallback?.Invoke("Mengeksekusi transaksi (F8)...");
                SendVKey(session, sessionType, 8); // VKey 8 = F8 (Execute)

                // Cek kemungkinan dialog/popup peringatan setelah F8
                Thread.Sleep(1500);
                HandlePossiblePopups(session, sessionType);

                // Tunggu hingga report selesai dirender
                Thread.Sleep(2500);

                // 8. Ekspor ke File CSV Lokal
                statusCallback?.Invoke($"Mengunduh laporan ke file CSV: {Path.GetFileName(outputFilePath)}...");
                ExportReportToCsv(session, sessionType, outputFilePath);

                // 9. Logout dari SAP (Clean Logout)
                statusCallback?.Invoke("Menutup sesi transaksi dan logout dari SAP (/nex)...");
                FindAndSetText(session, sessionType, "wnd[0]/tbar[0]/okcd", "/nex");
                SendVKey(session, sessionType, 0);

                stopwatch.Stop();
                return new SapExecutionResult
                {
                    Success = true,
                    ExportedFilePath = outputFilePath,
                    Duration = stopwatch.Elapsed,
                    Message = "Otomasi SAP GUI berhasil: login, transaksi ZFM001, unduh CSV, dan logout tuntas."
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                var deepError = GetFullErrorMessage(ex);
                statusCallback?.Invoke($"Gagal pada otomasi SAP: {deepError}");
                return new SapExecutionResult
                {
                    Success = false,
                    Duration = stopwatch.Elapsed,
                    Message = $"Error SAP Automation: {deepError}"
                };
            }

            finally
            {
                // Graceful cleanup COM objects
                ReleaseCom(session);
                ReleaseCom(connection);
                ReleaseCom(sapGuiApp);
                ReleaseCom(sapRotObj);
                ReleaseCom(rotWrapper);
            }
        }, ct);
    }

    private static void FindAndSetText(object session, Type sessionType, string elementId, string text)
    {
        var element = sessionType.InvokeMember("FindById", System.Reflection.BindingFlags.InvokeMethod, null, session, new object[] { elementId });
        if (element != null)
        {
            element.GetType().InvokeMember("Text", System.Reflection.BindingFlags.SetProperty, null, element, new object[] { text });
        }
    }

    private static void TrySetTextIfExists(object session, Type sessionType, string elementId, string text)
    {
        try
        {
            FindAndSetText(session, sessionType, elementId, text);
        }
        catch
        {
            // Element id mungkin berbeda tergantung konfigurasi varian ZFM001
        }
    }

    private static void SendVKey(object session, Type sessionType, int vkey)
    {
        var window = sessionType.InvokeMember("FindById", System.Reflection.BindingFlags.InvokeMethod, null, session, new object[] { "wnd[0]" });
        if (window != null)
        {
            window.GetType().InvokeMember("SendVKey", System.Reflection.BindingFlags.InvokeMethod, null, window, new object[] { vkey });
        }
    }

    private static void HandlePossiblePopups(object session, Type sessionType)
    {
        try
        {
            // Cek jika ada dialog modal wnd[1] seperti Copyright atau Multi Logon
            var popup = sessionType.InvokeMember("FindById", System.Reflection.BindingFlags.InvokeMethod, null, session, new object[] { "wnd[1]" });
            if (popup != null)
            {
                // Pilih opsi "Continue with this logon" (radio button 1) jika ada, lalu Enter
                try
                {
                    var radio = sessionType.InvokeMember("FindById", System.Reflection.BindingFlags.InvokeMethod, null, session, new object[] { "wnd[1]/usr/radMULTI_LOGON_OPT1" });
                    if (radio != null)
                    {
                        radio.GetType().InvokeMember("Select", System.Reflection.BindingFlags.InvokeMethod, null, radio, null);
                    }
                }
                catch { }

                var popWindow = popup.GetType();
                popWindow.InvokeMember("SendVKey", System.Reflection.BindingFlags.InvokeMethod, null, popup, new object[] { 0 }); // Enter
            }
        }
        catch
        {
            // Tidak ada popup
        }
    }

    private static void ExportReportToCsv(object session, Type sessionType, string outputFilePath)
    {
        var dir = Path.GetDirectoryName(outputFilePath) ?? "";
        var fileName = Path.GetFileName(outputFilePath);

        bool exportBerhasil = false;

        // Opsi 1: Menu Report -> Export / Save to Local File (mbar menu[0])
        try
        {
            // Coba trigger menu Report -> Export (umumnya menu[0]/menu[1] atau menu[0]/menu[2] atau menu[0]/menu[3])
            string[] possibleMenuPaths = new[]
            {
                "wnd[0]/mbar/menu[0]/menu[1]",       // Report -> Export
                "wnd[0]/mbar/menu[0]/menu[2]",       // Report -> Export/Save
                "wnd[0]/mbar/menu[0]/menu[3]/menu[2]", // List -> Export -> Spreadsheet / Local File
                "wnd[0]/mbar/menu[0]/menu[1]/menu[2]"
            };

            foreach (var menuPath in possibleMenuPaths)
            {
                try
                {
                    var menuElem = sessionType.InvokeMember("FindById", System.Reflection.BindingFlags.InvokeMethod, null, session, new object[] { menuPath });
                    if (menuElem != null)
                    {
                        menuElem.GetType().InvokeMember("Select", System.Reflection.BindingFlags.InvokeMethod, null, menuElem, null);
                        Thread.Sleep(1000);
                        if (ProcessExportDialog(session, sessionType, dir, fileName))
                        {
                            exportBerhasil = true;
                            break;
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        // Opsi 2: Toolbar command %PC jika menu bar tidak langsung membuka dialog
        if (!exportBerhasil)
        {
            try
            {
                FindAndSetText(session, sessionType, "wnd[0]/tbar[0]/okcd", "%PC");
                SendVKey(session, sessionType, 0); // Enter
                Thread.Sleep(1000);
                exportBerhasil = ProcessExportDialog(session, sessionType, dir, fileName);
            }
            catch { }
        }

        // Opsi 3: Toolbar ALV Export Button
        if (!exportBerhasil)
        {
            try
            {
                // Tombol icon export di toolbar ALV grid (btn[45] atau btn[43])
                string[] possibleButtons = new[]
                {
                    "wnd[0]/tbar[1]/btn[45]",
                    "wnd[0]/tbar[1]/btn[43]",
                    "wnd[0]/tbar[1]/btn[48]"
                };

                foreach (var btnId in possibleButtons)
                {
                    try
                    {
                        var btn = sessionType.InvokeMember("FindById", System.Reflection.BindingFlags.InvokeMethod, null, session, new object[] { btnId });
                        if (btn != null)
                        {
                            btn.GetType().InvokeMember("Press", System.Reflection.BindingFlags.InvokeMethod, null, btn, null);
                            Thread.Sleep(1000);
                            if (ProcessExportDialog(session, sessionType, dir, fileName))
                            {
                                exportBerhasil = true;
                                break;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Berikan toleransi hingga 3 detik untuk penulisan file ke disk oleh SAP GUI
        for (int i = 0; i < 6; i++)
        {
            if (File.Exists(outputFilePath) && new FileInfo(outputFilePath).Length > 0)
            {
                return;
            }
            Thread.Sleep(500);
        }

        if (!File.Exists(outputFilePath) || new FileInfo(outputFilePath).Length == 0)
        {
            throw new InvalidOperationException($"Proses ekspor selesai tetapi file hasil unduhan SAP '{fileName}' tidak ditemukan di folder '{dir}'. Pastikan dialog ekspor disimpan ke format .csv.");
        }
    }

    private static bool ProcessExportDialog(object session, Type sessionType, string dir, string fileName)
    {
        try
        {
            // Cek jika muncul dialog pilihan format (wnd[1])
            var popup = sessionType.InvokeMember("FindById", System.Reflection.BindingFlags.InvokeMethod, null, session, new object[] { "wnd[1]" });
            if (popup != null)
            {
                // Pilih opsi format: Spreadsheet / Text with Tabs / Unconverted
                TrySelectRadio(session, sessionType, "wnd[1]/usr/radRB_OTHERS");
                TrySelectRadio(session, sessionType, "wnd[1]/usr/radRB_1");
                SendVKey(session, sessionType, 0); // Enter konfirmasi format
                Thread.Sleep(1000);
            }

            // Dialog simpan direktori & nama file (wnd[1] berikutnya)
            var saveDialog = sessionType.InvokeMember("FindById", System.Reflection.BindingFlags.InvokeMethod, null, session, new object[] { "wnd[1]" });
            if (saveDialog != null)
            {
                // Masukkan Folder & Nama File CSV
                TrySetTextIfExists(session, sessionType, "wnd[1]/usr/ctxtDY_PATH", dir);
                TrySetTextIfExists(session, sessionType, "wnd[1]/usr/ctxtDY_FILENAME", fileName);

                // Tekan tombol Generate / Tombol Hijau / Tombol Simpan
                var generateBtn = sessionType.InvokeMember("FindById", System.Reflection.BindingFlags.InvokeMethod, null, session, new object[] { "wnd[1]/tbar[0]/btn[0]" });
                if (generateBtn != null)
                {
                    generateBtn.GetType().InvokeMember("Press", System.Reflection.BindingFlags.InvokeMethod, null, generateBtn, null);
                }
                else
                {
                    SendVKey(session, sessionType, 0); // Enter
                }
                return true;
            }
        }
        catch { }

        return false;
    }


    private static void TrySelectRadio(object session, Type sessionType, string elementId)
    {
        try
        {
            var elem = sessionType.InvokeMember("FindById", System.Reflection.BindingFlags.InvokeMethod, null, session, new object[] { elementId });
            elem?.GetType().InvokeMember("Select", System.Reflection.BindingFlags.InvokeMethod, null, elem, null);
        }
        catch { }
    }

    private static string GetFullErrorMessage(Exception ex)
    {
        var messages = new List<string>();
        var curr = ex;
        while (curr != null)
        {
            if (!string.IsNullOrWhiteSpace(curr.Message))
            {
                messages.Add(curr.Message);
            }
            curr = curr.InnerException;
        }

        // Ambil pesan paling spesifik (InnerException terdalam) jika ada TargetInvocationException
        return messages.Count > 1 
            ? string.Join(" -> ", messages) 
            : (messages.FirstOrDefault() ?? "Terjadi kesalahan COM SAP GUI yang tidak diketahui.");
    }

    private static void ReleaseCom(object? comObj)
    {
        if (comObj != null && Marshal.IsComObject(comObj))
        {
            try
            {
                Marshal.FinalReleaseComObject(comObj);
            }
            catch { }
        }
    }
}

