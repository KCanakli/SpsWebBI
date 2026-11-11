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
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Npgsql;
using SpsWebBI.Security;

namespace SpsWebBI.Services
{
    public class DbConnectionInfoDto
    {
        public string? Id { get; set; }
        public string EnvironmentName { get; set; } = "Canlı Ortam";
        public string Description { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
        public string DbType { get; set; } = "MariaDB"; // MariaDB, SQLite, MSSQL, PostgreSQL, Supabase
        public string SqliteFilePath { get; set; } = "App_Data/sps_bi.db";
        public string Server { get; set; } = "127.0.0.1";
        public uint Port { get; set; } = 3323;
        public string Database { get; set; } = "spsteks";
        public string UserId { get; set; } = "root";
        public string Password { get; set; } = string.Empty;
        public bool HasSavedPassword { get; set; }
        public bool CacheEnabled { get; set; } = true;
        public int MaxCacheableRows { get; set; } = 0; // 0 => appsettings.json varsayılanı
        public int MaxEntrySizeKB { get; set; } = 0; // 0 => appsettings.json varsayılanı
        public int SlidingExpirationSeconds { get; set; } = 0; // 0 => appsettings.json varsayılanı
    }

    /// <summary>
    /// Raporlama veri kaynağı bağlantı profillerini (MariaDB, SQLite, MSSQL, PostgreSQL, Supabase)
    /// appsettings.json üzerinde çoklu aktif bağlantı olarak yöneten servis.
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

        public static string NormalizeDbType(string? dbType)
        {
            string val = (dbType ?? string.Empty).Trim().ToLowerInvariant();
            return val switch
            {
                "sqlite" => "SQLite",
                "mssql" or "sqlserver" or "sql server" => "MSSQL",
                "postgresql" or "postgres" => "PostgreSQL",
                "supabase" => "Supabase",
                _ => "MariaDB"
            };
        }

        private string ResolveSqliteFullPath(string? configuredPath, bool ensureDirectoryExists = false)
        {
            string relOrAbs = string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine("App_Data", "erp_data.db")
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

        /// <summary>
        /// appsettings.json içindeki parolaları DPAPI (ENC:...) formatına çevirir.
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
                        raw = ConnectionProtector.ProtectConnectionStringPassword(raw, "MariaDB");
                        _configuration["ConnectionStrings:MariaDbConnection"] = raw;
                    }
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
            string dbType,
            string? sqliteFilePath,
            string rawConnStr)
        {
            string normType = NormalizeDbType(dbType);

            if (normType == "SQLite")
            {
                string filePath = !string.IsNullOrWhiteSpace(sqliteFilePath) ? sqliteFilePath : "App_Data/erp_data.db";
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
                    EnvironmentName = string.IsNullOrWhiteSpace(envName) ? "SQLite Ortam" : envName,
                    Description = description ?? string.Empty,
                    IsActive = isActive,
                    DbType = "SQLite",
                    SqliteFilePath = filePath,
                    Server = string.Empty,
                    Port = 0,
                    Database = string.Empty,
                    UserId = string.Empty,
                    Password = string.Empty,
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
                    DbType = normType,
                    SqliteFilePath = "App_Data/erp_data.db"
                };
            }

