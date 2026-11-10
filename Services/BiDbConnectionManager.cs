using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using SpsWebBI.Security;

namespace SpsWebBI.Services
{
    /// <summary>
    /// BI sistem tablolarının (bi_users, bi_dashboards, bi_datasources vb.) tutulacağı
    /// veritabanı bağlantı profillerini 'appsettings.bi.json' üzerinden DbConnectionManager ile
    /// birebir aynı formatta (çoklu bağlantı, test, save, delete) yöneten servis.
    /// </summary>
    public class BiDbConnectionManager
    {
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _env;
        private readonly DbConnectionManager _dataConnManager;
        private readonly object _fileLock = new();

        public BiDbConnectionManager(
            IConfiguration configuration,
            IWebHostEnvironment env,
            DbConnectionManager dataConnManager)
        {
            _configuration = configuration;
            _env = env;
            _dataConnManager = dataConnManager;
            EnsureBiConfigExists();
        }

        private string GetBiConfigPath() => Path.Combine(_env.ContentRootPath, "appsettings.bi.json");

        private void EnsureBiConfigExists()
        {
            try
            {
                string path = GetBiConfigPath();
                if (!File.Exists(path))
                {
                    // Dosya yoksa boş şablon oluştur, otomatik bağlantı kopyalama yapma
                    File.WriteAllText(path, "{\n  \"ConnectionStrings\": {\n    \"MariaDbConnection\": \"\"\n  },\n  \"DatabaseConnections\": []\n}");
                }
            }
            catch { }
        }

        private List<(DbConnectionInfoDto Dto, string RawConnectionString)> ReadConnectionsFromDisk()
        {
            var result = new List<(DbConnectionInfoDto Dto, string RawConnectionString)>();
            lock (_fileLock)
            {
                string path = GetBiConfigPath();
                if (!File.Exists(path)) return result;

                try
                {
                    string json = File.ReadAllText(path);
                    var rootNode = JsonNode.Parse(json);
                    if (rootNode?["DatabaseConnections"] is JsonArray arr)
                    {
                        foreach (var node in arr)
                        {
                            if (node is JsonObject obj)
                            {
                                string id = obj["Id"]?.GetValue<string>() ?? string.Empty;
                                string envName = obj["EnvironmentName"]?.GetValue<string>() ?? "BI Ortam";
                                string desc = obj["Description"]?.GetValue<string>() ?? string.Empty;
                                bool isActive = obj["IsActive"]?.GetValue<bool>() ?? true;
                                string rawConn = obj["ConnectionString"]?.GetValue<string>() ?? string.Empty;

                                var dto = ParseConnectionNode(id, envName, desc, isActive, rawConn);
                                result.Add((dto, rawConn));
                            }
                        }
                    }
                }
                catch { }
            }
            return result;
        }

        private static DbConnectionInfoDto ParseConnectionNode(
            string id,
            string envName,
            string description,
            bool isActive,
            string rawConnStr)
        {
            if (string.IsNullOrWhiteSpace(rawConnStr))
            {
                return new DbConnectionInfoDto
                {
                    Id = id,
                    EnvironmentName = envName,
                    Description = description,
                    IsActive = isActive
                };
            }

            try
            {
                var builder = new MySqlConnectionStringBuilder(rawConnStr);
                return new DbConnectionInfoDto
                {
                    Id = id,
                    EnvironmentName = envName,
                    Description = description,
                    IsActive = isActive,
                    Server = builder.Server,
                    Port = builder.Port,
                    Database = builder.Database,
                    UserId = builder.UserID,
                    HasSavedPassword = !string.IsNullOrEmpty(builder.Password)
                };
            }
            catch
            {
                return new DbConnectionInfoDto
                {
                    Id = id,
                    EnvironmentName = envName,
                    Description = description,
                    IsActive = isActive
                };
            }
        }

        public List<DbConnectionInfoDto> GetAllConnections()
        {
            var entries = ReadConnectionsFromDisk();
            return entries.Select(x => x.Dto).ToList();
        }

        public string GetBiConnectionString()
        {
            var entries = ReadConnectionsFromDisk();
            var activeEntry = entries.FirstOrDefault(x => x.Dto.IsActive);
            if (!string.IsNullOrWhiteSpace(activeEntry.RawConnectionString))
            {
                return ConnectionProtector.GetUsableConnectionString(activeEntry.RawConnectionString);
            }

            if (entries.Count > 0 && !string.IsNullOrWhiteSpace(entries[0].RawConnectionString))
            {
                return ConnectionProtector.GetUsableConnectionString(entries[0].RawConnectionString);
            }

            return _dataConnManager.GetActiveConnectionString();
        }

