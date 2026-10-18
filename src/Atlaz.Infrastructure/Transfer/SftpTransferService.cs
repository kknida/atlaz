using System.IO;
using Atlaz.Core.Interfaces;
using Atlaz.Core.Models;
using Renci.SshNet;

namespace Atlaz.Infrastructure.Transfer;

public class SftpTransferService : ITransferService
{
    public async Task<(bool Success, string Message)> TestConnectionAsync(WebDarsanaConfig config)
    {
        return await Task.Run(() =>
        {
            try
            {
                var host = (config.Host ?? string.Empty).Trim();
                int port = config.Port > 0 ? config.Port : 22;
                var username = (config.Username ?? string.Empty).Trim();
                var password = (config.EncryptedPasswordOrToken ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(host))
                    return (false, "Host SFTP belum diisi di Pengaturan.");

                if (string.IsNullOrWhiteSpace(username))
                    return (false, "Username SFTP/cPanel belum diisi.");

                if (string.IsNullOrWhiteSpace(password))
                    return (false, "Password SFTP/cPanel belum diisi.");

                using var client = CreateSftpClient(host, port, username, password);
                client.Connect();

                if (client.IsConnected)
                {
                    var workingDir = client.WorkingDirectory;
                    client.Disconnect();
                    return (true, $"Koneksi SFTP BERHASIL ke {host}:{port} sebagai '{username}'! (Working Dir: {workingDir})");
                }

                return (false, "Gagal terhubung ke server SFTP.");
            }
            catch (Exception ex)
            {
                return (false, $"Gagal koneksi SFTP: {ex.Message}");
            }
        });
    }

    public async Task<TransferResult> UploadToWebDarsanaAsync(
        CsvValidationResult validationResult,
        WebDarsanaConfig config,
        Action<string>? statusCallback = null)
    {
        return await Task.Run(() =>
        {
            var result = new TransferResult
            {
                ProtocolUsed = "SFTP"
            };

            try
            {
                var host = (config.Host ?? string.Empty).Trim();
                int port = config.Port > 0 ? config.Port : 22;
                var username = (config.Username ?? string.Empty).Trim();
                var password = (config.EncryptedPasswordOrToken ?? string.Empty).Trim();
                var remotePath = (config.RemoteStagingPath ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    result.Success = false;
                    result.Message = "Konfigurasi SFTP belum lengkap (Host, Username, atau Password kosong).";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(remotePath))
                {
                    remotePath = "storage/app/sap/incoming";
                }

                statusCallback?.Invoke($"Menghubungkan ke SFTP {host}:{port}...");
                using var client = CreateSftpClient(host, port, username, password);
                client.Connect();

                if (!client.IsConnected)
                {
                    result.Success = false;
                    result.Message = "Tidak dapat terhubung ke server SFTP cPanel.";
                    return result;
                }

                statusCallback?.Invoke($"Memeriksa folder tujuan remote: {remotePath}...");
                EnsureRemoteDirectoryExists(client, remotePath);

                var remoteFilePath = remotePath.TrimEnd('/') + "/" + validationResult.FileName;

                statusCallback?.Invoke($"Mengunggah {validationResult.FileName} via SFTP Port {port} (Bypass WAF)...");
                using (var fs = new FileStream(validationResult.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    client.UploadFile(fs, remoteFilePath, canOverride: true);
                }

                client.Disconnect();

                // Arsipkan file lokal
                result.ArchivedFilePath = ArchiveLocalCsvFile(validationResult.FilePath);
                result.Success = true;
                result.RowsImportedOnServer = validationResult.RowsImported;
                result.Message = $"File CSV ({validationResult.RowsImported} baris) berhasil diunggah via SFTP ke: {remoteFilePath}";

                statusCallback?.Invoke("Unggah SFTP sukses & file diarsipkan.");
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Gagal unggah via SFTP: {ex.Message}";
                return result;
            }
        });
    }

    public async Task<TransferResult> UploadFileAsync(
        string localFilePath,
        WebDarsanaConfig config,
        Action<string>? statusCallback = null)
    {
        return await Task.Run(() =>
        {
            var result = new TransferResult
            {
                ProtocolUsed = "SFTP"
            };

            var host = (config.Host ?? string.Empty).Trim();
            int port = config.Port > 0 ? config.Port : 22;
            var username = (config.Username ?? string.Empty).Trim();
            var password = (config.EncryptedPasswordOrToken ?? string.Empty).Trim();
            var remotePath = (config.RemoteStagingPath ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                result.Success = false;
                result.Message = "Konfigurasi SFTP belum lengkap (Host, Username, atau Password kosong).";
                return result;
            }

            if (!File.Exists(localFilePath))
            {
                result.Success = false;
                result.Message = $"File lokal tidak ditemukan: {localFilePath}";
                return result;
            }

            if (string.IsNullOrWhiteSpace(remotePath))
            {
                remotePath = "storage/app/sap/incoming";
            }

            var fileName = Path.GetFileName(localFilePath);
            var fileInfo = new FileInfo(localFilePath);

            try
            {
                statusCallback?.Invoke($"Menghubungkan ke server SFTP {host}:{port}...");
                using var client = CreateSftpClient(host, port, username, password);
                client.Connect();

                if (!client.IsConnected)
                {
                    result.Success = false;
                    result.Message = "Tidak dapat terhubung ke server SFTP cPanel.";
                    return result;
                }

                statusCallback?.Invoke($"Memeriksa folder tujuan remote: {remotePath}...");
                EnsureRemoteDirectoryExists(client, remotePath);

                var remoteFilePath = remotePath.TrimEnd('/') + "/" + fileName;

                statusCallback?.Invoke($"Mengunggah {fileName} ({fileInfo.Length:N0} bytes) via SFTP...");

                using (var fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    client.UploadFile(fs, remoteFilePath, canOverride: true);
                }

                result.ArchivedFilePath = ArchiveLocalCsvFile(localFilePath);
                result.Success = true;
                result.Message = $"File {fileName} ({fileInfo.Length:N0} bytes) berhasil diunggah via SFTP ke: {remoteFilePath}";

                statusCallback?.Invoke($"Unggah SFTP sukses: {fileName}");
                client.Disconnect();
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Gagal unggah {fileName} via SFTP: {ex.Message}";
                return result;
            }
        });
    }

    private static SftpClient CreateSftpClient(string host, int port, string username, string password)
    {
        var connInfo = new ConnectionInfo(
            host,
            port,
            username,
            new PasswordAuthenticationMethod(username, password)
        )
        {
            Timeout = TimeSpan.FromSeconds(25)
        };

        return new SftpClient(connInfo);
    }

    private static void EnsureRemoteDirectoryExists(SftpClient client, string remoteDirectory)
    {
        if (string.IsNullOrWhiteSpace(remoteDirectory)) return;

        var parts = remoteDirectory.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        var currentPath = remoteDirectory.StartsWith('/') ? "/" : "";

        foreach (var part in parts)
        {
            currentPath = currentPath == "/" ? "/" + part : (currentPath == "" ? part : currentPath + "/" + part);
            try
            {
                if (!client.Exists(currentPath))
                {
                    client.CreateDirectory(currentPath);
                }
            }
            catch
            {
                // Lewati jika direktori sudah ada atau tidak diizinkan membuat root
            }
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
}