            try
            {
                if (normType == "MSSQL")
                {
                    var sqlB = new SqlConnectionStringBuilder(rawConnStr);
                    string ds = sqlB.DataSource ?? "127.0.0.1";
                    string srv = ds;
                    uint port = 1433u;
                    int commaIdx = ds.LastIndexOf(',');
                    if (commaIdx > 0 && uint.TryParse(ds[(commaIdx + 1)..].Trim(), out uint parsedPort))
                    {
                        srv = ds[..commaIdx].Trim();
                        port = parsedPort;
                    }
                    string decrypted = ConnectionProtector.DecryptSecret(sqlB.Password);
                    return new DbConnectionInfoDto
                    {
                        Id = id,
                        EnvironmentName = string.IsNullOrWhiteSpace(envName) ? "MSSQL Ortam" : envName,
                        Description = description ?? string.Empty,
                        IsActive = isActive,
                        DbType = "MSSQL",
                        SqliteFilePath = "App_Data/erp_data.db",
                        Server = srv,
                        Port = port,
                        Database = sqlB.InitialCatalog ?? string.Empty,
                        UserId = sqlB.UserID ?? string.Empty,
                        Password = string.Empty,
                        HasSavedPassword = !string.IsNullOrEmpty(decrypted)
                    };
                }

                if (normType == "PostgreSQL" || normType == "Supabase")
                {
                    var pgB = new NpgsqlConnectionStringBuilder(rawConnStr);
                    string decrypted = ConnectionProtector.DecryptSecret(pgB.Password);
                    return new DbConnectionInfoDto
                    {
                        Id = id,
                        EnvironmentName = string.IsNullOrWhiteSpace(envName) ? $"{normType} Ortam" : envName,
                        Description = description ?? string.Empty,
                        IsActive = isActive,
                        DbType = normType,
                        SqliteFilePath = "App_Data/erp_data.db",
                        Server = pgB.Host ?? "127.0.0.1",
                        Port = pgB.Port <= 0 ? 5432u : (uint)pgB.Port,
                        Database = pgB.Database ?? "postgres",
                        UserId = pgB.Username ?? "postgres",
                        Password = string.Empty,
                        HasSavedPassword = !string.IsNullOrEmpty(decrypted)
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
                    DbType = "MariaDB",
                    SqliteFilePath = "App_Data/erp_data.db",
                    Server = string.IsNullOrWhiteSpace(builder.Server) ? "127.0.0.1" : builder.Server,
                    Port = builder.Port == 0 ? 3323u : builder.Port,
                    Database = string.IsNullOrWhiteSpace(builder.Database) ? "spsteks" : builder.Database,
                    UserId = string.IsNullOrWhiteSpace(builder.UserID) ? "root" : builder.UserID,
                    Password = string.Empty,
                    HasSavedPassword = !string.IsNullOrEmpty(decryptedPwd)
                };
            }
            catch
            {
                return new DbConnectionInfoDto
                {
                    Id = id,
                    EnvironmentName = envName,
                    Description = description ?? string.Empty,
                    IsActive = isActive,
                    DbType = normType,
                    SqliteFilePath = "App_Data/erp_data.db"
                };
            }
        }

