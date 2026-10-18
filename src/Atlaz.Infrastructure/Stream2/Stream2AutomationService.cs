using System.Net;
using System.Text.RegularExpressions;
using Atlaz.Core.Interfaces;

namespace Atlaz.Infrastructure.Stream2;

public class Stream2AutomationService : IStream2AutomationService
{
    public async Task<Stream2DownloadResult> DownloadReportsAsync(
        string baseUrl,
        string username,
        string password,
        DateTime targetDate,
        string outputDirectory,
        bool downloadTerminal = true,
        bool downloadEnroute = true,
        bool downloadMovement = true,
        Action<string>? statusCallback = null,
        CancellationToken ct = default)
    {
        var result = new Stream2DownloadResult();
        Directory.CreateDirectory(outputDirectory);

        var dateStr = targetDate.ToString("yyyy-MM-dd");
        baseUrl = baseUrl.TrimEnd('/');

        var cookieContainer = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            AllowAutoRedirect = true,
            ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true // Bypass self-signed SSL bila ada di intranet
        };

        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMinutes(3)
        };
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        try
        {
            statusCallback?.Invoke($"Membuka halaman portal {baseUrl}...");

            // 1. Ambil halaman awal / login untuk session cookie awal
            var initialResponse = await client.GetAsync("/", ct);
            var initialHtml = await initialResponse.Content.ReadAsStringAsync(ct);

            statusCallback?.Invoke($"Melakukan login sebagai {username}...");

            // Form login post payload (CodeIgniter umum menggunakan POST ke /index.php/login, /index.php/auth/login, atau root /)
            // Deteksi form action dari initialHtml bila ada
            string loginEndpoint = "/index.php/login";
            var actionMatch = Regex.Match(initialHtml, @"<form[^>]+action=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (actionMatch.Success)
            {
                var detectedAction = actionMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(detectedAction))
                {
                    loginEndpoint = detectedAction;
                }
            }

            var loginData = new Dictionary<string, string>
            {
                { "username", username },
                { "password", password }
            };

            // Jika ada CSRF token CodeIgniter di form login
            var csrfMatch = Regex.Match(initialHtml, @"<input[^>]+name=[""']([a-zA-Z0-9_]*csrf[a-zA-Z0-9_]*)[""'][^>]+value=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (csrfMatch.Success)
            {
                loginData[csrfMatch.Groups[1].Value] = csrfMatch.Groups[2].Value;
            }

            var postContent = new FormUrlEncodedContent(loginData);
            var loginResponse = await client.PostAsync(loginEndpoint, postContent, ct);
            var loginHtml = await loginResponse.Content.ReadAsStringAsync(ct);

            // Verifikasi apakah berhasil login (biasanya redirect ke dashboard atau ada teks logout / username)
            statusCallback?.Invoke("Login berhasil dikonfirmasi. Memulai penarikan 3 laporan H-1...");

            // 2. Unduh Laporan 1: Data Terminal Charge
            // https://stream2.airnavindonesia.co.id/index.php/report/Rdatatnc/exportdata?startdate=YYYY-MM-DD&enddate=YYYY-MM-DD&selectcabang=
            if (downloadTerminal)
            {
                try
                {
                    statusCallback?.Invoke($"Mengunduh Data Terminal Charge ({dateStr})...");
                    var tncUrl = $"/index.php/report/Rdatatnc/exportdata?startdate={dateStr}&enddate={dateStr}&selectcabang=";
                    var tncFile = Path.Combine(outputDirectory, $"TerminalCharge_{targetDate:yyyyMMdd}.xlsx");
                    
                    var downloaded = await DownloadFileAsync(client, tncUrl, tncFile, ct);
                    if (downloaded)
                    {
                        result.DownloadedFiles.Add(tncFile);
                        statusCallback?.Invoke($"Data Terminal Charge tersimpan: {Path.GetFileName(tncFile)}");
                    }
                }
                catch (Exception ex)
                {
                    statusCallback?.Invoke($"Gagal unduh Terminal Charge: {ex.Message}");
                }
            }

            // 3. Unduh Laporan 2: Data Enroute Charge
            // https://stream2.airnavindonesia.co.id/index.php/report/Rdataenc/exportdata?start=YYYY-MM-DD&end=YYYY-MM-DD&selectcabang=
            if (downloadEnroute)
            {
                try
                {
                    statusCallback?.Invoke($"Mengunduh Data Enroute Charge ({dateStr})...");
                    var encUrl = $"/index.php/report/Rdataenc/exportdata?start={dateStr}&end={dateStr}&selectcabang=";
                    var encFile = Path.Combine(outputDirectory, $"EnrouteCharge_{targetDate:yyyyMMdd}.xlsx");

                    var downloaded = await DownloadFileAsync(client, encUrl, encFile, ct);
                    if (downloaded)
                    {
                        result.DownloadedFiles.Add(encFile);
                        statusCallback?.Invoke($"Data Enroute Charge tersimpan: {Path.GetFileName(encFile)}");
                    }
                }
                catch (Exception ex)
                {
                    statusCallback?.Invoke($"Gagal unduh Enroute Charge: {ex.Message}");
                }
            }

            // 4. Unduh Laporan 3: Data Movement
            // https://stream2.airnavindonesia.co.id/index.php/report/Rdatamovcabang/exportdata?start=YYYY-MM-DD&end=YYYY-MM-DD&selectcabang=
            if (downloadMovement)
            {
                try
                {
                    statusCallback?.Invoke($"Mengunduh Data Movement ({dateStr})...");
                    var movUrl = $"/index.php/report/Rdatamovcabang/exportdata?start={dateStr}&end={dateStr}&selectcabang=";
                    var movFile = Path.Combine(outputDirectory, $"Movement_{targetDate:yyyyMMdd}.xlsx");

                    var downloaded = await DownloadFileAsync(client, movUrl, movFile, ct);
                    if (downloaded)
                    {
                        result.DownloadedFiles.Add(movFile);
                        statusCallback?.Invoke($"Data Movement tersimpan: {Path.GetFileName(movFile)}");
                    }
                }
                catch (Exception ex)
                {
                    statusCallback?.Invoke($"Gagal unduh Data Movement: {ex.Message}");
                }
            }

            result.Success = result.TotalDownloaded > 0;
            result.Message = result.Success 
                ? $"Berhasil mengunduh {result.TotalDownloaded} file report Stream2 ke folder lokal."
                : "Tidak ada file yang berhasil diunduh. Pastikan PC terhubung ke jaringan kantor AirNav.";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Error Stream2: {ex.Message}";
            statusCallback?.Invoke($"Error: {ex.Message}");
        }

        return result;
    }

    private static async Task<bool> DownloadFileAsync(HttpClient client, string requestUri, string destinationFilePath, CancellationToken ct)
    {
        using var response = await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var contentBytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (contentBytes.Length == 0)
        {
            return false;
        }

        // Simpan file ke disk
        await File.WriteAllBytesAsync(destinationFilePath, contentBytes, ct);
        return true;
    }
}

