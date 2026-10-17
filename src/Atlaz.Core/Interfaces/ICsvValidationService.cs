using Atlaz.Core.Models;

namespace Atlaz.Core.Interfaces;

public interface ICsvValidationService
{
    /// <summary>
    /// Memvalidasi struktur file CSV hasil ekspor SAP ZFM001, menghitung SHA-256,
    /// dan mengekstrak baris-baris data siap-insert untuk tabel budget_realisasi Web Darsana.
    /// </summary>
    Task<CsvValidationResult> ValidateAndParseZfm001CsvAsync(
        string filePath,
        Action<string>? statusCallback = null);
}