        private List<(DbConnectionInfoDto Dto, string RawConnectionString)> ReadConnectionsFromDisk()
        {
            var result = new List<(DbConnectionInfoDto, string)>();
            lock (_fileLock)
            {
                string path = GetAppSettingsPath();
                if (!File.Exists(path)) return result;

                try
                {
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
                            string? dbTypeNode = obj["DbType"]?.GetValue<string>();
                            string? sqlitePathNode = obj["SqliteFilePath"]?.GetValue<string>();

                            string dbType = !string.IsNullOrWhiteSpace(dbTypeNode)
                                ? NormalizeDbType(dbTypeNode)
                                : (rawConn.Contains("Data Source=", StringComparison.OrdinalIgnoreCase) && !rawConn.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase)
                                    ? "SQLite"
                                    : "MariaDB");

                            var dto = ParseConnectionNode(id, envName, desc, isActive, dbType, sqlitePathNode, rawConn);
                            dto.CacheEnabled = obj["CacheEnabled"]?.GetValue<bool>() ?? true;
                            dto.MaxCacheableRows = Math.Max(0, obj["MaxCacheableRows"]?.GetValue<int>() ?? 0);
                            dto.MaxEntrySizeKB = Math.Max(0, obj["MaxEntrySizeKB"]?.GetValue<int>() ?? 0);
                            dto.SlidingExpirationSeconds = Math.Max(0, obj["SlidingExpirationSeconds"]?.GetValue<int>() ?? 0);

                            result.Add((dto, rawConn));
                        }
                    }
                }
                catch { }
            }
            return result;
        }

        public List<DbConnectionInfoDto> GetAllConnections()
        {
            return ReadConnectionsFromDisk().Select(x => x.Dto).ToList();
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
            if (activeEntry.Dto != null && !string.IsNullOrWhiteSpace(activeEntry.RawConnectionString))
            {
                return activeEntry.Dto.DbType == "SQLite"
                    ? BuildSqliteConnectionString(activeEntry.Dto.SqliteFilePath, false)
                    : ConnectionProtector.GetUsableConnectionString(activeEntry.RawConnectionString, activeEntry.Dto.DbType);
            }

            if (entries.Count > 0 && !string.IsNullOrWhiteSpace(entries[0].RawConnectionString))
            {
                return entries[0].Dto.DbType == "SQLite"
                    ? BuildSqliteConnectionString(entries[0].Dto.SqliteFilePath, false)
                    : ConnectionProtector.GetUsableConnectionString(entries[0].RawConnectionString, entries[0].Dto.DbType);
            }

            string raw = _configuration.GetConnectionString("MariaDbConnection") ?? string.Empty;
            return ConnectionProtector.GetUsableConnectionString(raw, "MariaDB");
        }

        /// <summary>
        /// Bir SQL sorgusunun (bi_datasources) bağlı olduğu connection_id'ye göre hedef veritabanı tipini ve bağlantı dizesini döner.
        /// İlgili bağlantı pasifse (IsActive == false) sorgunun çalıştırılmasını engeller.
        /// </summary>
        public (bool IsUsable, string DbType, string ConnectionString, string ErrorMessage) ResolveConnectionForQuery(string? connectionId)
        {
            var entries = ReadConnectionsFromDisk();

            if (!string.IsNullOrWhiteSpace(connectionId))
            {
                var matched = entries.FirstOrDefault(x =>
                    string.Equals(x.Dto.Id, connectionId.Trim(), StringComparison.OrdinalIgnoreCase));

                if (matched.Dto != null && !string.IsNullOrWhiteSpace(matched.RawConnectionString))
                {
                    if (!matched.Dto.IsActive)
                    {
                        return (false, matched.Dto.DbType, string.Empty, $"'{matched.Dto.EnvironmentName}' veri kaynağı bağlantısı pasif durumda.");
                    }

                    string connStr = matched.Dto.DbType == "SQLite"
                        ? BuildSqliteConnectionString(matched.Dto.SqliteFilePath, ensureDirectoryExists: true)
                        : ConnectionProtector.GetUsableConnectionString(matched.RawConnectionString, matched.Dto.DbType);

                    return (true, matched.Dto.DbType, connStr, string.Empty);
                }
            }

            // connection_id belirtilmemişse (veya varsayılan ise) ilk aktif bağlantıyı kullan
            var firstActive = entries.FirstOrDefault(x => x.Dto.IsActive);
            if (firstActive.Dto != null && !string.IsNullOrWhiteSpace(firstActive.RawConnectionString))
            {
                string connStr = firstActive.Dto.DbType == "SQLite"
                    ? BuildSqliteConnectionString(firstActive.Dto.SqliteFilePath, ensureDirectoryExists: true)
                    : ConnectionProtector.GetUsableConnectionString(firstActive.RawConnectionString, firstActive.Dto.DbType);

                return (true, firstActive.Dto.DbType, connStr, string.Empty);
            }

            if (entries.Count > 0)
            {
                return (false, "MariaDB", string.Empty, "Tüm veri kaynağı bağlantıları pasif durumda.");
            }

            string fallback = GetActiveConnectionString();
            return (!string.IsNullOrWhiteSpace(fallback), "MariaDB", fallback, "Veri kaynağı bağlantısı bulunamadı.");
        }

        /// <summary>
        /// Sorgunun çalıştırılacağı hedef veritabanı bağlantısına (connection_id) ait önbellek kurallarını döner.
        /// </summary>
        public (bool CacheEnabled, int MaxCacheableRows, int MaxEntrySizeKB, int SlidingExpirationSeconds) ResolveConnectionCachePolicy(string? connectionId)
        {
            var entries = ReadConnectionsFromDisk();
            DbConnectionInfoDto? target = null;

            if (!string.IsNullOrWhiteSpace(connectionId))
            {
                target = entries.FirstOrDefault(x =>
                    string.Equals(x.Dto.Id, connectionId.Trim(), StringComparison.OrdinalIgnoreCase)).Dto;
            }
            target ??= entries.FirstOrDefault(x => x.Dto.IsActive).Dto;

            if (target == null)
            {
                return (true, 0, 0, 0);
            }

            return (
                target.CacheEnabled,
                Math.Max(0, target.MaxCacheableRows),
                Math.Max(0, target.MaxEntrySizeKB),
                Math.Max(0, target.SlidingExpirationSeconds)
            );
        }

        /// <summary>
        /// Çözümlenen hedef veri kaynağı bağlantısı türüne göre (MariaDB, SQLite, MSSQL, PostgreSQL, Supabase) DbConnection açıp döner.
        /// </summary>
        public async Task<DbConnection> OpenTargetConnectionAsync(string dbType, string connectionString)
        {
            string normType = NormalizeDbType(dbType);

            if (normType == "SQLite")
            {
                var sqliteConn = new SqliteConnection(connectionString);
                await sqliteConn.OpenAsync();
                await sqliteConn.ExecuteAsync("PRAGMA busy_timeout = 5000;");
                return sqliteConn;
            }

            if (normType == "MSSQL")
            {
                var sqlConn = new SqlConnection(connectionString);
                await sqlConn.OpenAsync();
                return sqlConn;
            }

            if (normType == "PostgreSQL" || normType == "Supabase")
            {
                var pgConn = new NpgsqlConnection(connectionString);
                await pgConn.OpenAsync();
                return pgConn;
            }

            var mysqlConn = new MySqlConnection(connectionString);
            await mysqlConn.OpenAsync();
            return mysqlConn;
        }

        public DbConnectionInfoDto GetCurrentSettings()
        {
            var all = GetAllConnections();
            return all.FirstOrDefault(x => x.IsActive) ?? all.FirstOrDefault() ?? new DbConnectionInfoDto
            {
                SqliteFilePath = "App_Data/erp_data.db"
            };
        }

        private string ResolvePassword(DbConnectionInfoDto dto)
        {
            if (!string.IsNullOrEmpty(dto.Password))
                return dto.Password;

            string normType = NormalizeDbType(dto.DbType);
            var entries = ReadConnectionsFromDisk();
            if (!string.IsNullOrWhiteSpace(dto.Id))
            {
                var found = entries.FirstOrDefault(x => string.Equals(x.Dto.Id, dto.Id, StringComparison.OrdinalIgnoreCase));
                if (found.Dto != null && !string.IsNullOrWhiteSpace(found.RawConnectionString) && found.Dto.DbType != "SQLite")
                {
                    try
                    {
                        if (found.Dto.DbType == "MSSQL")
                        {
                            var sb = new SqlConnectionStringBuilder(found.RawConnectionString);
                            string pwd = ConnectionProtector.DecryptSecret(sb.Password);
                            if (!string.IsNullOrEmpty(pwd)) return pwd;
                        }
                        else if (found.Dto.DbType == "PostgreSQL" || found.Dto.DbType == "Supabase")
                        {
                            var pgb = new NpgsqlConnectionStringBuilder(found.RawConnectionString);
                            string pwd = ConnectionProtector.DecryptSecret(pgb.Password);
                            if (!string.IsNullOrEmpty(pwd)) return pwd;
                        }
                        else
                        {
                            var b = new MySqlConnectionStringBuilder(found.RawConnectionString);
                            string pwd = ConnectionProtector.DecryptSecret(b.Password);
                            if (!string.IsNullOrEmpty(pwd)) return pwd;
                        }
                    }
                    catch { }
                }
            }

            if (normType == "MariaDB")
            {
                string fallbackRaw = _configuration.GetConnectionString("MariaDbConnection") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(fallbackRaw))
                {
                    try
                    {
                        var fbBuilder = new MySqlConnectionStringBuilder(fallbackRaw);
                        return ConnectionProtector.DecryptSecret(fbBuilder.Password);
                    }
                    catch { }
                }
            }

            return string.Empty;
        }

        public string BuildConnectionString(DbConnectionInfoDto dto)
        {
            string normType = NormalizeDbType(dto.DbType);

            if (normType == "SQLite")
            {
                return BuildSqliteConnectionString(dto.SqliteFilePath, ensureDirectoryExists: false);
            }

            string passwordToUse = ResolvePassword(dto);

            if (normType == "MSSQL")
            {
                string srv = (dto.Server ?? "127.0.0.1").Trim();
                string dataSource = (srv.Contains(',') || srv.Contains('\\'))
                    ? srv
                    : (dto.Port > 0 ? $"{srv},{dto.Port}" : srv);

                var sqlBuilder = new SqlConnectionStringBuilder
                {
                    DataSource = dataSource,
                    InitialCatalog = (dto.Database ?? string.Empty).Trim(),
                    TrustServerCertificate = true,
                    Encrypt = SqlConnectionEncryptOption.Optional,
                    ConnectTimeout = 8
                };

                if (string.IsNullOrWhiteSpace(dto.UserId))
                {
                    sqlBuilder.IntegratedSecurity = true;
                }
                else
                {
                    sqlBuilder.UserID = dto.UserId.Trim();
                    sqlBuilder.Password = passwordToUse;
                }

                return sqlBuilder.ConnectionString;
            }

            if (normType == "PostgreSQL" || normType == "Supabase")
            {
                var pgBuilder = new NpgsqlConnectionStringBuilder
                {
                    Host = (dto.Server ?? "127.0.0.1").Trim(),
                    Port = dto.Port == 0 ? 5432 : (int)dto.Port,
                    Database = string.IsNullOrWhiteSpace(dto.Database) ? "postgres" : dto.Database.Trim(),
                    Username = string.IsNullOrWhiteSpace(dto.UserId) ? "postgres" : dto.UserId.Trim(),
                    Password = passwordToUse,
                    Timeout = normType == "Supabase" ? 12 : 8,
                    CommandTimeout = 30
                };

                if (normType == "Supabase")
                {
                    pgBuilder.SslMode = SslMode.Require;
                    // Supabase Transaction Pooler (port 6543) ile Npgsql prepared statement çakışmasını önle
                    if (pgBuilder.Port == 6543)
                    {
                        pgBuilder.MaxAutoPrepare = 0;
                    }
                }

                return pgBuilder.ConnectionString;
            }

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
            string dbType = NormalizeDbType(dto.DbType);
            string envTitle = string.IsNullOrWhiteSpace(dto.EnvironmentName) ? "Bağlantı" : dto.EnvironmentName.Trim();

            if (dbType == "SQLite")
            {
                string targetPath = string.IsNullOrWhiteSpace(dto.SqliteFilePath) ? "App_Data/erp_data.db" : dto.SqliteFilePath.Trim();
                string fullPath = ResolveSqliteFullPath(targetPath, ensureDirectoryExists: true);
                bool existedBefore = File.Exists(fullPath);

                try
                {
                    string connStr = BuildSqliteConnectionString(targetPath, ensureDirectoryExists: true);
                    await using var conn = new SqliteConnection(connStr);
                    await conn.OpenAsync();

                    string ver = await conn.ExecuteScalarAsync<string>("SELECT sqlite_version();") ?? "?";
                    int tableCount = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';");

                    await conn.CloseAsync();
                    SqliteConnection.ClearPool(conn);

                    if (!existedBefore && File.Exists(fullPath))
                    {
                        try { File.Delete(fullPath); } catch { }
                    }

                    return (true, $"[{envTitle}] Bağlantı başarılı! (SQLite Sürümü: {ver}, Tablo Sayısı: {tableCount}, Dosya: {targetPath})");
                }
                catch (Exception ex)
                {
                    return (false, $"SQLite bağlantısı kurulamadı: {ex.Message}");
                }
            }

            if (dbType == "MSSQL")
            {
                try
                {
                    string connStr = BuildConnectionString(dto);
                    await using var conn = new SqlConnection(connStr);
                    await conn.OpenAsync();
                    return (true, $"[{envTitle}] Bağlantı başarılı! (MSSQL Sürümü: {conn.ServerVersion}, Veritabanı: {conn.Database})");
                }
                catch (Exception ex)
                {
                    return (false, $"MSSQL bağlantısı kurulamadı: {ex.Message}");
                }
            }

            if (dbType == "PostgreSQL" || dbType == "Supabase")
            {
                try
                {
                    string connStr = BuildConnectionString(dto);
                    await using var conn = new NpgsqlConnection(connStr);
                    await conn.OpenAsync();
                    return (true, $"[{envTitle}] Bağlantı başarılı! ({dbType} / PostgreSQL Sürümü: {conn.PostgreSqlVersion}, Veritabanı: {conn.Database})");
                }
                catch (Exception ex)
                {
                    return (false, $"{dbType} bağlantısı kurulamadı: {ex.Message}");
                }
            }

            try
            {
                string connStr = BuildConnectionString(dto);
                await using var conn = new MySqlConnection(connStr);
                await conn.OpenAsync();
                string serverVersion = conn.ServerVersion;
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

            dto.DbType = NormalizeDbType(dto.DbType);
            if (dto.DbType == "SQLite" && string.IsNullOrWhiteSpace(dto.SqliteFilePath))
            {
                dto.SqliteFilePath = "App_Data/erp_data.db";
            }

            var testResult = await TestConnectionAsync(dto);
            if (!testResult.Success)
            {
                return (false, testResult.Message, string.Empty);
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
                    string usableConnStr = BuildConnectionString(dto);
                    protectedConnStr = ConnectionProtector.ProtectConnectionStringPassword(usableConnStr, dto.DbType);
                }

                string savedId = WriteConnectionToDisk(dto, protectedConnStr, setAsActiveMariaDb: dto.IsActive && dto.DbType == "MariaDB");

                string stateText = dto.IsActive ? "Aktif" : "Pasif";
                return (true, $"'{dto.EnvironmentName.Trim()}' ({dto.DbType} - {stateText}) bağlantı ayarları şifrelenerek kaydedildi.", savedId);
            }
            catch (Exception ex)
            {
                return (false, $"Ayarlar kaydedilirken hata oluştu: {ex.Message}", string.Empty);
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
                    string path = GetAppSettingsPath();
                    if (!File.Exists(path))
                    {
                        return (false, "Yapılandırma dosyası bulunamadı.");
                    }

                    string json = File.ReadAllText(path);
                    var rootNode = JsonNode.Parse(json);
                    if (rootNode is not JsonObject rootObj)
                    {
                        return (false, "Yapılandırma verisi okunamadı.");
                    }

                    if (rootObj["DatabaseConnections"] is not JsonArray connArray)
                    {
                        return (false, "Kayıtlı bağlantı listesi bulunamadı.");
                    }

                    if (connArray.Count == 0)
                    {
                        return (false, "Kayıtlı bağlantı bulunamadı.");
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

                    if (removeIndex < 0)
                    {
                        return (false, "Silinmek istenen bağlantı profili bulunamadı.");
                    }

                    connArray.RemoveAt(removeIndex);

                    if (rootObj["ConnectionStrings"] is JsonObject connStringsObj)
                    {
                        JsonObject? nextActive = null;
                        foreach (var item in connArray)
                        {
                            if (item is JsonObject obj &&
                                obj["IsActive"]?.GetValue<bool>() == true &&
                                string.Equals(obj["DbType"]?.GetValue<string>() ?? "MariaDB", "MariaDB", StringComparison.OrdinalIgnoreCase))
                            {
                                nextActive = obj;
                                break;
                            }
                        }

                        if (nextActive != null)
                        {
                            string? nextConnStr = nextActive["ConnectionString"]?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(nextConnStr))
                            {
                                connStringsObj["MariaDbConnection"] = nextConnStr;
                                _configuration["ConnectionStrings:MariaDbConnection"] = nextConnStr;
                            }
                        }
                    }

                    var options = new JsonSerializerOptions { WriteIndented = true };
                    File.WriteAllText(path, rootObj.ToJsonString(options));

                    return (true, $"'{removedName}' bağlantı profili başarıyla silindi.");
                }
                catch (Exception ex)
                {
                    return (false, $"Bağlantı silinirken hata oluştu: {ex.Message}");
                }
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

                // Çoklu veri kaynağı desteği: Diğer bağlantıların IsActive durumu korunur!
                foundObj["Id"] = targetId;
                foundObj["EnvironmentName"] = dto.EnvironmentName.Trim();
                foundObj["Description"] = (dto.Description ?? string.Empty).Trim();
                foundObj["DbType"] = dto.DbType;
                foundObj["SqliteFilePath"] = dto.DbType == "SQLite" ? dto.SqliteFilePath.Trim() : string.Empty;
                foundObj["IsActive"] = dto.IsActive;
                foundObj["CacheEnabled"] = dto.CacheEnabled;
                foundObj["MaxCacheableRows"] = Math.Max(0, dto.MaxCacheableRows);
                foundObj["MaxEntrySizeKB"] = Math.Max(0, dto.MaxEntrySizeKB);
                foundObj["SlidingExpirationSeconds"] = Math.Max(0, dto.SlidingExpirationSeconds);
                foundObj["ConnectionString"] = protectedConnectionString;

                if (rootNode is JsonObject rootObj && rootObj.ContainsKey("DatabaseEnvironments"))
                {
                    rootObj.Remove("DatabaseEnvironments");
                }

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
