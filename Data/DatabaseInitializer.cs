using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using SpsWebBI.Security;
using SpsWebBI.Services;

namespace SpsWebBI.Data
{
    public class DatabaseInitializer
    {
        private readonly DbConnectionManager _connManager;
        private readonly ILogger<DatabaseInitializer> _logger;

        public DatabaseInitializer(DbConnectionManager connManager, ILogger<DatabaseInitializer> logger)
        {
            _connManager = connManager;
            _logger = logger;
        }

        public string GetConnectionString() => _connManager.GetActiveConnectionString();

        public async Task<(bool Success, string Message)> InitializeAsync()
        {
            string connStr = GetConnectionString();
            if (string.IsNullOrWhiteSpace(connStr))
                return (false, "MariaDB bağlantı dizesi tanımlı değil.");

            try
            {
                await using var connection = new MySqlConnection(connStr);
                await connection.OpenAsync();

                // 1. Kullanıcılar Tablosu
                await connection.ExecuteAsync(@"
                    CREATE TABLE IF NOT EXISTS bi_users (
                        user_id INT AUTO_INCREMENT PRIMARY KEY,
                        user_code VARCHAR(50) NOT NULL UNIQUE,
                        full_name VARCHAR(100) NOT NULL,
                        password_hash VARCHAR(255) NOT NULL,
                        role VARCHAR(20) NOT NULL DEFAULT 'viewer',
                        allow_passwordless TINYINT(1) NOT NULL DEFAULT 0,
                        is_active TINYINT(1) NOT NULL DEFAULT 1,
                        failed_login_count INT NOT NULL DEFAULT 0,
                        locked_until DATETIME NULL,
                        created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");

                // 2. Veri Kaynakları (SQL Sorguları) Tablosu
                await connection.ExecuteAsync(@"
                    CREATE TABLE IF NOT EXISTS bi_datasources (
                        datasource_id INT AUTO_INCREMENT PRIMARY KEY,
                        code VARCHAR(50) NOT NULL UNIQUE,
                        name VARCHAR(120) NOT NULL,
                        category VARCHAR(50) NULL,
                        connection_id VARCHAR(64) NULL,
                        sql_query LONGTEXT NOT NULL,
                        columns_schema_json LONGTEXT NULL,
                        is_active TINYINT(1) NOT NULL DEFAULT 1,
                        created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        updated_at DATETIME NULL
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");

                // Mevcut bi_datasources tablosunda connection_id kolonu yoksa ekle
                int hasConnCol = await connection.ExecuteScalarAsync<int>(@"
                    SELECT COUNT(*) FROM information_schema.COLUMNS
                     WHERE TABLE_SCHEMA = DATABASE()
                       AND TABLE_NAME = 'bi_datasources'
                       AND COLUMN_NAME = 'connection_id';");
                if (hasConnCol == 0)
                {
                    await connection.ExecuteAsync("ALTER TABLE bi_datasources ADD COLUMN connection_id VARCHAR(64) NULL AFTER category;");
                }

                // 3. Veri Kaynağı Filtre Parametreleri Tablosu (Madde 5)
                await connection.ExecuteAsync(@"
                    CREATE TABLE IF NOT EXISTS bi_datasource_params (
                        param_id INT AUTO_INCREMENT PRIMARY KEY,
                        datasource_id INT NOT NULL,
                        param_name VARCHAR(60) NOT NULL,
                        label VARCHAR(100) NOT NULL,
                        param_type VARCHAR(30) NOT NULL DEFAULT 'text',
                        default_value VARCHAR(255) NULL,
                        lookup_sql TEXT NULL,
                        display_order INT NOT NULL DEFAULT 1,
                        UNIQUE KEY uk_ds_param (datasource_id, param_name),
                        CONSTRAINT fk_param_ds FOREIGN KEY (datasource_id) REFERENCES bi_datasources(datasource_id) ON DELETE CASCADE
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");

                // 4. Dashboard Tanım ve Ana Layout Tablosu (Madde 9)
                await connection.ExecuteAsync(@"
                    CREATE TABLE IF NOT EXISTS bi_dashboards (
                        dashboard_id INT AUTO_INCREMENT PRIMARY KEY,
                        code VARCHAR(50) NOT NULL UNIQUE,
                        title VARCHAR(150) NOT NULL,
                        description VARCHAR(300) NULL,
                        primary_datasource_id INT NOT NULL,
                        layout_json LONGTEXT NOT NULL,
                        is_active TINYINT(1) NOT NULL DEFAULT 1,
                        created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        updated_at DATETIME NULL,
                        CONSTRAINT fk_dash_ds FOREIGN KEY (primary_datasource_id) REFERENCES bi_datasources(datasource_id) ON DELETE RESTRICT
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");

                // 5. Kullanıcı Yetkileri, Öncelik, Süreler ve Kişisel Layout Tablosu (Madde 6 & 10)
                await connection.ExecuteAsync(@"
                    CREATE TABLE IF NOT EXISTS bi_user_dashboards (
                        user_dashboard_id INT AUTO_INCREMENT PRIMARY KEY,
                        user_id INT NOT NULL,
                        dashboard_id INT NOT NULL,
                        can_view TINYINT(1) NOT NULL DEFAULT 1,
                        can_edit_layout TINYINT(1) NOT NULL DEFAULT 1,
                        display_order INT NOT NULL DEFAULT 1,
                        refresh_seconds INT NOT NULL DEFAULT 60,
                        slide_duration_seconds INT NOT NULL DEFAULT 30,
                        custom_layout_json LONGTEXT NULL,
                        UNIQUE KEY uk_user_dash (user_id, dashboard_id),
                        CONSTRAINT fk_ud_user FOREIGN KEY (user_id) REFERENCES bi_users(user_id) ON DELETE CASCADE,
                        CONSTRAINT fk_ud_dash FOREIGN KEY (dashboard_id) REFERENCES bi_dashboards(dashboard_id) ON DELETE CASCADE
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");

                // 6. Siber Güvenlik Denetim (Audit) Tablosu
                await connection.ExecuteAsync(@"
                    CREATE TABLE IF NOT EXISTS bi_audit_logs (
                        log_id BIGINT AUTO_INCREMENT PRIMARY KEY,
                        user_code VARCHAR(50) NULL,
                        action_type VARCHAR(50) NOT NULL,
                        ip_address VARCHAR(60) NULL,
                        details TEXT NULL,
                        created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");

                // Varsayılan kullanıcıları (admin ve A kullanıcısı) yoksa oluştur
                int userCount = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM bi_users;");
                if (userCount == 0)
                {
                    await connection.ExecuteAsync(@"
                        INSERT INTO bi_users (user_code, full_name, password_hash, role, allow_passwordless, is_active)
                        VALUES 
                        ('admin', 'Sistem Yöneticisi', @AdminHash, 'admin', 0, 1),
                        ('A', 'A Kullanıcısı (TV / Operasyon)', @UserAHash, 'viewer', 1, 1);",
                        new
                        {
                            AdminHash = PasswordHasher.HashPassword("Admin123!"),
                            UserAHash = PasswordHasher.HashPassword("1234")
                        });
                }

                // Otomatik örnek veri tohumlama (Auto-Seed) kullanıcı isteğiyle devre dışı bırakıldı.
                // Kullanıcı kendi veri kaynaklarını ve dashboard tasarımlarını /designer ekranından sıfırdan oluşturacaktır.

                _logger.LogInformation("MariaDB SpsWebBI tabloları başarıyla doğrulandı/oluşturuldu.");
                return (true, "MariaDB bağlantısı ve BI tabloları hazır.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("MariaDB başlatma uyarısı: {Message}", ex.Message);
                return (false, ex.Message);
            }
        }

        private static async Task SeedInitialDataSourcesAndDashboardsAsync(MySqlConnection connection)
        {
            const string siparisSql = @"SELECT s.Siparis_ID, s.Siparis_Turu, s.siparis_tarihi,
       YEAR(s.siparis_tarihi) AS siparis_yil, MONTH(s.siparis_tarihi) AS siparis_ay,
       s.termin_tarihi, COALESCE(c.Cari, 'Tanımsız Cari') AS Cari,
       COALESCE(ca.Ulke, 'Belirsiz') AS Ulke, COALESCE(ca.Il, 'Belirsiz') AS Il,
       st.Stok_Tipi, st.stok_kodu, st.Stok_Adi,
       COALESCE(sp.Kumas_Turu, 'Diğer') AS Kumas_Turu,
       COALESCE(sp.Siparis_Mt, 0) AS Siparis_Mt,
       COALESCE(sp.siparis_kg, 0) AS siparis_kg,
       COALESCE(sp.siparis_miktar, 0) AS siparis_miktar,
       COALESCE(sp.Birim_Fiyati, 0) AS Birim_Fiyati,
       ROUND(COALESCE(sp.siparis_miktar, 0) * COALESCE(sp.Birim_Fiyati, 0), 2) AS Toplam_Tutar
  FROM siparis s
  LEFT JOIN cari c ON c.cari_id = s.cari_id
  LEFT JOIN cari_adres ca ON ca.cari_adres_id = s.Cari_Adres_ID
  LEFT JOIN siparis_parti sp ON sp.siparis_id = s.Siparis_ID
  LEFT JOIN stok st ON st.stok_id = sp.Stok_ID
 WHERE (@Ulke IS NULL OR ca.Ulke = @Ulke)
   AND (@SiparisTuru IS NULL OR s.Siparis_Turu = @SiparisTuru)
 ORDER BY s.siparis_tarihi DESC";

            const string stokSql = @"SELECT COALESCE(d.Depo, 'Tanımsız Depo') AS Depo,
       COALESCE(sd.Kumas_Turu, 'Diğer') AS Kumas_Turu,
       COALESCE(sd.Top_durum, 'Belirsiz') AS Top_durum,
       sd.Desen_no, sd.Varyant_no, sd.Varyant_Adi,
       COALESCE(sd.Brut_Kg, 0) AS Brut_Kg,
       COALESCE(sd.Kg, 0) AS Kg,
       COALESCE(sd.Mt, 0) AS Mt,
       COALESCE(sd.Top, 0) AS Top
  FROM w_stok_durum sd
  LEFT JOIN depo d ON d.Depo_ID = sd.Depo_ID
 WHERE (@Depo IS NULL OR d.Depo = @Depo)
   AND (@KumasTuru IS NULL OR sd.Kumas_Turu = @KumasTuru)";

            int dsSiparisId = await connection.ExecuteScalarAsync<int>(@"
                INSERT INTO bi_datasources (code, name, category, sql_query, is_active)
                VALUES ('DS_SIPARIS', 'Satış & Sipariş Verileri', 'Satış', @Sql, 1);
                SELECT LAST_INSERT_ID();", new { Sql = siparisSql });

            int dsStokId = await connection.ExecuteScalarAsync<int>(@"
                INSERT INTO bi_datasources (code, name, category, sql_query, is_active)
                VALUES ('DS_STOK', 'Depo & Stok Durum Verileri', 'Stok', @Sql, 1);
                SELECT LAST_INSERT_ID();", new { Sql = stokSql });

            // Sipariş Filtre Parametreleri (Madde 5)
            await connection.ExecuteAsync(@"
                INSERT INTO bi_datasource_params (datasource_id, param_name, label, param_type, default_value, lookup_sql, display_order)
                VALUES 
                (@DsSiparis, 'Ulke', 'Ülke', 'select', NULL, 'SELECT DISTINCT Ulke AS val, Ulke AS txt FROM cari_adres WHERE Ulke IS NOT NULL AND Ulke <> '''' ORDER BY Ulke', 1),
                (@DsSiparis, 'SiparisTuru', 'Sipariş Türü', 'select', NULL, 'SELECT DISTINCT Siparis_Turu AS val, Siparis_Turu AS txt FROM siparis WHERE Siparis_Turu IS NOT NULL AND Siparis_Turu <> '''' ORDER BY Siparis_Turu', 2),
                (@DsStok, 'Depo', 'Depo', 'select', NULL, 'SELECT DISTINCT Depo AS val, Depo AS txt FROM depo WHERE Depo IS NOT NULL AND Depo <> '''' ORDER BY Depo', 1),
                (@DsStok, 'KumasTuru', 'Kumaş Türü', 'select', NULL, 'SELECT DISTINCT Kumas_Turu AS val, Kumas_Turu AS txt FROM w_stok_durum WHERE Kumas_Turu IS NOT NULL AND Kumas_Turu <> '''' ORDER BY Kumas_Turu', 2);",
                new { DsSiparis = dsSiparisId, DsStok = dsStokId });

            // Varsayılan Dashboard Layout Tanımları (Madde 9)
            const string siparisLayoutJson = @"{
  ""widgets"": [
    { ""id"": ""kpi_tutar"", ""type"": ""kpi"", ""title"": ""Toplam Sipariş Tutarı"", ""valueField"": ""Toplam_Tutar"", ""agg"": ""sum"", ""x"": 0, ""y"": 0, ""w"": 3, ""h"": 2 },
    { ""id"": ""kpi_miktar"", ""type"": ""kpi"", ""title"": ""Toplam Miktar (Mt)"", ""valueField"": ""siparis_miktar"", ""agg"": ""sum"", ""x"": 3, ""y"": 0, ""w"": 3, ""h"": 2 },
    { ""id"": ""kpi_kg"", ""type"": ""kpi"", ""title"": ""Toplam Kg"", ""valueField"": ""siparis_kg"", ""agg"": ""sum"", ""x"": 6, ""y"": 0, ""w"": 3, ""h"": 2 },
    { ""id"": ""kpi_adet"", ""type"": ""kpi"", ""title"": ""Sipariş Kalem Sayısı"", ""valueField"": ""Siparis_ID"", ""agg"": ""count"", ""x"": 9, ""y"": 0, ""w"": 3, ""h"": 2 },
    { ""id"": ""bar_cari"", ""type"": ""bar"", ""title"": ""Müşteri Bazlı Satışlar (İlk 10)"", ""dimension"": ""Cari"", ""drillHierarchy"": [""Cari"", ""Kumas_Turu"", ""Stok_Adi""], ""valueField"": ""Toplam_Tutar"", ""agg"": ""sum"", ""topN"": 10, ""x"": 0, ""y"": 2, ""w"": 5, ""h"": 5 },
    { ""id"": ""bar_ulke"", ""type"": ""bar"", ""title"": ""Ülke / İl Bazlı Sipariş Miktarı"", ""dimension"": ""Ulke"", ""drillHierarchy"": [""Ulke"", ""Il"", ""Cari""], ""valueField"": ""siparis_miktar"", ""agg"": ""sum"", ""x"": 5, ""y"": 2, ""w"": 4, ""h"": 5 },
    { ""id"": ""pie_kumas"", ""type"": ""pie"", ""title"": ""Kumaş Türü Dağılımı"", ""dimension"": ""Kumas_Turu"", ""drillHierarchy"": [""Kumas_Turu"", ""Stok_Adi""], ""valueField"": ""siparis_miktar"", ""agg"": ""sum"", ""x"": 9, ""y"": 2, ""w"": 3, ""h"": 5 },
    { ""id"": ""grid_detay"", ""type"": ""table"", ""title"": ""Sipariş Detay Tablosu"", ""columns"": [""Siparis_ID"", ""siparis_tarihi"", ""Ulke"", ""Il"", ""Siparis_Turu"", ""Cari"", ""Kumas_Turu"", ""Stok_Adi"", ""siparis_miktar"", ""Birim_Fiyati"", ""Toplam_Tutar""], ""x"": 0, ""y"": 7, ""w"": 12, ""h"": 5 }
  ]
}";

            const string stokLayoutJson = @"{
  ""widgets"": [
    { ""id"": ""kpi_netkg"", ""type"": ""kpi"", ""title"": ""Toplam Net Kg"", ""valueField"": ""Kg"", ""agg"": ""sum"", ""x"": 0, ""y"": 0, ""w"": 3, ""h"": 2 },
    { ""id"": ""kpi_brutkg"", ""type"": ""kpi"", ""title"": ""Toplam Brüt Kg"", ""valueField"": ""Brut_Kg"", ""agg"": ""sum"", ""x"": 3, ""y"": 0, ""w"": 3, ""h"": 2 },
    { ""id"": ""kpi_mt"", ""type"": ""kpi"", ""title"": ""Toplam Metre"", ""valueField"": ""Mt"", ""agg"": ""sum"", ""x"": 6, ""y"": 0, ""w"": 3, ""h"": 2 },
    { ""id"": ""kpi_top"", ""type"": ""kpi"", ""title"": ""Toplam Top Sayısı"", ""valueField"": ""Top"", ""agg"": ""sum"", ""x"": 9, ""y"": 0, ""w"": 3, ""h"": 2 },
    { ""id"": ""bar_depo"", ""type"": ""bar"", ""title"": ""Depo Bazlı Stok (Net Kg)"", ""dimension"": ""Depo"", ""drillHierarchy"": [""Depo"", ""Kumas_Turu"", ""Varyant_Adi""], ""valueField"": ""Kg"", ""agg"": ""sum"", ""x"": 0, ""y"": 2, ""w"": 5, ""h"": 5 },
    { ""id"": ""pie_topdurum"", ""type"": ""pie"", ""title"": ""Top Durumu Dağılımı"", ""dimension"": ""Top_durum"", ""drillHierarchy"": [""Top_durum"", ""Depo"", ""Kumas_Turu""], ""valueField"": ""Kg"", ""agg"": ""sum"", ""x"": 5, ""y"": 2, ""w"": 3, ""h"": 5 },
    { ""id"": ""pie_stokkumas"", ""type"": ""pie"", ""title"": ""Kumaş Türü Stok Payı (Mt)"", ""dimension"": ""Kumas_Turu"", ""drillHierarchy"": [""Kumas_Turu"", ""Varyant_Adi""], ""valueField"": ""Mt"", ""agg"": ""sum"", ""x"": 8, ""y"": 2, ""w"": 4, ""h"": 5 },
    { ""id"": ""grid_stok"", ""type"": ""table"", ""title"": ""Depo & Stok Detay Tablosu"", ""columns"": [""Depo"", ""Kumas_Turu"", ""Top_durum"", ""Desen_no"", ""Varyant_no"", ""Varyant_Adi"", ""Brut_Kg"", ""Kg"", ""Mt"", ""Top""], ""x"": 0, ""y"": 7, ""w"": 12, ""h"": 5 }
  ]
}";

            int dashSiparisId = await connection.ExecuteScalarAsync<int>(@"
                INSERT INTO bi_dashboards (code, title, description, primary_datasource_id, layout_json, is_active)
                VALUES ('DASH_SIPARIS', 'Satış & Sipariş Analiz Dashboard', 'Sipariş, müşteri, ülke ve kumaş kırılımlı satış analizi', @DsId, @Layout, 1);
                SELECT LAST_INSERT_ID();", new { DsId = dsSiparisId, Layout = siparisLayoutJson });

            int dashStokId = await connection.ExecuteScalarAsync<int>(@"
                INSERT INTO bi_dashboards (code, title, description, primary_datasource_id, layout_json, is_active)
                VALUES ('DASH_STOK', 'Depo & Stok Durum Dashboard', 'Depo, kumaş türü, top durumu ve varyant bazlı stok analizi', @DsId, @Layout, 1);
                SELECT LAST_INSERT_ID();", new { DsId = dsStokId, Layout = stokLayoutJson });

            // Kullanıcı yetkileri (Madde 6: Gösterim önceliği, refresh süresi ve ekranda kalma süresi)
            var users = await connection.QueryAsync<int>("SELECT user_id FROM bi_users;");
            foreach (int uid in users)
            {
                await connection.ExecuteAsync(@"
                    INSERT IGNORE INTO bi_user_dashboards (user_id, dashboard_id, can_view, can_edit_layout, display_order, refresh_seconds, slide_duration_seconds)
                    VALUES 
                    (@Uid, @Dash1, 1, 1, 1, 60, 20),
                    (@Uid, @Dash2, 1, 1, 2, 60, 20);",
                    new { Uid = uid, Dash1 = dashSiparisId, Dash2 = dashStokId });
            }
        }
    }
}
