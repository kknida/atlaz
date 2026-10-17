using System.IO;
using Atlaz.Core.Interfaces;
using Atlaz.Core.Models;
using FluentFTP;

namespace Atlaz.Infrastructure.Transfer;

public class FtpTransferService : ITransferService
{
    public async Task<(bool Success, string Message)> TestConnectionAsync(WebDarsanaConfig config)
    {
        return await Task.Run(() =>
        {
            try
            {
                var host = (config.Host ?? string.Empty).Trim();
                int port = config.Port > 0 ? config.Port : 21;
                var username = (config.Username ?? string.Empty).Trim();
                var password = (config.EncryptedPasswordOrToken ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(host))
                    return (false, "Host FTP belum diisi di Pengaturan.");

                if (string.IsNullOrWhiteSpace(username))
                    return (false, "Username FTP cPanel belum diisi.");

                if (string.IsNullOrWhiteSpace(password))
                    return (false, "Password FTP cPanel belum diisi.");

                using var client = new FtpClient(host, username, password, port);
                client.Config.ConnectTimeout = 15000;
                client.Config.ReadTimeout = 15000;
                client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;

                client.Connect();

                if (client.IsConnected)
                {
                    var workingDir = client.GetWorkingDirectory();
                    client.Disconnect();
                    return (true, $"Koneksi FTP BERHASIL ke {host}:{port} sebagai '{username}'! (Working Dir: {workingDir})");
                }

                return (false, "Gagal terhubung ke server FTP cPanel.");
            }
            catch (Exception ex)
            {
                return (false, $"Gagal koneksi FTP: {ex.Message}");
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
                ProtocolUsed = "FTP"
            };

            try
            {
                var host = (config.Host ?? string.Empty).Trim();
                int port = config.Port > 0 ? config.Port : 21;
                var username = (config.Username ?? string.Empty).Trim();
                var password = (config.EncryptedPasswordOrToken ?? string.Empty).Trim();
                var remotePath = (config.RemoteStagingPath ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    result.Success = false;
                    result.Message = "Konfigurasi FTP belum lengkap (Host, Username, atau Password kosong).";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(remotePath))
                {
                    remotePath = "storage/app/sap/incoming";
                }

                statusCallback?.Invoke($"Menghubungkan ke FTP {host}:{port}...");
                using var client = new FtpClient(host, username, password, port);
                client.Config.ConnectTimeout = 20000;
                client.Config.ReadTimeout = 20000;
                client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;

                client.Connect();

                if (!client.IsConnected)
                {
                    result.Success = false;
                    result.Message = "Tidak dapat terhubung ke server FTP cPanel.";
                    return result;
                }

                statusCallback?.Invoke($"Memeriksa folder tujuan remote: {remotePath}...");
                // Pastikan folder remote ada
                var cleanRemotePath = remotePath.TrimStart('/');
                if (!client.DirectoryExists(cleanRemotePath))
                {
                    client.CreateDirectory(cleanRemotePath);
                }

                var remoteFilePath = cleanRemotePath.TrimEnd('/') + "/" + validationResult.FileName;

                statusCallback?.Invoke($"Mengunggah {validationResult.FileName} via FTP Port {port}...");
                var status = client.UploadFile(validationResult.FilePath, remoteFilePath, FtpRemoteExists.Overwrite, createRemoteDir: true);

                client.Disconnect();

                if (status == FtpStatus.Success)
                {
                    result.ArchivedFilePath = ArchiveLocalCsvFile(validationResult.FilePath);
                    result.Success = true;
                    result.RowsImportedOnServer = validationResult.RowsImported;
                    result.Message = $"File CSV ({validationResult.RowsImported} baris) berhasil diunggah via FTP ke: {remoteFilePath}";

                    statusCallback?.Invoke("Unggah FTP sukses & file diarsipkan.");
                }
                else
                {
                    result.Success = false;
                    result.Message = $"Status unggah FTP: {status}";
                }

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Gagal unggah via FTP: {ex.Message}";
                return result;
            }
        });
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

