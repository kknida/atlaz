using System.IO;
using Atlaz.Core.Interfaces;
using Atlaz.Core.Models;
using FluentFTP;
using FluentFTP.Exceptions;

namespace Atlaz.Infrastructure.Transfer;

public class FtpTransferService : ITransferService
{
    public async Task<(bool Success, string Message)> TestConnectionAsync(WebDarsanaConfig config)
    {
        return await Task.Run(() =>
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

            FtpClient? client = null;
            try
            {
                client = CreateFtpClient(host, username, password, port);
                
                // Gunakan AutoConnect agar otomatis mendeteksi TLS/SSL dan enkripsi cPanel
                var profile = client.AutoConnect();

                if (client.IsConnected)
                {
                    var workingDir = client.GetWorkingDirectory();
                    var profileName = profile != null ? profile.ToString() : "Standard FTP";
                    return (true, $"Koneksi FTP BERHASIL ke {host}:{port} sebagai '{username}'! ({profileName} | Dir: {workingDir})");
                }

                return (false, "Gagal terhubung ke server FTP cPanel.");
            }
            catch (FtpException fex) when (fex.Message.Contains("421"))
            {
                return (false, "Server FTP merespons 421: Batas koneksi tercapai. Buka cPanel -> FTP Connections -> Disconnect koneksi lama, lalu coba lagi.");
            }
            catch (Exception ex)
            {
                return (false, $"Gagal koneksi FTP: {GetFullErrorMessage(ex)}");
            }
            finally
            {
                if (client != null)
                {
                    if (client.IsConnected)
                    {
                        try { client.Disconnect(); } catch { }
                    }
                    client.Dispose();
                }
            }
        });
    }

    public async Task<TransferResult> UploadToWebDarsanaAsync(
        CsvValidationResult validationResult,
        WebDarsanaConfig config,
        Action<string>? statusCallback = null)
    {
        return await Task.Run(async () =>
        {
            var result = new TransferResult
            {
                ProtocolUsed = "FTP"
            };

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

            const int maxRetries = 2;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                FtpClient? client = null;
                try
                {
                    statusCallback?.Invoke($"Menghubungkan ke FTP {host}:{port} (Percobaan {attempt}/{maxRetries})...");
                    client = CreateFtpClient(host, username, password, port);

                    // AutoConnect untuk auto-negotiate protokol & SSL
                    client.AutoConnect();

                    if (!client.IsConnected)
                    {
                        result.Success = false;
                        result.Message = "Tidak dapat terhubung ke server FTP cPanel.";
                        return result;
                    }

                    statusCallback?.Invoke($"Memeriksa folder tujuan remote: {remotePath}...");
                    var cleanRemotePath = remotePath.TrimStart('/');
                    if (!client.DirectoryExists(cleanRemotePath))
                    {
                        client.CreateDirectory(cleanRemotePath);
                    }

                    var remoteFilePath = cleanRemotePath.TrimEnd('/') + "/" + validationResult.FileName;

                    statusCallback?.Invoke($"Mengunggah {validationResult.FileName} ({validationResult.FileSizeBytes:N0} bytes) via FTP...");

                    // Gunakan FileStream dengan ReadWrite share agar tidak konflik dengan Excel yang baru ditutup
                    using (var fs = new FileStream(validationResult.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var status = client.UploadStream(fs, remoteFilePath, FtpRemoteExists.Overwrite, createRemoteDir: true);
                        if (status == FtpStatus.Success)
                        {
                            result.ArchivedFilePath = ArchiveLocalCsvFile(validationResult.FilePath);
                            result.Success = true;
                            result.RowsImportedOnServer = validationResult.RowsImported;
                            result.Message = $"File CSV ({validationResult.RowsImported} baris) berhasil diunggah via FTP ke: {remoteFilePath}";

                            statusCallback?.Invoke("Unggah FTP sukses & file diarsipkan.");
                            return result;
                        }
                        else
                        {
                            result.Success = false;
                            result.Message = $"Status unggah FTP: {status}";
                            return result;
                        }
                    }
                }
                catch (FtpException fex) when (fex.Message.Contains("421") && attempt < maxRetries)
                {
                    statusCallback?.Invoke("Server FTP merespons 421. Menunggu 4 detik untuk giliran sesi...");
                    await Task.Delay(4000);
                }
                catch (Exception ex)
                {
                    var detailedMsg = GetFullErrorMessage(ex);
                    if (attempt == maxRetries)
                    {
                        result.Success = false;
                        result.Message = $"Gagal unggah via FTP: {detailedMsg}";
                        return result;
                    }
                    statusCallback?.Invoke($"Percobaan {attempt} gagal ({detailedMsg}). Mencoba ulang...");
                    await Task.Delay(2000);
                }
                finally
                {
                    if (client != null)
                    {
                        if (client.IsConnected)
                        {
                            try { client.Disconnect(); } catch { }
                        }
                        client.Dispose();
                    }
                }
            }

            result.Success = false;
            result.Message = "Gagal unggah via FTP setelah beberapa percobaan.";
            return result;
        });
    }

    public async Task<TransferResult> UploadFileAsync(
        string localFilePath,
        WebDarsanaConfig config,
        Action<string>? statusCallback = null)
    {
        return await Task.Run(async () =>
        {
            var result = new TransferResult
            {
                ProtocolUsed = "FTP"
            };

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

            const int maxRetries = 2;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                FtpClient? client = null;
                try
                {
                    statusCallback?.Invoke($"Menghubungkan ke FTP {host}:{port} (Percobaan {attempt}/{maxRetries})...");
                    client = CreateFtpClient(host, username, password, port);

                    client.AutoConnect();

                    if (!client.IsConnected)
                    {
                        result.Success = false;
                        result.Message = "Tidak dapat terhubung ke server FTP cPanel.";
                        return result;
                    }

                    var cleanRemotePath = remotePath.TrimStart('/');
                    if (!client.DirectoryExists(cleanRemotePath))
                    {
                        client.CreateDirectory(cleanRemotePath);
                    }

                    var remoteFilePath = cleanRemotePath.TrimEnd('/') + "/" + fileName;

                    statusCallback?.Invoke($"Mengunggah {fileName} ({fileInfo.Length:N0} bytes) via FTP...");

                    using (var fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var status = client.UploadStream(fs, remoteFilePath, FtpRemoteExists.Overwrite, createRemoteDir: true);
                        if (status == FtpStatus.Success)
                        {
                            result.ArchivedFilePath = ArchiveLocalCsvFile(localFilePath);
                            result.Success = true;
                            result.Message = $"File {fileName} ({fileInfo.Length:N0} bytes) berhasil diunggah via FTP ke: {remoteFilePath}";
                            statusCallback?.Invoke($"Unggah FTP sukses: {fileName}");
                            return result;
                        }
                        else
                        {
                            result.Success = false;
                            result.Message = $"Status unggah FTP: {status}";
                            return result;
                        }
                    }
                }
                catch (FtpException fex) when (fex.Message.Contains("421") && attempt < maxRetries)
                {
                    statusCallback?.Invoke("Server FTP merespons 421. Menunggu 4 detik untuk giliran sesi...");
                    await Task.Delay(4000);
                }
                catch (Exception ex)
                {
                    var detailedMsg = GetFullErrorMessage(ex);
                    if (attempt == maxRetries)
                    {
                        result.Success = false;
                        result.Message = $"Gagal unggah {fileName} via FTP: {detailedMsg}";
                        return result;
                    }
                    statusCallback?.Invoke($"Percobaan {attempt} gagal ({detailedMsg}). Mencoba ulang...");
                    await Task.Delay(2000);
                }
                finally
                {
                    if (client != null)
                    {
                        if (client.IsConnected)
                        {
                            try { client.Disconnect(); } catch { }
                        }
                        client.Dispose();
                    }
                }
            }

            result.Success = false;
            result.Message = $"Gagal unggah {fileName} via FTP setelah beberapa percobaan.";
            return result;
        });
    }

    private static FtpClient CreateFtpClient(string host, string username, string password, int port)
    {
        var client = new FtpClient(host, username, password, port);
        client.Config.ConnectTimeout = 15000;
        client.Config.ReadTimeout = 20000;
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        client.Config.SocketKeepAlive = false;
        client.Config.DisconnectWithQuit = true;
        // Izinkan semua sertifikat SSL/TLS jika cPanel menggunakan self-signed cert
        client.Config.ValidateAnyCertificate = true;
        return client;
    }

    private static string GetFullErrorMessage(Exception ex)
    {
        var msg = ex.Message;
        var inner = ex.InnerException;
        while (inner != null)
        {
            msg += " -> " + inner.Message;
            inner = inner.InnerException;
        }
        return msg;
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
