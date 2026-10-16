# Panduan Uji Coba Otomasi SAP (Tahap 2) di Komputer Server

Paket ini disiapkan agar dapat langsung dijalankan pada komputer server yang telah terpasang **SAP GUI** tanpa memerlukan instalasi .NET SDK tambahan (*Self-Contained*).

---

## 1. Persiapan di Komputer Server
1. **Ekstrak File:**
   - Salin file `Atlaz_Server_Portable_v1.0.zip` dari folder `installer/` ke komputer server.
   - Ekstrak file zip ke direktori pilihan, misalnya: `C:\Atlaz\`.
2. **Prasyarat SAP GUI Scripting:**
   - Pastikan **SAP GUI Scripting** sudah diaktifkan di komputer server:
     1. Buka SAP Logon -> Klik icon menu di pojok kiri atas -> **Options**.
     2. Masuk ke **Accessibility & Scripting** -> **Scripting**.
     3. Centang opsi **"Enable scripting"**.
     4. (Opsional) Hilangkan centang pada *"Notify when a script attaches to SAP GUI"* dan *"Notify when a script opens a connection"* agar proses berjalan otomatis tanpa dialog konfirmasi.
   - Di sisi server SAP, parameter profil `sapgui/user_scripting` harus bernilai `TRUE` (biasanya sudah aktif untuk transaksi batch bot).

---

## 2. Menjalankan Aplikasi
1. Buka folder hasil ekstrak `C:\Atlaz\`.
2. Klik ganda pada **`Atlaz.UI.exe`**.
3. Aplikasi akan langsung terbuka.

---

## 3. Langkah Pengujian Tahap 2 (Otomasi SAP)
1. Masuk ke tab **⚙️ Konfigurasi Sistem**:
   - **System ID / Description:** Masukkan nama koneksi sistem SAP persis seperti yang tertulis di SAP Logon server (contoh: `PRD`, `ECC Production`, dll).
   - **Client:** Masukkan nomor client (contoh: `100`, `200`, `300`).
   - **Username SAP:** Username untuk login.
   - **Password SAP:** Password akun SAP.
   - **Tahun Anggaran & Periode:** Masukkan tahun (contoh: `2026`) dan periode (contoh: `10`).
   - Klik **Simpan Konfigurasi**.
2. Masuk ke tab **📊 Dashboard**:
   - Klik tombol **"▶ Jalankan Sinkronisasi Sekarang (Manual Run)"**.
3. **Observasi Alur Tahap 2:**
   - Aplikasi akan mendeteksi proses SAP Logon atau membukanya otomatis.
   - Login otomatis dieksekusi.
   - Transaksi `/nZFM001` dibuka, parameter tahun dan periode diisi, lalu dieksekusi (`F8`).
   - Laporan diekspor ke file CSV lokal di:
     `%LOCALAPPDATA%\Atlaz\exports\ZFM001_YYYYMMDD_HHMMSS.csv`
   - Sesi SAP di-logout secara bersih (`/nex`).
   - Status eksekusi dan path file CSV hasil unduhan akan tercatat di tab **📜 Riwayat & Log**.
