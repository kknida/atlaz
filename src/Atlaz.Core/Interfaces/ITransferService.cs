using Atlaz.Core.Models;

namespace Atlaz.Core.Interfaces;

public interface ITransferService
{
    /// <summary>
    /// Mengirimkan data tervalidasi dari Tahap 3 ke Web Darsana menggunakan jalur yang tahan blokir WAF
    /// (Anti-WAF GZip+Base64 JSON Payload, Multipart CSV, atau SFTP), lalu mengarsipkan file lokal.
    /// </summary>
    Task<TransferResult> UploadToWebDarsanaAsync(
        CsvValidationResult validationResult,
        WebDarsanaConfig config,
        Action<string>? statusCallback = null);

    /// <summary>
    /// Mengunggah file arbiter (misal: Excel Stream2) langsung ke remote path hosting
    /// </summary>
    Task<TransferResult> UploadFileAsync(
        string localFilePath,
        WebDarsanaConfig config,
        Action<string>? statusCallback = null);

    /// <summary>
    /// Menguji koneksi & autentikasi Bearer Token ke endpoint /api/sap/heartbeat Web Darsana.
    /// </summary>
    Task<(bool Success, string Message)> TestConnectionAsync(WebDarsanaConfig config);
}