        public DbConnectionInfoDto GetCurrentSettings()
        {
            var all = GetAllConnections();
            return all.FirstOrDefault(x => x.IsActive) ?? all.FirstOrDefault() ?? new DbConnectionInfoDto
            {
                EnvironmentName = "BI Canlı",
                Description = "BI Sistem Veritabanı"
            };
        }

        public async Task<bool> HasWorkingAdminUserAsync()
        {
            string connStr = GetBiConnectionString();
            if (string.IsNullOrWhiteSpace(connStr)) return false;

            try
            {
                await using var conn = new MySqlConnection(connStr);
                await conn.OpenAsync();

                // bi_users tablosu var mı ve en az bir admin kullanıcısı mevcut mu?
                int userCount = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, @"
                    SELECT COUNT(*) FROM information_schema.TABLES
                     WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bi_users';");

                if (userCount == 0) return false;

                int adminCount = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, @"
                    SELECT COUNT(*) FROM bi_users WHERE role = 'admin' AND is_active = 1;");

                return adminCount > 0;
            }
            catch
            {
                return false;
            }
        }

        public async Task<(bool Success, string Message)> TestBiConnectionAsync(DbConnectionInfoDto dto)
        {
            try
            {
                string connStr = BuildConnectionString(dto);
                await using var conn = new MySqlConnection(connStr);
                await conn.OpenAsync();

                string ver = conn.ServerVersion;
                string db = conn.Database;
                return (true, $"[{dto.EnvironmentName}] Bağlantı başarılı! (MariaDB Sürümü: {ver}, Veritabanı: {db})");
            }
            catch (Exception ex)
            {
                return (false, $"Bağlantı hatası: {ex.Message}");
            }
        }

        public async Task<(bool Success, string Message, string SavedId)> SaveBiConnectionSettingsAsync(DbConnectionInfoDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Server) || dto.Port == 0 ||
                string.IsNullOrWhiteSpace(dto.Database) || string.IsNullOrWhiteSpace(dto.UserId))
            {
                return (false, "Sunucu, port, veritabanı adı ve kullanıcı adı zorunludur.", string.Empty);
            }

            if (string.IsNullOrWhiteSpace(dto.EnvironmentName))
            {
                dto.EnvironmentName = "BI Ortam";
            }

            var test = await TestBiConnectionAsync(dto);
            if (!test.Success)
            {
                return (false, $"Bağlantı doğrulanamadı: {test.Message}", string.Empty);
            }

