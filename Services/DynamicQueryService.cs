using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MySqlConnector;
using SpsWebBI.Models;
using SpsWebBI.Security;

namespace SpsWebBI.Services
{
    public class QueryCacheOptions
    {
        public int SizeLimitMB { get; set; } = 128;
        public int MaxCacheableRows { get; set; } = 10000;
        public int MaxEntrySizeKB { get; set; } = 8192;
        public int DefaultSlidingExpirationSeconds { get; set; } = 60;
        public double CompactionPercentage { get; set; } = 0.25;
    }
    public class ColumnSchemaInfo
    {
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = "string"; // number, date, string, boolean
    }

    public class DynamicQueryResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<ColumnSchemaInfo> Columns { get; set; } = new();
        public List<Dictionary<string, object?>> Rows { get; set; } = new();
        public int TotalRowCount => Rows.Count;
        public bool FromCache { get; set; }
        public DateTime? CachedAtUtc { get; set; }
        public int CacheTtlSeconds { get; set; }
        public long ElapsedMs { get; set; }
    }

    public class ParameterWithOptionsDto
    {
        public int param_id { get; set; }
        public int datasource_id { get; set; }
        public string param_name { get; set; } = string.Empty;
        public string label { get; set; } = string.Empty;
        public string param_type { get; set; } = "text";
        public string? default_value { get; set; }
        public int display_order { get; set; }
        public List<LookupOptionDto> options { get; set; } = new();
    }

    public class LookupOptionDto
    {
        public string Value { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// Madde 2.4, 2.5, 4, 5, 9, 10:
    /// C# tarafında hiçbir DTO sınıfı tanımlamaya gerek kalmadan MariaDB üzerinde
    /// güvenli (Salt-Okunur + Parametrik) dinamik SQL çalıştıran ve şema çıkaran servis.
    /// </summary>
    public class DynamicQueryService
    {
        private sealed class RefCountedGate
        {
            public SemaphoreSlim Semaphore { get; } = new(1, 1);
            public int RefCount;
        }

        private readonly DbConnectionManager _connManager;
        private readonly BiDbConnectionManager _biConnManager;
        private readonly IMemoryCache _cache;
        private readonly QueryCacheOptions _cacheOptions;
        private readonly ConcurrentDictionary<int, long> _dataSourceVersions = new();
        private readonly ConcurrentDictionary<string, RefCountedGate> _inFlightLocks = new();
        private long _globalCacheVersion;

        public DynamicQueryService(
            DbConnectionManager connManager,
            BiDbConnectionManager biConnManager,
            IMemoryCache cache,
            IOptions<QueryCacheOptions> cacheOptions)
        {
            _connManager = connManager;
            _biConnManager = biConnManager;
            _cache = cache;
            _cacheOptions = cacheOptions?.Value ?? new QueryCacheOptions();
        }

        /// <summary>
        /// Bir veri kaynağı düzenlendiğinde veya pasife alındığında ilgili önbellek sürümünü artırarak eski önbelleği geçersiz kılar.
        /// </summary>
        public void InvalidateDataSourceCache(int datasourceId)
        {
            if (datasourceId <= 0) return;
            _dataSourceVersions.AddOrUpdate(datasourceId, 1, (_, ver) => ver + 1);
        }

        /// <summary>
        /// Bir veritabanı sunucusu bağlantı profilinin önbellek ayarları değiştiğinde tüm önbellek sürümünü geçersiz kılar.
        /// </summary>
        public void InvalidateAllDataSourceCaches()
        {
            Interlocked.Increment(ref _globalCacheVersion);
        }

        /// <summary>
        /// Kullanıcının yetkili olduğu dashboard ve datasource listesini (display_order sırasıyla) getirir.
        /// Madde 10 gereği kullanıcının custom_layout_json kaydı varsa onu, yoksa ana layout_json'u döner.
        /// </summary>
        public async Task<List<AuthorizedDashboardView>> GetAuthorizedDashboardsAsync(int userId)
        {
            await using DbConnection conn = await _biConnManager.OpenBiConnectionAsync();

            const string sql = @"
                SELECT d.dashboard_id,
                       d.code AS dashboard_code,
                       d.title,
                       d.description,
                       d.primary_datasource_id,
                       ds.code AS datasource_code,
                       ds.name AS datasource_name,
                       ds.category,
                       ud.display_order,
                       ud.refresh_seconds,
                       ud.slide_duration_seconds,
                       ud.can_edit_layout,
                       COALESCE(NULLIF(ud.custom_layout_json, ''), d.layout_json) AS effective_layout_json,
                       CASE WHEN ud.custom_layout_json IS NOT NULL AND ud.custom_layout_json <> '' THEN 1 ELSE 0 END AS has_custom_layout,
                       ud.last_params_json
                  FROM bi_user_dashboards ud
                  INNER JOIN bi_dashboards d ON d.dashboard_id = ud.dashboard_id AND d.is_active = 1
                  LEFT JOIN bi_datasources ds ON ds.datasource_id = d.primary_datasource_id
                 WHERE ud.user_id = @UserId
                   AND ud.can_view = 1
                 ORDER BY ud.display_order ASC, d.dashboard_id ASC;";

            var list = await conn.QueryAsync<AuthorizedDashboardView>(sql, new { UserId = userId });
            return list.ToList();
        }

        /// <summary>
        /// Kullanıcının ilgili datasource veya dashboard için görüntüleme yetkisi olup olmadığını kontrol eder (IDOR koruması).
        /// Hem dashboard'un ana veri kaynağını hem de dashboard bileşenlerinde (widgets) kullanılan ikincil sorguları doğrular.
        /// </summary>
        public async Task<bool> IsUserAuthorizedForDataSourceAsync(int userId, string userRole, int datasourceId)
        {
            if (string.Equals(userRole, "admin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(userRole, "designer", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            await using DbConnection conn = await _biConnManager.OpenBiConnectionAsync();

            const string sql = @"
                SELECT COUNT(1)
                  FROM bi_user_dashboards ud
                  INNER JOIN bi_dashboards d ON d.dashboard_id = ud.dashboard_id AND d.is_active = 1
                 WHERE ud.user_id = @UserId
                   AND ud.can_view = 1
                   AND (
                        d.primary_datasource_id = @DataSourceId
                        OR d.layout_json LIKE @DsPattern
                        OR ud.custom_layout_json LIKE @DsPattern
                   );";

            int count = await conn.ExecuteScalarAsync<int>(sql, new
            {
                UserId = userId,
                DataSourceId = datasourceId,
                DsPattern = $"%\"dataSourceId\":{datasourceId}%"
            });
            return count > 0;
        }

        /// <summary>
        /// Seçilen veri kaynağına ait filtre parametrelerini ve (varsa) açılır kutu seçeneklerini (lookup_sql) getirir.
        /// </summary>
        public async Task<string?> GetDataSourceNameAsync(int datasourceId)
        {
            await using DbConnection sysConn = await _biConnManager.OpenBiConnectionAsync();
            return await sysConn.ExecuteScalarAsync<string?>(
                "SELECT name FROM bi_datasources WHERE datasource_id = @DataSourceId LIMIT 1;",
                new { DataSourceId = datasourceId });
        }

        public async Task<List<ParameterWithOptionsDto>> GetDataSourceParametersAsync(int datasourceId)
        {
            await using DbConnection sysConn = await _biConnManager.OpenBiConnectionAsync();

            var ds = await sysConn.QueryFirstOrDefaultAsync<BiDataSource>(
                "SELECT * FROM bi_datasources WHERE datasource_id = @DataSourceId AND is_active = 1 LIMIT 1;",
                new { DataSourceId = datasourceId });
            if (ds == null)
            {
                return new List<ParameterWithOptionsDto>();
            }

            var targetConnInfo = _connManager.ResolveConnectionForQuery(ds.connection_id);
            if (!targetConnInfo.IsUsable)
            {
                return new List<ParameterWithOptionsDto>();
            }

            var parameters = (await sysConn.QueryAsync<BiDataSourceParam>(@"
                SELECT * FROM bi_datasource_params 
                 WHERE datasource_id = @DataSourceId 
                 ORDER BY display_order ASC, param_id ASC;",
                new { DataSourceId = datasourceId })).ToList();

            var result = new List<ParameterWithOptionsDto>();

            await using DbConnection targetConn = await _connManager.OpenTargetConnectionAsync(targetConnInfo.DbType, targetConnInfo.ConnectionString);

            foreach (var p in parameters)
            {
                var item = new ParameterWithOptionsDto
                {
                    param_id = p.param_id,
                    datasource_id = p.datasource_id,
                    param_name = p.param_name,
                    label = p.label,
                    param_type = p.param_type,
                    default_value = p.default_value,
                    display_order = p.display_order
                };

                if (!string.IsNullOrWhiteSpace(p.lookup_sql))
                {
                    var validation = SqlSecurityValidator.ValidateSelectQuery(p.lookup_sql);
                    if (validation.IsValid)
                    {
                        try
                        {
                            var rows = await targetConn.QueryAsync(p.lookup_sql);
                            foreach (IDictionary<string, object> row in rows)
                            {
                                var values = row.Values.ToList();
                                if (values.Count == 0) continue;
                                string val = values[0]?.ToString() ?? string.Empty;
                                string txt = values.Count > 1 ? (values[1]?.ToString() ?? val) : val;
                                if (!string.IsNullOrWhiteSpace(val))
                                {
                                    item.options.Add(new LookupOptionDto { Value = val, Text = txt });
                                }
                            }
                        }
                        catch
                        {
                            // Lookup sorgusu hata verirse parametre metin/boş liste olarak çalışmaya devam eder
                        }
                    }
                }

                result.Add(item);
            }

            return result;
        }

        /// <summary>
        /// Kayıtlı bir veri kaynağını (bi_datasources) kendi seçili veritabanı bağlantısı (connection_id) üzerinde çalıştırır.
        /// cache_ttl_seconds &gt; 0 ise IMemoryCache + Single-Flight kilidi ile paralel isteklerde tek SQL çalıştırır.
        /// </summary>
        public async Task<DynamicQueryResult> ExecuteDataSourceAsync(
            int datasourceId,
            Dictionary<string, string?>? filterValues,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            await using DbConnection sysConn = await _biConnManager.OpenBiConnectionAsync();

            var ds = await sysConn.QueryFirstOrDefaultAsync<BiDataSource>(
                new CommandDefinition(
                    "SELECT * FROM bi_datasources WHERE datasource_id = @Id AND is_active = 1 LIMIT 1;",
                    new { Id = datasourceId },
                    cancellationToken: cancellationToken));

            if (ds == null)
            {
                return new DynamicQueryResult { Success = false, Message = "SQL sorgusu bulunamadı veya pasif." };
            }

            var connPolicy = _connManager.ResolveConnectionCachePolicy(ds.connection_id);
            int ttlSeconds = connPolicy.CacheEnabled ? Math.Clamp(ds.cache_ttl_seconds, 0, 86400) : 0;
            int timeoutSeconds = Math.Clamp(ds.command_timeout_seconds > 0 ? ds.command_timeout_seconds : 30, 5, 300);
            long ver = _dataSourceVersions.GetOrAdd(datasourceId, 0);
            long gVer = Interlocked.Read(ref _globalCacheVersion);
            string cacheKey = BuildDataSourceCacheKey(datasourceId, gVer, ver, filterValues);

            if (ttlSeconds > 0 && !forceRefresh && _cache.TryGetValue(cacheKey, out DynamicQueryResult? cached) && cached != null)
            {
                return new DynamicQueryResult
                {
                    Success = cached.Success,
                    Message = cached.Message,
                    Columns = cached.Columns,
                    Rows = cached.Rows,
                    FromCache = true,
                    CachedAtUtc = cached.CachedAtUtc,
                    CacheTtlSeconds = ttlSeconds,
                    ElapsedMs = 0
                };
            }

            var targetConnInfo = _connManager.ResolveConnectionForQuery(ds.connection_id);
            if (!targetConnInfo.IsUsable)
            {
                return new DynamicQueryResult { Success = false, Message = targetConnInfo.ErrorMessage };
            }

            RefCountedGate gate = AcquireGate(cacheKey);
            bool acquired = false;
            try
            {
                await gate.Semaphore.WaitAsync(cancellationToken);
                acquired = true;

                if (ttlSeconds > 0 && !forceRefresh && _cache.TryGetValue(cacheKey, out DynamicQueryResult? cachedAfterWait) && cachedAfterWait != null)
                {
                    return new DynamicQueryResult
                    {
                        Success = cachedAfterWait.Success,
                        Message = cachedAfterWait.Message,
                        Columns = cachedAfterWait.Columns,
                        Rows = cachedAfterWait.Rows,
                        FromCache = true,
                        CachedAtUtc = cachedAfterWait.CachedAtUtc,
                        CacheTtlSeconds = ttlSeconds,
                        ElapsedMs = 0
                    };
                }

                var sw = Stopwatch.StartNew();
                await using DbConnection targetConn = await _connManager.OpenTargetConnectionAsync(targetConnInfo.DbType, targetConnInfo.ConnectionString);
                var result = await ExecuteRawSelectQueryAsync(targetConn, ds.sql_query, filterValues, maxRows: null, commandTimeout: timeoutSeconds, cancellationToken: cancellationToken);
                sw.Stop();

                result.ElapsedMs = sw.ElapsedMilliseconds;
                result.CacheTtlSeconds = ttlSeconds;
                result.FromCache = false;

                if (result.Success && ttlSeconds > 0)
                {
                    int effectiveMaxRows = connPolicy.MaxCacheableRows > 0
                        ? connPolicy.MaxCacheableRows
                        : Math.Max(0, _cacheOptions.MaxCacheableRows);
                    long effectiveMaxEntryBytes = (connPolicy.MaxEntrySizeKB > 0
                        ? (long)connPolicy.MaxEntrySizeKB
                        : (long)Math.Max(0, _cacheOptions.MaxEntrySizeKB)) * 1024L;

                    long estimatedBytes = EstimateResultSizeBytes(result);
                    bool withinRowLimit = effectiveMaxRows == 0 || result.Rows.Count <= effectiveMaxRows;
                    bool withinByteLimit = effectiveMaxEntryBytes == 0 || estimatedBytes <= effectiveMaxEntryBytes;

                    if (withinRowLimit && withinByteLimit)
                    {
                        result.CachedAtUtc = DateTime.UtcNow;
                        var entryOpts = new MemoryCacheEntryOptions
                        {
                            Size = estimatedBytes,
                            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttlSeconds)
                        };
                        int slidingSec = connPolicy.SlidingExpirationSeconds > 0
                            ? connPolicy.SlidingExpirationSeconds
                            : Math.Max(0, _cacheOptions.DefaultSlidingExpirationSeconds);
                        if (slidingSec > 0 && slidingSec < ttlSeconds)
                        {
                            entryOpts.SlidingExpiration = TimeSpan.FromSeconds(slidingSec);
                        }
                        _cache.Set(cacheKey, result, entryOpts);
                    }
                }

                return result;
            }
            finally
            {
                if (acquired) gate.Semaphore.Release();
                ReleaseGate(cacheKey, gate);
            }
        }

        private RefCountedGate AcquireGate(string key)
        {
            while (true)
            {
                var g = _inFlightLocks.GetOrAdd(key, _ => new RefCountedGate());
                lock (g)
                {
                    if (g.RefCount >= 0)
                    {
                        g.RefCount++;
                        return g;
                    }
                }
                Thread.Yield();
            }
        }

        private void ReleaseGate(string key, RefCountedGate g)
        {
            lock (g)
            {
                g.RefCount--;
                if (g.RefCount == 0)
                {
                    g.RefCount = -1;
                    _inFlightLocks.TryRemove(new KeyValuePair<string, RefCountedGate>(key, g));
                }
            }
        }

        private static long EstimateResultSizeBytes(DynamicQueryResult result)
        {
            long bytes = 1024 + (result.Columns.Count * 64L);
            foreach (var row in result.Rows)
            {
                bytes += 64 + (row.Count * 40L);
                foreach (var kv in row)
                {
                    if (kv.Value is string s)
                    {
                        bytes += 24 + (s.Length * 2L);
                    }
                    else if (kv.Value != null)
                    {
                        bytes += 16;
                    }
                }
            }
            return Math.Max(1024L, bytes);
        }

        private static string BuildDataSourceCacheKey(int datasourceId, long globalVersion, long version, Dictionary<string, string?>? filterValues)
        {
            if (filterValues == null || filterValues.Count == 0)
            {
                return $"ds:{datasourceId}:g{globalVersion}:v{version}:no_filters";
            }

            var normalized = filterValues
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
                .Select(kv => new KeyValuePair<string, string>(
                    kv.Key.Trim().TrimStart('@').ToLowerInvariant(),
                    NormalizeFilterCacheValue(kv.Value)))
                .Where(kv => kv.Value.Length > 0)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}");

            string raw = string.Join("&", normalized);
            if (raw.Length == 0) return $"ds:{datasourceId}:g{globalVersion}:v{version}:no_filters";

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return $"ds:{datasourceId}:g{globalVersion}:v{version}:{Convert.ToHexString(hash)[..16]}";
        }

        private static string NormalizeFilterCacheValue(string? val)
        {
            if (string.IsNullOrWhiteSpace(val)) return string.Empty;
            string t = val.Trim();
            if (t == "ALL" || t == "Tümü") return string.Empty;
            return t;
        }

        /// <summary>
        /// Herhangi bir SELECT sorgusunu SqlSecurityValidator ile doğruladıktan sonra Dapper DynamicParameters ile çalıştırır
        /// ve C# DTO sınıfına ihtiyaç duymadan dinamik satır + kolon şeması döner.
        /// </summary>
        public async Task<DynamicQueryResult> ExecuteRawSelectQueryAsync(
            DbConnection conn,
            string sqlQuery,
            Dictionary<string, string?>? filterValues,
            int? maxRows = null,
            int commandTimeout = 30,
            CancellationToken cancellationToken = default)
        {
            var validation = SqlSecurityValidator.ValidateSelectQuery(sqlQuery);
            if (!validation.IsValid)
            {
                return new DynamicQueryResult { Success = false, Message = validation.ErrorMessage };
            }

            try
            {
                var dynamicParams = new DynamicParameters();
                var expectedParams = SqlSecurityValidator.ExtractParameters(sqlQuery);
                string effectiveSql = sqlQuery;

                // Sorguda geçen tüm @Parametre'leri bağla (gönderilmeyenler NULL olarak bağlanır)
                foreach (var paramName in expectedParams)
                {
                    string? rawVal = null;
                    if (filterValues != null)
                    {
                        var matchKey = filterValues.Keys.FirstOrDefault(k =>
                            k.TrimStart('@').Equals(paramName, StringComparison.OrdinalIgnoreCase));
                        if (matchKey != null)
                        {
                            rawVal = filterValues[matchKey];
                        }
                    }

                    if (string.IsNullOrWhiteSpace(rawVal) || rawVal == "ALL" || rawVal == "Tümü")
                    {
                        dynamicParams.Add(paramName, null);

                        // Eğer sorguda (@Param IS NULL OR ...) yazılmamışsa ve parametre boş (Tümü) ise
                        // WHERE YEAR(t) >= @Param gibi karşılaştırmaların 0 kayıt döndürmemesi için otomatik sarmala
                        bool hasNullHandling = System.Text.RegularExpressions.Regex.IsMatch(
                            effectiveSql,
                            $@"(@{System.Text.RegularExpressions.Regex.Escape(paramName)}\s+IS\s+NULL|(?:COALESCE|IFNULL)\s*\(\s*@{System.Text.RegularExpressions.Regex.Escape(paramName)}\b)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        if (!hasNullHandling)
                        {
                            effectiveSql = System.Text.RegularExpressions.Regex.Replace(
                                effectiveSql,
                                $@"(?<=\b(?:WHERE|AND|OR)\s+|\(\s*)([a-zA-Z0-9_.]+(?:\s*\([^()]*\))?)\s*(=|>=|<=|>|<|<>|!=|LIKE)\s*@{System.Text.RegularExpressions.Regex.Escape(paramName)}\b",
                                m => $"(@{paramName} IS NULL OR {m.Value})",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        }
                    }
                    else
                    {
                        dynamicParams.Add(paramName, rawVal.Trim());
                    }
                }

                int safeTimeout = Math.Clamp(commandTimeout, 5, 300);
                var cmd = new CommandDefinition(effectiveSql, dynamicParams, commandTimeout: safeTimeout, cancellationToken: cancellationToken);
                await using var reader = await conn.ExecuteReaderAsync(cmd);
                var rowsList = new List<Dictionary<string, object?>>();
                var columns = new List<ColumnSchemaInfo>();
                var seenCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string colName = reader.GetName(i);
                    if (string.IsNullOrWhiteSpace(colName)) colName = $"Col_{i + 1}";
                    if (seenCols.Add(colName))
                    {
                        columns.Add(new ColumnSchemaInfo
                        {
                            Name = colName,
                            DataType = InferColumnTypeFromClrType(reader.GetFieldType(i))
                        });
                    }
                }

                int count = 0;
                while (await reader.ReadAsync(cancellationToken))
                {
                    var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        string colName = reader.GetName(i);
                        object? val = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        dict[colName] = val;
                    }
                    rowsList.Add(dict);
                    count++;
                    if (maxRows.HasValue && count >= maxRows.Value)
                        break;
                }

                return new DynamicQueryResult
                {
                    Success = true,
                    Message = "Sorgu başarıyla çalıştırıldı.",
                    Columns = columns,
                    Rows = rowsList
                };
            }
            catch (OperationCanceledException)
            {
                return new DynamicQueryResult
                {
                    Success = false,
                    Message = "Sorgu isteği iptal edildi."
                };
            }
            catch (Exception ex)
            {
                return new DynamicQueryResult
                {
                    Success = false,
                    Message = $"Sorgu çalıştırma hatası: {ex.Message}"
                };
            }
        }

        private static string InferColumnTypeFromClrType(Type? clrType)
        {
            if (clrType == null) return "string";
            var t = Nullable.GetUnderlyingType(clrType) ?? clrType;
            if (t == typeof(sbyte) || t == typeof(byte) || t == typeof(short) || t == typeof(ushort) ||
                t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong) ||
                t == typeof(float) || t == typeof(double) || t == typeof(decimal))
            {
                return "number";
            }
            if (t == typeof(DateTime) || t == typeof(DateOnly))
            {
                return "date";
            }
            if (t == typeof(bool))
            {
                return "boolean";
            }
            return "string";
        }

        private static string InferColumnType(object value)
        {
            return value switch
            {
                sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => "number",
                DateTime or DateOnly => "date",
                bool => "boolean",
                _ => "string"
            };
        }
    }
}

