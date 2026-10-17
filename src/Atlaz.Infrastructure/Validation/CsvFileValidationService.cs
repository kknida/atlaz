using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Atlaz.Core.Interfaces;
using Atlaz.Core.Models;

namespace Atlaz.Infrastructure.Validation;

public class CsvFileValidationService : ICsvValidationService
{
    public async Task<CsvValidationResult> ValidateAndParseZfm001CsvAsync(
        string filePath,
        Action<string>? statusCallback = null)
    {
        return await Task.Run(() =>
        {
            var result = new CsvValidationResult
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath)
            };

            try
            {
                statusCallback?.Invoke("Memeriksa keberadaan dan ukuran file CSV...");

                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    result.IsValid = false;
                    result.Message = $"File CSV tidak ditemukan di: {filePath}";
                    return result;
                }

                var fileInfo = new FileInfo(filePath);
                result.FileSizeBytes = fileInfo.Length;
                if (result.FileSizeBytes == 0)
                {
                    result.IsValid = false;
                    result.Message = "File CSV kosong (0 byte). Ekspor SAP mungkin tidak menghasilkan data.";
                    return result;
                }

                // 1. Hitung SHA-256 Checksum dengan FileShare.ReadWrite
                statusCallback?.Invoke("Menghitung checksum integritas SHA-256...");
                byte[] fileBytes;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    using var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    fileBytes = ms.ToArray();
                }

                var hashBytes = SHA256.HashData(fileBytes);
                result.Sha256Checksum = Convert.ToHexString(hashBytes).ToLowerInvariant();

                // 2. Ekstrak tanggal laporan dari nama file (misal realisasi_20261017_0730_ZFM001.csv)
                result.ReportDate = ExtractReportDate(result.FileName);

                // 3. Baca teks dan deteksi delimiter otomatis (Tab, Semicolon, atau Comma)
                statusCallback?.Invoke("Mendeteksi delimiter dan memvalidasi kolom header ZFM001...");
                var textContent = DecodeFileText(fileBytes);
                var lines = textContent.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

                char delimiter = DetectDelimiter(lines.Take(25));
                result.DetectedDelimiter = delimiter == '\t' ? "TAB (\\t)" : delimiter.ToString();

                // 4. Parsing baris sesuai struktur SapImportService di Web Darsana
                var headerMapping = new Dictionary<string, int>();
                bool headerParsed = false;
                var buffer = new List<(string ItemCode, string ItemName, Dictionary<string, long> Values)>();
                var targetColumns = new[] { "rkap", "release_budget", "commitment", "total_consume", "available_budget" };

