using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using SpsWebBI.Security;

namespace SpsWebBI.Services
{
    /// <summary>
    /// BI sistem tablolarının (bi_users, bi_dashboards, bi_datasources vb.) tutulacağı
    /// MariaDB veya SQLite bağlantı profillerini 'appsettings.bi.json' üzerinden yöneten servis.
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
                    File.WriteAllText(path, "{\n  \"ConnectionStrings\": {\n    \"MariaDbConnection\": \"\"\n  },\n  \"DatabaseConnections\": []\n}");
                }
            }
            catch { }
        }

        private static string NormalizeDbType(string? dbType)
        {
            return string.Equals(dbType?.Trim(), "SQLite", StringComparison.OrdinalIgnoreCase)
                ? "SQLite"
                : "MariaDB";
        }

        private string ResolveSqliteFullPath(string? configuredPath, bool ensureDirectoryExists = false)
        {
            string relOrAbs = string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine("App_Data", "sps_bi.db")
                : configuredPath.Trim();

            string fullPath = Path.IsPathRooted(relOrAbs)
                ? Path.GetFullPath(relOrAbs)
                : Path.GetFullPath(Path.Combine(_env.ContentRootPath, relOrAbs));

            if (ensureDirectoryExists)
            {
                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }

            return fullPath;
        }

        private string BuildSqliteConnectionString(string? sqlitePath, bool ensureDirectoryExists = false)
        {
            string fullPath = ResolveSqliteFullPath(sqlitePath, ensureDirectoryExists);
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            };
            return builder.ConnectionString;
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
                                string? dbTypeNode = obj["DbType"]?.GetValue<string>();
                                string? sqlitePathNode = obj["SqliteFilePath"]?.GetValue<string>();

                                string dbType = !string.IsNullOrWhiteSpace(dbTypeNode)
                                    ? NormalizeDbType(dbTypeNode)
                                    : (rawConn.Contains("Data Source=", StringComparison.OrdinalIgnoreCase) ? "SQLite" : "MariaDB");

                                var dto = ParseConnectionNode(id, envName, desc, isActive, dbType, sqlitePathNode, rawConn);
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
            string dbType,
            string? sqliteFilePath,
            string rawConnStr)
        {
            if (string.Equals(dbType, "SQLite", StringComparison.OrdinalIgnoreCase))
            {
                string filePath = !string.IsNullOrWhiteSpace(sqliteFilePath) ? sqliteFilePath : "App_Data/sps_bi.db";
                if (string.IsNullOrWhiteSpace(sqliteFilePath) && !string.IsNullOrWhiteSpace(rawConnStr))
                {
                    try
                    {
                        var sb = new SqliteConnectionStringBuilder(rawConnStr);
                        if (!string.IsNullOrWhiteSpace(sb.DataSource))
                        {
                            filePath = sb.DataSource;
                        }
                    }
                    catch { }
                }

                return new DbConnectionInfoDto
                {
                    Id = id,
                    EnvironmentName = envName,
                    Description = description,
                    IsActive = isActive,
                    DbType = "SQLite",
                    SqliteFilePath = filePath,
                    Server = string.Empty,
                    Port = 0,
                    Database = string.Empty,
                    UserId = string.Empty,
                    HasSavedPassword = false
                };
            }

            if (string.IsNullOrWhiteSpace(rawConnStr))
            {
                return new DbConnectionInfoDto
                {
                    Id = id,
                    EnvironmentName = envName,
                    Description = description,
                    IsActive = isActive,
                    DbType = "MariaDB"
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
                    DbType = "MariaDB",
                    SqliteFilePath = "App_Data/sps_bi.db",
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
                    IsActive = isActive,
                    DbType = "MariaDB"
                };
            }
        }

        public List<DbConnectionInfoDto> GetAllConnections()
        {
            var entries = ReadConnectionsFromDisk();
            return entries.Select(x => x.Dto).ToList();
        }

        private (string DbType, string ConnectionString, string SqliteFilePath) GetActiveProfile()
        {
            var entries = ReadConnectionsFromDisk();
            var activeEntry = entries.FirstOrDefault(x => x.Dto.IsActive);
            if (activeEntry.Dto == null && entries.Count > 0)
            {
                activeEntry = entries[0];
            }

            if (activeEntry.Dto != null)
            {
                if (string.Equals(activeEntry.Dto.DbType, "SQLite", StringComparison.OrdinalIgnoreCase))
                {
                    string sqlConn = BuildSqliteConnectionString(activeEntry.Dto.SqliteFilePath, ensureDirectoryExists: false);
                    return ("SQLite", sqlConn, activeEntry.Dto.SqliteFilePath);
                }

                if (!string.IsNullOrWhiteSpace(activeEntry.RawConnectionString))
                {
                    return ("MariaDB", ConnectionProtector.GetUsableConnectionString(activeEntry.RawConnectionString), string.Empty);
                }
            }

            return ("MariaDB", string.Empty, string.Empty);
        }

        public string GetActiveDbType() => GetActiveProfile().DbType;

        public bool IsSqlite => string.Equals(GetActiveDbType(), "SQLite", StringComparison.OrdinalIgnoreCase);

        public string LastInsertIdSql => IsSqlite ? "SELECT last_insert_rowid();" : "SELECT LAST_INSERT_ID();";

        public string GetBiConnectionString() => GetActiveProfile().ConnectionString;

        /// <summary>
        /// Aktif BI veritabanı türüne (SQLite veya MariaDB) uygun bağlantıyı açıp döner.
        /// SQLite bağlantılarında WAL modu ve foreign keys otomatik etkinleştirilir.
        /// </summary>
        public async Task<DbConnection> OpenBiConnectionAsync()
        {
            var profile = GetActiveProfile();
            if (string.IsNullOrWhiteSpace(profile.ConnectionString))
            {
                throw new InvalidOperationException("Aktif BI veritabanı bağlantısı tanımlı değil. Lütfen /bi-settings ekranından MariaDB veya SQLite kurulumunu yapınız.");
            }

            if (string.Equals(profile.DbType, "SQLite", StringComparison.OrdinalIgnoreCase))
            {
                string connStr = BuildSqliteConnectionString(profile.SqliteFilePath, ensureDirectoryExists: true);
                var sqliteConn = new SqliteConnection(connStr);
                await sqliteConn.OpenAsync();
                await sqliteConn.ExecuteAsync("PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
                return sqliteConn;
            }

            var mysqlConn = new MySqlConnection(profile.ConnectionString);
            await mysqlConn.OpenAsync();
            return mysqlConn;
        }

        public DbConnectionInfoDto GetCurrentSettings()
        {
            var all = GetAllConnections();
            return all.FirstOrDefault(x => x.IsActive) ?? all.FirstOrDefault() ?? new DbConnectionInfoDto
            {
                EnvironmentName = "BI Canlı",
                Description = "BI Sistem Veritabanı",
                DbType = "MariaDB",
                SqliteFilePath = "App_Data/sps_bi.db"
            };
        }

        public async Task<bool> HasWorkingAdminUserAsync()
        {
            var profile = GetActiveProfile();
            if (string.IsNullOrWhiteSpace(profile.ConnectionString)) return false;

            try
            {
                if (string.Equals(profile.DbType, "SQLite", StringComparison.OrdinalIgnoreCase))
                {
                    string fullPath = ResolveSqliteFullPath(profile.SqliteFilePath, ensureDirectoryExists: false);
                    if (!File.Exists(fullPath)) return false;

                    await using var conn = new SqliteConnection(BuildSqliteConnectionString(profile.SqliteFilePath, ensureDirectoryExists: false));
                    await conn.OpenAsync();

                    int tblCount = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'bi_users';");
                    if (tblCount == 0) return false;

                    int adminCount = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM bi_users WHERE role = 'admin' AND is_active = 1;");
                    return adminCount > 0;
                }
                else
                {
                    await using var conn = new MySqlConnection(profile.ConnectionString);
                    await conn.OpenAsync();

                    int userCount = await conn.ExecuteScalarAsync<int>(@"
                        SELECT COUNT(*) FROM information_schema.TABLES
                         WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bi_users';");
                    if (userCount == 0) return false;

                    int adminCount = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM bi_users WHERE role = 'admin' AND is_active = 1;");
                    return adminCount > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public async Task<(bool Success, string Message)> TestBiConnectionAsync(DbConnectionInfoDto dto)
        {
            string dbType = NormalizeDbType(dto.DbType);
            if (dbType == "SQLite")
            {
                string targetPath = string.IsNullOrWhiteSpace(dto.SqliteFilePath) ? "App_Data/sps_bi.db" : dto.SqliteFilePath.Trim();
                string fullPath = ResolveSqliteFullPath(targetPath, ensureDirectoryExists: true);
                bool existedBefore = File.Exists(fullPath);

                try
                {
                    string connStr = BuildSqliteConnectionString(targetPath, ensureDirectoryExists: true);
                    await using var conn = new SqliteConnection(connStr);
                    await conn.OpenAsync();

                    string ver = await conn.ExecuteScalarAsync<string>("SELECT sqlite_version();") ?? "?";
                    await conn.CloseAsync();
                    SqliteConnection.ClearPool(conn);

                    // Önceden dosya yoksa, Test butonu arkasında boş dosya bırakmaması için temizle
                    if (!existedBefore && File.Exists(fullPath))
                    {
                        try { File.Delete(fullPath); } catch { }
                    }

                    string envTitle = string.IsNullOrWhiteSpace(dto.EnvironmentName) ? "BI SQLite" : dto.EnvironmentName;
                    return (true, $"[{envTitle}] Bağlantı başarılı! (SQLite Sürümü: {ver}, Dosya: {targetPath})");
                }
                catch (Exception ex)
                {
                    return (false, $"SQLite bağlantı hatası: {ex.Message}");
                }
            }

            try
            {
                string connStr = BuildMariaDbConnectionString(dto);
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
            dto.DbType = NormalizeDbType(dto.DbType);

            if (dto.DbType == "SQLite")
            {
                if (string.IsNullOrWhiteSpace(dto.SqliteFilePath))
                {
                    dto.SqliteFilePath = "App_Data/sps_bi.db";
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(dto.Server) || dto.Port == 0 ||
                    string.IsNullOrWhiteSpace(dto.Database) || string.IsNullOrWhiteSpace(dto.UserId))
                {
                    return (false, "Sunucu, port, veritabanı adı ve kullanıcı adı zorunludur.", string.Empty);
                }
            }

            if (string.IsNullOrWhiteSpace(dto.EnvironmentName))
            {
                dto.EnvironmentName = dto.DbType == "SQLite" ? "BI Yerel (SQLite)" : "BI Ortam";
            }

            var test = await TestBiConnectionAsync(dto);
            if (!test.Success)
            {
                return (false, $"Bağlantı doğrulanamadı: {test.Message}", string.Empty);
            }

            try
            {
                string protectedConnStr;
                if (dto.DbType == "SQLite")
                {
                    ResolveSqliteFullPath(dto.SqliteFilePath, ensureDirectoryExists: true);
                    protectedConnStr = $"Data Source={dto.SqliteFilePath.Trim()}";
                }
                else
                {
                    string usableConnStr = BuildMariaDbConnectionString(dto);
                    protectedConnStr = ConnectionProtector.ProtectConnectionStringPassword(usableConnStr);
                }

                string savedId = WriteConnectionToDisk(dto, protectedConnStr);

                string stateText = dto.IsActive ? "Aktif" : "Pasif";
                return (true, $"'{dto.EnvironmentName.Trim()}' ({dto.DbType} - {stateText}) BI bağlantı ayarları kaydedildi.", savedId);
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

        private string BuildMariaDbConnectionString(DbConnectionInfoDto dto)
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
                if (!string.IsNullOrWhiteSpace(found.RawConnectionString) && found.Dto.DbType != "SQLite")
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

        private string WriteConnectionToDisk(DbConnectionInfoDto dto, string protectedConnectionString)
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

                // Yeni/güncellenen profil Aktif yapılıyorsa diğer BI bağlantılarının aktifliğini kapat
                if (dto.IsActive)
                {
                    foreach (var item in connArray)
                    {
                        if (item is JsonObject obj &&
                            !string.Equals(obj["Id"]?.GetValue<string>(), targetId, StringComparison.OrdinalIgnoreCase))
                        {
                            obj["IsActive"] = false;
                        }
                    }
                }

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
                foundObj["DbType"] = dto.DbType;
                foundObj["SqliteFilePath"] = dto.DbType == "SQLite" ? dto.SqliteFilePath.Trim() : string.Empty;
                foundObj["IsActive"] = dto.IsActive;
                foundObj["ConnectionString"] = protectedConnectionString;

                if (dto.IsActive)
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