            try
            {
                string usableConnStr = BuildConnectionString(dto);
                string protectedConnStr = ConnectionProtector.ProtectConnectionStringPassword(usableConnStr);

                string savedId = WriteConnectionToDisk(dto, protectedConnStr, setAsActiveMariaDb: dto.IsActive);

                string stateText = dto.IsActive ? "Aktif" : "Pasif";
                return (true, $"'{dto.EnvironmentName.Trim()}' ({stateText}) BI bağlantı ayarları şifrelenerek kaydedildi.", savedId);
            }
            catch (Exception ex)
            {
                return (false, $"BI ayarları kaydedilirken hata oluştu: {ex.Message}", string.Empty);
            }
        }

        public (bool Success, string Message) DeleteConnection(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return (false, "Silinecek bağlantı kimliği belirtilmedi.");
            }

            lock (_fileLock)
            {
                try
                {
                    string path = GetBiConfigPath();
                    if (!File.Exists(path)) return (false, "Yapılandırma dosyası bulunamadı.");

                    string json = File.ReadAllText(path);
                    var rootNode = JsonNode.Parse(json);
                    if (rootNode is not JsonObject rootObj || rootObj["DatabaseConnections"] is not JsonArray connArray)
                    {
                        return (false, "Kayıtlı bağlantı listesi bulunamadı.");
                    }

                    int removeIndex = -1;
                    string removedName = connectionId;
                    for (int i = 0; i < connArray.Count; i++)
                    {
                        if (connArray[i] is JsonObject obj &&
                            string.Equals(obj["Id"]?.GetValue<string>(), connectionId.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            removeIndex = i;
                            removedName = obj["EnvironmentName"]?.GetValue<string>() ?? connectionId;
                            break;
                        }
                    }

                    if (removeIndex < 0) return (false, "Silinmek istenen bağlantı profili bulunamadı.");

                    connArray.RemoveAt(removeIndex);

                    if (rootObj["ConnectionStrings"] is JsonObject connStringsObj)
                    {
                        JsonObject? nextActive = null;
                        foreach (var item in connArray)
                        {
                            if (item is JsonObject obj && obj["IsActive"]?.GetValue<bool>() == true)
                            {
                                nextActive = obj;
                                break;
                            }
                        }
                        nextActive ??= connArray.FirstOrDefault() as JsonObject;

                        if (nextActive != null)
                        {
                            string? nextConnStr = nextActive["ConnectionString"]?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(nextConnStr))
                            {
                                connStringsObj["MariaDbConnection"] = nextConnStr;
                            }
                        }
                        else
                        {
                            connStringsObj["MariaDbConnection"] = string.Empty;
                        }
                    }

                    var options = new JsonSerializerOptions { WriteIndented = true };
                    File.WriteAllText(path, rootObj.ToJsonString(options));

                    return (true, $"'{removedName}' BI bağlantı profili başarıyla silindi.");
                }
                catch (Exception ex)
                {
                    return (false, $"BI bağlantısı silinirken hata oluştu: {ex.Message}");
                }
            }
        }

        private string BuildConnectionString(DbConnectionInfoDto dto)
        {
            string password = ResolvePassword(dto);
            var builder = new MySqlConnectionStringBuilder
            {
                Server = dto.Server.Trim(),
                Port = dto.Port,
                Database = dto.Database.Trim(),
                UserID = dto.UserId.Trim(),
                Password = password,
                AllowLoadLocalInfile = false,
                AllowUserVariables = true,
                ConnectionTimeout = 8
            };
            return builder.ConnectionString;
        }

        private string ResolvePassword(DbConnectionInfoDto dto)
        {
            if (!string.IsNullOrEmpty(dto.Password))
                return dto.Password;

            var entries = ReadConnectionsFromDisk();
            if (!string.IsNullOrWhiteSpace(dto.Id))
            {
                var found = entries.FirstOrDefault(x => string.Equals(x.Dto.Id, dto.Id, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(found.RawConnectionString))
                {
                    var b = new MySqlConnectionStringBuilder(found.RawConnectionString);
                    string pwd = ConnectionProtector.DecryptSecret(b.Password);
                    if (!string.IsNullOrEmpty(pwd)) return pwd;
                }
            }

            var fallback = _dataConnManager.GetActiveConnectionString();
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                var fbBuilder = new MySqlConnectionStringBuilder(fallback);
                return ConnectionProtector.DecryptSecret(fbBuilder.Password);
            }

            return string.Empty;
        }

        private string WriteConnectionToDisk(DbConnectionInfoDto dto, string protectedConnectionString, bool setAsActiveMariaDb)
        {
            lock (_fileLock)
            {
                string path = GetBiConfigPath();
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

                if (rootNode["DatabaseConnections"] is not JsonArray connArray)
                {
                    connArray = new JsonArray();
                    rootNode["DatabaseConnections"] = connArray;
                }

                string targetId = string.IsNullOrWhiteSpace(dto.Id)
                    ? "bi_conn_" + Guid.NewGuid().ToString("N")[..8]
                    : dto.Id.Trim();

                JsonObject? foundObj = null;
                foreach (var item in connArray)
                {
                    if (item is JsonObject obj &&
                        string.Equals(obj["Id"]?.GetValue<string>(), targetId, StringComparison.OrdinalIgnoreCase))
                    {
                        foundObj = obj;
                        break;
                    }
                }

                if (foundObj == null)
                {
                    foundObj = new JsonObject();
                    connArray.Add(foundObj);
                }

                foundObj["Id"] = targetId;
                foundObj["EnvironmentName"] = dto.EnvironmentName.Trim();
                foundObj["Description"] = (dto.Description ?? string.Empty).Trim();
                foundObj["IsActive"] = dto.IsActive;
                foundObj["ConnectionString"] = protectedConnectionString;

                if (setAsActiveMariaDb && (string.IsNullOrWhiteSpace(connStringsObj["MariaDbConnection"]?.GetValue<string>()) || targetId == "bi_conn_default"))
                {
                    connStringsObj["MariaDbConnection"] = protectedConnectionString;
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(path, rootNode.ToJsonString(options));

                return targetId;
            }
        }
    }
}
