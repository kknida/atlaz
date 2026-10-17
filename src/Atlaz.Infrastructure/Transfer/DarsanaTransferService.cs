using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atlaz.Core.Interfaces;
using Atlaz.Core.Models;

namespace Atlaz.Infrastructure.Transfer;

public class DarsanaTransferService : ITransferService
{
    private static readonly HttpClient _httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
        // Gunakan User-Agent standar aplikasi resmi (bukan PowerShell/cURL default yang sering diblokir WAF Imunify360)
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 AtlazSync/1.0");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(WebDarsanaConfig config)
    {
        try
        {
            var baseUrl = (config.DashboardUrl ?? string.Empty).Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl))
            {
                return (false, "URL Web Darsana belum diisi.");
            }

            var token = !string.IsNullOrWhiteSpace(config.ApiToken)
                ? config.ApiToken.Trim()
                : config.EncryptedPasswordOrToken.Trim();

            if (string.IsNullOrEmpty(token))
            {
                return (false, "API Bearer Token (BOT_TOKEN) belum diisi di Pengaturan.");
            }

            var heartbeatUrl = $"{baseUrl}/api/sap/heartbeat";
            var payload = new
            {
                status = "idle",
                message = "Uji koneksi dari Aplikasi Desktop Atlaz",
                machineName = Environment.MachineName,
                botVersion = "1.0.0"
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, heartbeatUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var resp = await _httpClient.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (resp.IsSuccessStatusCode)
            {
                return (true, $"Koneksi ke {baseUrl} BERHASIL! (HTTP {(int)resp.StatusCode} - Heartbeat diterima Web Darsana)");
            }

            if ((int)resp.StatusCode == 401)
            {
                return (false, "Koneksi terhubung ke server, tetapi Token ditolak (HTTP 401 Unauthorized). Periksa BOT_TOKEN.");
            }

            return (false, $"Server merespons HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}): {Truncate(body, 180)}");
        }
        catch (Exception ex)
        {
            return (false, $"Gagal menghubungi Web Darsana: {ex.Message}");
        }
    }

    public async Task<TransferResult> UploadToWebDarsanaAsync(
        CsvValidationResult validationResult,
        WebDarsanaConfig config,
        Action<string>? statusCallback = null)
    {
        var result = new TransferResult
        {
            ProtocolUsed = config.TransferProtocol
        };

        try
        {
            var baseUrl = (config.DashboardUrl ?? string.Empty).Trim().TrimEnd('/');
            var token = !string.IsNullOrWhiteSpace(config.ApiToken)
                ? config.ApiToken.Trim()
                : config.EncryptedPasswordOrToken.Trim();

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                result.Success = false;
                result.Message = "URL Web Darsana kosong. Harap isi URL di menu Pengaturan.";
                return result;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                result.Success = false;
                result.Message = "API Bearer Token (BOT_TOKEN) belum diisi di menu Pengaturan.";
                return result;
            }

            // 1. Kirim Heartbeat 'running'
            await SendHeartbeatSafeAsync(baseUrl, token, "running", $"Mengunggah {validationResult.RowsImported} baris data ZFM001...");

            var importUrl = $"{baseUrl}/api/sap/import";

            // 2. Pilih Metode Pengiriman
            if (string.Equals(config.TransferProtocol, "API_GZIP", StringComparison.OrdinalIgnoreCase))
            {
                statusCallback?.Invoke("Mengompresi data (GZip + Base64 JSON + HMAC-SHA256) agar bebas blokir WAF...");
                var gzipResult = await SendGzipBase64PayloadAsync(importUrl, token, validationResult);

                if (gzipResult.Success)
                {
                    result.Success = true;
                    result.RowsImportedOnServer = gzipResult.RowsImported;
                    result.Message = gzipResult.Message;
                }
                else if (gzipResult.StatusCode == 422)
                {
                    // Fallback otomatis ke Multipart jika endpoint darsana belum di-update untuk menerima payload_b64
                    statusCallback?.Invoke("Endpoint meminta format Multipart CSV, mengirim ulang dengan mode kompatibilitas...");
                    var mpResult = await SendMultipartCsvAsync(importUrl, token, validationResult);
                    result.Success = mpResult.Success;
                    result.RowsImportedOnServer = mpResult.RowsImported;
                    result.Message = mpResult.Message;
                }
                else
                {
                    result.Success = false;
                    result.Message = gzipResult.Message;
                }
            }
            else
            {
                statusCallback?.Invoke("Mengunggah file CSV ke /api/sap/import Web Darsana...");
                var mpResult = await SendMultipartCsvAsync(importUrl, token, validationResult);
                result.Success = mpResult.Success;
                result.RowsImportedOnServer = mpResult.RowsImported;
                result.Message = mpResult.Message;
            }

            // 3. Arsipkan file CSV lokal & kirim Heartbeat akhir
            if (result.Success)
            {
                result.ArchivedFilePath = ArchiveLocalCsvFile(validationResult.FilePath);
                await SendHeartbeatSafeAsync(baseUrl, token, "idle", $"Sukses sinkronisasi {result.RowsImportedOnServer} baris dari Atlaz.");
            }
            else
            {
                await SendHeartbeatSafeAsync(baseUrl, token, "error", result.Message);
            }

            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Kesalahan saat upload ke Web Darsana: {ex.Message}";
            return result;
        }
    }

    private async Task<(bool Success, int StatusCode, int RowsImported, string Message)> SendGzipBase64PayloadAsync(
        string importUrl,
        string token,
        CsvValidationResult validationResult)
    {
        // Serialisasi daftar baris siap-insert (tanpa sintaks SQL sama sekali agar tidak memicu WAF SQLi rule)
        var recordsJson = JsonSerializer.Serialize(validationResult.Records);
        var rawBytes = Encoding.UTF8.GetBytes(recordsJson);

        byte[] compressedBytes;
        using (var ms = new MemoryStream())
        {
            using (var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(rawBytes, 0, rawBytes.Length);
            }
            compressedBytes = ms.ToArray();
        }

        string base64Payload = Convert.ToBase64String(compressedBytes);

        // Hitung HMAC-SHA256 Signature dari payload menggunakan Token sebagai kunci
        var keyBytes = Encoding.UTF8.GetBytes(token);
        var sigBytes = HMACSHA256.HashData(keyBytes, Encoding.UTF8.GetBytes(base64Payload));
        string signature = Convert.ToHexString(sigBytes).ToLowerInvariant();

        var requestBody = new
        {
            mode = "atlaz_gzip_records",
            file_name = validationResult.FileName,
            report_date = validationResult.ReportDate,
            sha256 = validationResult.Sha256Checksum,
            rows_count = validationResult.RowsImported,
            branches_count = validationResult.BranchesCount,
            items_count = validationResult.ItemsCount,
            skipped_count = validationResult.SkippedCount,
            signature = signature,
            payload_b64 = base64Payload
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, importUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Atlaz-Signature", signature);
        req.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        using var resp = await _httpClient.SendAsync(req);
        var respText = await resp.Content.ReadAsStringAsync();

        if (resp.IsSuccessStatusCode)
        {
            int rows = ParseRowsImportedFromResponse(respText, validationResult.RowsImported);
            return (true, (int)resp.StatusCode, rows, $"Upload Anti-WAF Sukses ({rows} baris diimpor ke Web Darsana)");
        }

        return (false, (int)resp.StatusCode, 0, $"Gagal upload (HTTP {(int)resp.StatusCode}): {Truncate(respText, 200)}");
    }

    private async Task<(bool Success, int StatusCode, int RowsImported, string Message)> SendMultipartCsvAsync(
        string importUrl,
        string token,
        CsvValidationResult validationResult)
    {
        byte[] fileBytes;
        using (var fs = new FileStream(validationResult.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            fileBytes = ms.ToArray();
        }

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(fileContent, "file", validationResult.FileName);

        using var req = new HttpRequestMessage(HttpMethod.Post, importUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = content;

        using var resp = await _httpClient.SendAsync(req);
        var respText = await resp.Content.ReadAsStringAsync();

        if (resp.IsSuccessStatusCode)
        {
            int rows = ParseRowsImportedFromResponse(respText, validationResult.RowsImported);
            return (true, (int)resp.StatusCode, rows, $"Upload CSV Sukses ({rows} baris diimpor ke Web Darsana)");
        }

        return (false, (int)resp.StatusCode, 0, $"Upload ke Web Darsana Gagal (HTTP {(int)resp.StatusCode}): {Truncate(respText, 200)}");
    }

    private async Task SendHeartbeatSafeAsync(string baseUrl, string token, string status, string message)
    {
        try
        {
            var payload = new
            {
                status = status,
                message = message,
                machineName = Environment.MachineName,
                botVersion = "1.0.0"
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/sap/heartbeat");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            await _httpClient.SendAsync(req);
        }
        catch
        {
            // Abaikan jika heartbeat gagal agar tidak mengganggu alur utama
        }
    }

    private static string ArchiveLocalCsvFile(string sourceFilePath)
    {
        try
        {
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlaz", "archives", DateTime.Now.ToString("yyyy-MM"));
            Directory.CreateDirectory(baseDir);
            var destPath = Path.Combine(baseDir, Path.GetFileName(sourceFilePath));
            File.Copy(sourceFilePath, destPath, overwrite: true);
            return destPath;
        }
        catch
        {
            return sourceFilePath;
        }
    }

    private static int ParseRowsImportedFromResponse(string json, int fallbackCount)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("rows_imported", out var prop) && prop.TryGetInt32(out int count))
            {
                return count;
            }
        }
        catch
        {
        }
        return fallbackCount;
    }

    private static string Truncate(string input, int maxLen)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var clean = input.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean.Length <= maxLen ? clean : clean[..maxLen] + "...";
    }
}

