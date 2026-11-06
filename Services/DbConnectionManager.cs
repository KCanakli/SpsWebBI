using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using SpsWebBI.Security;

namespace SpsWebBI.Services
{
    public class DbConnectionInfoDto
    {
        public string Server { get; set; } = "127.0.0.1";
        public uint Port { get; set; } = 3323;
        public string Database { get; set; } = "spsteks";
        public string UserId { get; set; } = "root";
        public string Password { get; set; } = string.Empty;
        public bool HasSavedPassword { get; set; }
    }

    /// <summary>
    /// MariaDB bağlantı bilgilerini çalışma zamanında okuyan, test eden ve şifreyi DPAPI (ENC:...)
    /// ile şifreleyerek appsettings.json dosyasına güvenli biçimde kaydeden servis.
    /// </summary>
    public class DbConnectionManager
    {
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _env;
        private readonly object _fileLock = new();

        public DbConnectionManager(IConfiguration configuration, IWebHostEnvironment env)
        {
            _configuration = configuration;
            _env = env;
            EnsurePasswordEncryptedOnDisk();
        }

        private string GetAppSettingsPath()
        {
            return Path.Combine(_env.ContentRootPath, "appsettings.json");
        }

        /// <summary>
        /// appsettings.json içindeki parola açık metin olarak yazılmışsa ilk açılışta otomatik olarak DPAPI (ENC:...) formatına çevirir.
        /// </summary>
        public void EnsurePasswordEncryptedOnDisk()
        {
            try
            {
                string raw = _configuration.GetConnectionString("MariaDbConnection") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(raw)) return;

                var builder = new MySqlConnectionStringBuilder(raw);
                if (!string.IsNullOrEmpty(builder.Password) && !builder.Password.StartsWith("ENC:", StringComparison.Ordinal))
                {
                    string protectedConnStr = ConnectionProtector.ProtectConnectionStringPassword(raw);
                    WriteConnectionStringToFile(protectedConnStr);
                }
            }
            catch
            {
                // Dosya erişim hatası olursa çalışma zamanını kesme
            }
        }

        public string GetActiveConnectionString()
        {
            string raw = _configuration.GetConnectionString("MariaDbConnection") ?? string.Empty;
            return ConnectionProtector.GetUsableConnectionString(raw);
        }

        public DbConnectionInfoDto GetCurrentSettings()
        {
            string raw = _configuration.GetConnectionString("MariaDbConnection") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
                return new DbConnectionInfoDto();

            var builder = new MySqlConnectionStringBuilder(raw);
            string decryptedPwd = ConnectionProtector.DecryptSecret(builder.Password);

            return new DbConnectionInfoDto
            {
                Server = string.IsNullOrWhiteSpace(builder.Server) ? "127.0.0.1" : builder.Server,
                Port = builder.Port == 0 ? 3323u : builder.Port,
                Database = string.IsNullOrWhiteSpace(builder.Database) ? "spsteks" : builder.Database,
                UserId = string.IsNullOrWhiteSpace(builder.UserID) ? "root" : builder.UserID,
                Password = string.Empty, // Güvenlik gereği arayüze mevcut şifre açık gönderilmez
                HasSavedPassword = !string.IsNullOrEmpty(decryptedPwd)
            };
        }

        public string BuildConnectionString(DbConnectionInfoDto dto)
        {
            string passwordToUse = dto.Password;
            if (string.IsNullOrEmpty(passwordToUse))
            {
                // Kullanıcı şifre alanını boş bıraktıysa mevcut kayıtlı şifreyi koru
                string raw = _configuration.GetConnectionString("MariaDbConnection") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var currentBuilder = new MySqlConnectionStringBuilder(raw);
                    passwordToUse = ConnectionProtector.DecryptSecret(currentBuilder.Password);
                }
            }

            var builder = new MySqlConnectionStringBuilder
            {
                Server = dto.Server.Trim(),
                Port = dto.Port,
                Database = dto.Database.Trim(),
                UserID = dto.UserId.Trim(),
                Password = passwordToUse,
                AllowUserVariables = true,
                AllowLoadLocalInfile = false,
                ConnectionTimeout = 8
            };

            return builder.ConnectionString;
        }

        public async Task<(bool Success, string Message)> TestConnectionAsync(DbConnectionInfoDto dto)
        {
            try
            {
                string connStr = BuildConnectionString(dto);
                await using var conn = new MySqlConnection(connStr);
                await conn.OpenAsync();
                string serverVersion = conn.ServerVersion;
                return (true, $"Bağlantı başarılı! (MariaDB Sürümü: {serverVersion}, Veritabanı: {conn.Database})");
            }
            catch (Exception ex)
            {
                return (false, $"Bağlantı kurulamadı: {ex.Message}");
            }
        }

        public async Task<(bool Success, string Message)> SaveConnectionSettingsAsync(DbConnectionInfoDto dto)
        {
            var testResult = await TestConnectionAsync(dto);
            if (!testResult.Success)
            {
                return testResult;
            }

            try
            {
                string usableConnStr = BuildConnectionString(dto);
                string protectedConnStr = ConnectionProtector.ProtectConnectionStringPassword(usableConnStr);
                WriteConnectionStringToFile(protectedConnStr);

                // Bellekteki IConfiguration değerini de anında güncelle
                _configuration["ConnectionStrings:MariaDbConnection"] = protectedConnStr;

                return (true, "Veritabanı bağlantı ayarları şifrelenerek kaydedildi.");
            }
            catch (Exception ex)
            {
                return (false, $"Ayarlar kaydedilirken hata oluştu: {ex.Message}");
            }
        }

        private void WriteConnectionStringToFile(string protectedConnectionString)
        {
            lock (_fileLock)
            {
                string path = GetAppSettingsPath();
                JsonNode? rootNode = null;

                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    rootNode = JsonNode.Parse(json);
                }

                rootNode ??= new JsonObject();
                if (rootNode["ConnectionStrings"] is not JsonObject connStringsObj)
                {
                    connStringsObj = new JsonObject();
                    rootNode["ConnectionStrings"] = connStringsObj;
                }

                connStringsObj["MariaDbConnection"] = protectedConnectionString;

                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(path, rootNode.ToJsonString(options));
            }
        }
    }
}
