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
    public class DbConnectionInfoDto
    {
        public string? Id { get; set; }
        public string EnvironmentName { get; set; } = "Canlı Ortam";
        public string Description { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
        public string Server { get; set; } = "127.0.0.1";
        public uint Port { get; set; } = 3323;
        public string Database { get; set; } = "spsteks";
        public string UserId { get; set; } = "root";
        public string Password { get; set; } = string.Empty;
        public bool HasSavedPassword { get; set; }
    }

    /// <summary>
    /// MariaDB bağlantı profillerini (Ortam İsmi, Açıklama, Aktif/Pasif, Sunucu, DB ve DPAPI şifreli parola)
    /// appsettings.json üzerinde çoklu kayıt olarak yöneten servis.
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
        /// appsettings.json içindeki parolaları DPAPI (ENC:...) formatına çevirir ve
        /// DatabaseConnections listesi henüz yoksa mevcut bağlantıdan ilk kaydı oluşturur.
        /// </summary>
        public void EnsurePasswordEncryptedOnDisk()
        {
            try
            {
                string raw = _configuration.GetConnectionString("MariaDbConnection") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var builder = new MySqlConnectionStringBuilder(raw);
                    if (!string.IsNullOrEmpty(builder.Password) && !builder.Password.StartsWith("ENC:", StringComparison.Ordinal))
                    {
                        raw = ConnectionProtector.ProtectConnectionStringPassword(raw);
                        _configuration["ConnectionStrings:MariaDbConnection"] = raw;
                    }
                }

                var list = ReadConnectionsFromDisk();
                if (list.Count == 0 && !string.IsNullOrWhiteSpace(raw))
                {
                    var initial = ParseConnectionNode("conn_default", "Canlı Ortam", "Varsayılan MariaDB Bağlantısı", true, raw);
                    WriteConnectionToDisk(initial, raw, setAsActiveMariaDb: true);
                }
            }
            catch
            {
                // Dosya erişim hatası olursa çalışma zamanını kesme
            }
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

            var builder = new MySqlConnectionStringBuilder(rawConnStr);
            string decryptedPwd = ConnectionProtector.DecryptSecret(builder.Password);

            return new DbConnectionInfoDto
            {
                Id = id,
                EnvironmentName = string.IsNullOrWhiteSpace(envName) ? "Canlı Ortam" : envName,
                Description = description ?? string.Empty,
                IsActive = isActive,
                Server = string.IsNullOrWhiteSpace(builder.Server) ? "127.0.0.1" : builder.Server,
                Port = builder.Port == 0 ? 3323u : builder.Port,
                Database = string.IsNullOrWhiteSpace(builder.Database) ? "spsteks" : builder.Database,
                UserId = string.IsNullOrWhiteSpace(builder.UserID) ? "root" : builder.UserID,
                Password = string.Empty,
                HasSavedPassword = !string.IsNullOrEmpty(decryptedPwd)
            };
        }

        private List<(DbConnectionInfoDto Dto, string RawConnectionString)> ReadConnectionsFromDisk()
        {
            var result = new List<(DbConnectionInfoDto, string)>();
            lock (_fileLock)
            {
                string path = GetAppSettingsPath();
                if (!File.Exists(path)) return result;

                string json = File.ReadAllText(path);
                var rootNode = JsonNode.Parse(json);
                if (rootNode?["DatabaseConnections"] is JsonArray arr)
                {
                    foreach (var item in arr)
                    {
                        if (item is not JsonObject obj) continue;
                        string id = obj["Id"]?.GetValue<string>() ?? ("conn_" + Guid.NewGuid().ToString("N")[..8]);
                        string envName = obj["EnvironmentName"]?.GetValue<string>() ?? "Canlı Ortam";
                        string desc = obj["Description"]?.GetValue<string>() ?? string.Empty;
                        bool isActive = obj["IsActive"]?.GetValue<bool>() ?? false;
                        string rawConn = obj["ConnectionString"]?.GetValue<string>() ?? string.Empty;
                        result.Add((ParseConnectionNode(id, envName, desc, isActive, rawConn), rawConn));
                    }
                }
            }
            return result;
        }

        public List<DbConnectionInfoDto> GetAllConnections()
        {
            var list = ReadConnectionsFromDisk().Select(x => x.Dto).ToList();
            if (list.Count == 0)
            {
                string raw = _configuration.GetConnectionString("MariaDbConnection") ?? string.Empty;
                list.Add(ParseConnectionNode("conn_default", "Canlı Ortam", "Varsayılan MariaDB Bağlantısı", true, raw));
            }
            return list;
        }

        public bool IsActiveConnectionEnabled()
        {
            var list = GetAllConnections();
            return list.Any(x => x.IsActive);
        }

        public string GetActiveConnectionString()
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

            string raw = _configuration.GetConnectionString("MariaDbConnection") ?? string.Empty;
            return ConnectionProtector.GetUsableConnectionString(raw);
        }

        /// <summary>
        /// Bir SQL sorgusunun (bi_datasources) bağlı olduğu connection_id'ye göre hedef veritabanı bağlantı dizesini döner.
        /// İlgili bağlantı pasifse (IsActive == false) sorgunun çalıştırılmasını engeller.
        /// </summary>
        public (bool IsUsable, string ConnectionString, string ErrorMessage) ResolveConnectionForQuery(string? connectionId)
        {
            var entries = ReadConnectionsFromDisk();

            if (!string.IsNullOrWhiteSpace(connectionId))
            {
                var matched = entries.FirstOrDefault(x =>
                    string.Equals(x.Dto.Id, connectionId.Trim(), StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(matched.RawConnectionString))
                {
                    if (!matched.Dto.IsActive)
                    {
                        return (false, string.Empty, $"'{matched.Dto.EnvironmentName}' veri kaynağı bağlantısı pasif durumda.");
                    }
                    return (true, ConnectionProtector.GetUsableConnectionString(matched.RawConnectionString), string.Empty);
                }
            }

            // connection_id belirtilmemişse (veya varsayılan ise) ilk aktif bağlantıyı kullan
            var firstActive = entries.FirstOrDefault(x => x.Dto.IsActive);
            if (!string.IsNullOrWhiteSpace(firstActive.RawConnectionString))
            {
                return (true, ConnectionProtector.GetUsableConnectionString(firstActive.RawConnectionString), string.Empty);
            }

            if (entries.Count > 0)
            {
                return (false, string.Empty, "Tüm veri kaynağı bağlantıları pasif durumda.");
            }

            string fallback = GetActiveConnectionString();
            return (!string.IsNullOrWhiteSpace(fallback), fallback, "Veri kaynağı bağlantısı bulunamadı.");
        }

        public DbConnectionInfoDto GetCurrentSettings()
        {
            var all = GetAllConnections();
            return all.FirstOrDefault(x => x.IsActive) ?? all.FirstOrDefault() ?? new DbConnectionInfoDto();
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

            string fallbackRaw = _configuration.GetConnectionString("MariaDbConnection") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(fallbackRaw))
            {
                var fbBuilder = new MySqlConnectionStringBuilder(fallbackRaw);
                return ConnectionProtector.DecryptSecret(fbBuilder.Password);
            }

            return string.Empty;
        }

        public string BuildConnectionString(DbConnectionInfoDto dto)
        {
            string passwordToUse = ResolvePassword(dto);

            var builder = new MySqlConnectionStringBuilder
            {
                Server = (dto.Server ?? "127.0.0.1").Trim(),
                Port = dto.Port == 0 ? 3323u : dto.Port,
                Database = (dto.Database ?? "spsteks").Trim(),
                UserID = (dto.UserId ?? "root").Trim(),
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
                string envTitle = string.IsNullOrWhiteSpace(dto.EnvironmentName) ? "Bağlantı" : dto.EnvironmentName.Trim();
                return (true, $"[{envTitle}] Bağlantı başarılı! (MariaDB Sürümü: {serverVersion}, Veritabanı: {conn.Database})");
            }
            catch (Exception ex)
            {
                return (false, $"Bağlantı kurulamadı: {ex.Message}");
            }
        }

        public async Task<(bool Success, string Message, string SavedId)> SaveConnectionSettingsAsync(DbConnectionInfoDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.EnvironmentName))
            {
                return (false, "Lütfen Ortam İsmi alanını doldurun.", string.Empty);
            }

            var testResult = await TestConnectionAsync(dto);
            if (!testResult.Success)
            {
                return (false, testResult.Message, string.Empty);
            }

            try
            {
                string usableConnStr = BuildConnectionString(dto);
                string protectedConnStr = ConnectionProtector.ProtectConnectionStringPassword(usableConnStr);

                string savedId = WriteConnectionToDisk(dto, protectedConnStr, setAsActiveMariaDb: dto.IsActive);

                string stateText = dto.IsActive ? "Aktif" : "Pasif";
                return (true, $"'{dto.EnvironmentName.Trim()}' ({stateText}) bağlantı ayarları şifrelenerek kaydedildi.", savedId);
            }
            catch (Exception ex)
            {
                return (false, $"Ayarlar kaydedilirken hata oluştu: {ex.Message}", string.Empty);
            }
        }

        private string WriteConnectionToDisk(DbConnectionInfoDto dto, string protectedConnectionString, bool setAsActiveMariaDb)
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

                if (rootNode["DatabaseConnections"] is not JsonArray connArray)
                {
                    connArray = new JsonArray();
                    rootNode["DatabaseConnections"] = connArray;
                }

                string targetId = string.IsNullOrWhiteSpace(dto.Id)
                    ? "conn_" + Guid.NewGuid().ToString("N")[..8]
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

                // Eski düğüm varsa temizle
                if (rootNode is JsonObject rootObj && rootObj.ContainsKey("DatabaseEnvironments"))
                {
                    rootObj.Remove("DatabaseEnvironments");
                }

                // Ana MariaDbConnection sistem bağlantısı boşsa veya ilk kayıt güncelleniyorsa senkronize tut
                if (setAsActiveMariaDb && (string.IsNullOrWhiteSpace(connStringsObj["MariaDbConnection"]?.GetValue<string>()) || targetId == "conn_default"))
                {
                    connStringsObj["MariaDbConnection"] = protectedConnectionString;
                    _configuration["ConnectionStrings:MariaDbConnection"] = protectedConnectionString;
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(path, rootNode.ToJsonString(options));

                return targetId;
            }
        }
    }
}