                foreach (var rawLine in lines)
                {
                    if (string.IsNullOrWhiteSpace(rawLine))
                        continue;

                    var row = SplitCsvLine(rawLine, delimiter);
                    if (row.Count == 0 || row.All(string.IsNullOrWhiteSpace))
                        continue;

                    if (!headerParsed)
                    {
                        for (int idx = 0; idx < row.Count; idx++)
                        {
                            var colClean = (row[idx] ?? string.Empty).Trim().ToLowerInvariant();
                            if (string.IsNullOrEmpty(colClean)) continue;

                            if (colClean.Contains("funds center") || colClean.Contains("commitment item") || colClean == "rkap")
                            {
                                headerParsed = true;
                            }

                            if (colClean.Contains("funds center") || colClean.Contains("commitment item"))
                                headerMapping["funds_center"] = idx;
                            else if (colClean == "rkap")
                                headerMapping["rkap"] = idx;
                            else if ((colClean.Contains("release budget") || colClean.Contains("budgetrelease") || colClean.Contains("budget release")) && !colClean.Contains("not release"))
                                headerMapping["release_budget"] = idx;
                            else if (colClean == "commitment")
                                headerMapping["commitment"] = idx;
                            else if ((colClean.Contains("total consume") || colClean.Contains("total cosume")) && !colClean.Contains("%"))
                                headerMapping["total_consume"] = idx;
                            else if (colClean.Contains("available budget") || colClean.Contains("availablebudget"))
                                headerMapping["available_budget"] = idx;
                        }
                        continue;
                    }

                    if (!headerMapping.TryGetValue("funds_center", out int fcIndex) || fcIndex >= row.Count)
                        continue;

                    var firstColRaw = (row[fcIndex] ?? string.Empty).Trim();
                    // Bersihkan tanda bintang di awal (* atau **)
                    firstColRaw = Regex.Replace(firstColRaw, @"^\*+\s*", "").Trim();

                    if (string.IsNullOrEmpty(firstColRaw))
                        continue;

                    // Abaikan garis pemisah seperti ----- atau =====
                    if (Regex.IsMatch(firstColRaw, @"^[\-=_\*\|+\s]+$"))
                        continue;

                    // Ambil nilai numerik 5 kolom utama
                    var values = new Dictionary<string, long>();
                    foreach (var col in targetColumns)
                    {
                        if (headerMapping.TryGetValue(col, out int cIdx) && cIdx >= 0 && cIdx < row.Count)
                        {
                            values[col] = ParseSapNumber(row[cIdx]);
                        }
                        else
                        {
                            values[col] = 0L;
                        }
                    }

                    // Cek apakah baris Grand Total ("* Funds Center/Commitment Item" atau "Funds Center")
                    if (firstColRaw.Contains("funds center", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Records.Add(new BudgetRealisasiRecord
                        {
                            ReportDate = result.ReportDate,
                            BranchCode = null,
                            ItemCode = null,
                            Level = "total",
                            BranchName = null,
                            ItemName = "Grand Total",
                            Rkap = values["rkap"],
                            ReleaseBudget = values["release_budget"],
                            Commitment = values["commitment"],
                            TotalConsume = values["total_consume"],
                            AvailableBudget = values["available_budget"]
                        });
                        result.RowsImported++;
                        continue;
                    }

                    // Pisahkan kode dan nama (misal "51010101  GAJI POKOK" atau "A022020000  PERUM LPPNPI")
                    string code = firstColRaw;
                    string name = string.Empty;

                    int firstSpace = firstColRaw.IndexOf(' ');
                    if (firstSpace > 0)
                    {
                        code = firstColRaw[..firstSpace].Trim();
                        name = firstColRaw[(firstSpace + 1)..].Trim();
                    }

                    // Cek apakah baris Item Pos Anggaran (6 s/d 12 digit angka, misal 8 digit 51010101 atau 10 digit 5102020100)
                    if (Regex.IsMatch(code, @"^\d{6,12}$"))
                    {
                        buffer.Add((code, name, values));
                    }
                    // Cek apakah baris Summary Cabang / Funds Center (diawali huruf A diikuti angka, misal A022020000)
                    else if (Regex.IsMatch(code, @"^A\d+", RegexOptions.IgnoreCase))
                    {
                        string branchCode = code.ToUpperInvariant();
                        string branchName = name;

                        foreach (var item in buffer)
                        {
                            result.Records.Add(new BudgetRealisasiRecord
                            {
                                ReportDate = result.ReportDate,
                                BranchCode = branchCode,
                                ItemCode = item.ItemCode,
                                Level = "item",
                                BranchName = branchName,
                                ItemName = item.ItemName,
                                Rkap = item.Values["rkap"],
                                ReleaseBudget = item.Values["release_budget"],
                                Commitment = item.Values["commitment"],
                                TotalConsume = item.Values["total_consume"],
                                AvailableBudget = item.Values["available_budget"]
                            });
                            result.RowsImported++;
                            result.ItemsCount++;
                        }
                        buffer.Clear();

                        result.Records.Add(new BudgetRealisasiRecord
                        {
                            ReportDate = result.ReportDate,
                            BranchCode = branchCode,
                            ItemCode = null,
                            Level = "cabang",
                            BranchName = branchName,
                            ItemName = null,
                            Rkap = values["rkap"],
                            ReleaseBudget = values["release_budget"],
                            Commitment = values["commitment"],
                            TotalConsume = values["total_consume"],
                            AvailableBudget = values["available_budget"]
                        });
                        result.RowsImported++;
                        result.BranchesCount++;
                    }
                    else
                    {
                        result.SkippedCount++;
                    }
                }

                // Jika masih ada sisa item di buffer (misal file tidak memiliki baris summary cabang A0220...), tetap masukkan
                if (buffer.Count > 0)
                {
                    foreach (var item in buffer)
                    {
                        result.Records.Add(new BudgetRealisasiRecord
                        {
                            ReportDate = result.ReportDate,
                            BranchCode = "A022020000",
                            ItemCode = item.ItemCode,
                            Level = "item",
                            BranchName = "PERUM LPPNPI",
                            ItemName = item.ItemName,
                            Rkap = item.Values["rkap"],
                            ReleaseBudget = item.Values["release_budget"],
                            Commitment = item.Values["commitment"],
                            TotalConsume = item.Values["total_consume"],
                            AvailableBudget = item.Values["available_budget"]
                        });
                        result.RowsImported++;
                        result.ItemsCount++;
                    }
                    buffer.Clear();
                }

                if (!headerParsed || !headerMapping.ContainsKey("funds_center"))
                {
                    result.IsValid = false;
                    result.Message = "Format CSV Tidak Sesuai: Header wajib 'Funds Center/Commitment Item' dan 'RKAP' tidak ditemukan.";
                    return result;
                }

                if (result.RowsImported == 0)
                {
                    result.IsValid = false;
                    result.Message = "Format CSV memiliki header, namun jumlah baris data anggaran yang valid adalah 0 baris.";
                    return result;
                }

                result.IsValid = true;
                result.Message = $"Validasi CSV OK ({result.RowsImported} baris valid: {result.BranchesCount} cabang, {result.ItemsCount} pos anggaran | SHA-256: {result.Sha256Checksum[..12]}...)";
                statusCallback?.Invoke(result.Message);
                return result;
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.Message = $"Gagal memvalidasi CSV: {ex.Message}";
                return result;
            }
        });
    }

    private static string DecodeFileText(byte[] bytes)
    {
        // Cek UTF-8 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        // Cek UTF-16 LE BOM (kadang dihasilkan SAP GUI Spreadsheet export)
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static char DetectDelimiter(IEnumerable<string> sampleLines)
    {
        var sample = string.Join("\n", sampleLines);
        int tabCount = sample.Count(c => c == '\t');
        int semiCount = sample.Count(c => c == ';');
        int commaCount = sample.Count(c => c == ',');

        if (tabCount >= semiCount && tabCount >= commaCount && tabCount > 0)
            return '\t';
        if (semiCount >= commaCount && semiCount > 0)
            return ';';
        return ',';
    }

    private static List<string> SplitCsvLine(string line, char delimiter)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delimiter && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result;
    }

    private static long ParseSapNumber(string? rawVal)
    {
        if (string.IsNullOrWhiteSpace(rawVal))
            return 0L;

        string s = rawVal.Trim().Replace("\"", "").Replace(" ", "");
        if (string.IsNullOrEmpty(s))
            return 0L;

        // Tangani tanda minus di belakang khas SAP (contoh: "125.000-" -> "-125.000")
        bool isNegative = false;
        if (s.EndsWith('-'))
        {
            isNegative = true;
            s = s.TrimEnd('-');
        }
        else if (s.StartsWith('-'))
        {
            isNegative = true;
            s = s.TrimStart('-');
        }

        // Jika mengandung titik dan koma, atau titik sebagai pemisah ribuan Indonesia (misal 1.250.000.000 atau 1.250.000,00)
        if (s.Contains('.') && s.Contains(','))
        {
            // Cek mana yang muncul belakangan
            int lastDot = s.LastIndexOf('.');
            int lastComma = s.LastIndexOf(',');
            if (lastComma > lastDot)
            {
                // Format Indonesia: 1.234.567,89
                s = s.Replace(".", "").Replace(",", ".");
            }
            else
            {
                // Format US: 1,234,567.89
                s = s.Replace(",", "");
            }
        }
        else if (s.Count(c => c == '.') > 1)
        {
            // Lebih dari 1 titik -> pasti pemisah ribuan (contoh: 500.000.000)
            s = s.Replace(".", "");
        }
        else if (s.Contains('.'))
        {
            // Satu titik: jika ada 3 digit di belakang titik pada angka anggaran rupiah, itu pemisah ribuan
            var parts = s.Split('.');
            if (parts.Length == 2 && parts[1].Length == 3)
            {
                s = s.Replace(".", "");
            }
        }
        else if (s.Contains(','))
        {
            var parts = s.Split(',');
            if (parts.Length == 2 && parts[1].Length <= 2)
            {
                s = s.Replace(",", ".");
            }
            else
            {
                s = s.Replace(",", "");
            }
        }

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
        {
            long rounded = (long)Math.Round(val);
            return isNegative ? -rounded : rounded;
        }

        return 0L;
    }

    private static string ExtractReportDate(string fileName)
    {
        var matchIso = Regex.Match(fileName, @"(\d{4}-\d{2}-\d{2})");
        if (matchIso.Success)
            return matchIso.Groups[1].Value;

        var matchCompact = Regex.Match(fileName, @"(\d{4})(\d{2})(\d{2})");
        if (matchCompact.Success)
            return $"{matchCompact.Groups[1].Value}-{matchCompact.Groups[2].Value}-{matchCompact.Groups[3].Value}";

        return DateTime.Now.ToString("yyyy-MM-dd");
    }
}

