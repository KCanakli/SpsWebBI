using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using SpsWebBI.Models;
using SpsWebBI.Security;

namespace SpsWebBI.Services
{
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
        private readonly DbConnectionManager _connManager;
        private readonly BiDbConnectionManager _biConnManager;

        public DynamicQueryService(DbConnectionManager connManager, BiDbConnectionManager biConnManager)
        {
            _connManager = connManager;
            _biConnManager = biConnManager;
        }

        /// <summary>
        /// Kullanıcının yetkili olduğu dashboard ve datasource listesini (display_order sırasıyla) getirir.
        /// Madde 10 gereği kullanıcının custom_layout_json kaydı varsa onu, yoksa ana layout_json'u döner.
        /// </summary>
        public async Task<List<AuthorizedDashboardView>> GetAuthorizedDashboardsAsync(int userId)
        {
            await using var conn = new MySqlConnection(_biConnManager.GetBiConnectionString());
            await conn.OpenAsync();

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
                       CASE WHEN ud.custom_layout_json IS NOT NULL AND ud.custom_layout_json <> '' THEN 1 ELSE 0 END AS has_custom_layout
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

            await using var conn = new MySqlConnection(_biConnManager.GetBiConnectionString());
            await conn.OpenAsync();

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
        public async Task<List<ParameterWithOptionsDto>> GetDataSourceParametersAsync(int datasourceId)
        {
            await using var sysConn = new MySqlConnection(_biConnManager.GetBiConnectionString());
            await sysConn.OpenAsync();

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

            await using var targetConn = new MySqlConnection(targetConnInfo.ConnectionString);
            await targetConn.OpenAsync();

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
        /// </summary>
        public async Task<DynamicQueryResult> ExecuteDataSourceAsync(
            int datasourceId,
            Dictionary<string, string?>? filterValues)
        {
            await using var sysConn = new MySqlConnection(_biConnManager.GetBiConnectionString());
            await sysConn.OpenAsync();

            var ds = await sysConn.QueryFirstOrDefaultAsync<BiDataSource>(
                "SELECT * FROM bi_datasources WHERE datasource_id = @Id AND is_active = 1 LIMIT 1;",
                new { Id = datasourceId });

            if (ds == null)
            {
                return new DynamicQueryResult { Success = false, Message = "SQL sorgusu bulunamadı veya pasif." };
            }

            var targetConnInfo = _connManager.ResolveConnectionForQuery(ds.connection_id);
            if (!targetConnInfo.IsUsable)
            {
                return new DynamicQueryResult { Success = false, Message = targetConnInfo.ErrorMessage };
            }

            await using var targetConn = new MySqlConnection(targetConnInfo.ConnectionString);
            await targetConn.OpenAsync();

            return await ExecuteRawSelectQueryAsync(targetConn, ds.sql_query, filterValues);
        }

        /// <summary>
        /// Herhangi bir SELECT sorgusunu SqlSecurityValidator ile doğruladıktan sonra Dapper DynamicParameters ile çalıştırır
        /// ve C# DTO sınıfına ihtiyaç duymadan dinamik satır + kolon şeması döner.
        /// </summary>
        public async Task<DynamicQueryResult> ExecuteRawSelectQueryAsync(
            MySqlConnection conn,
            string sqlQuery,
            Dictionary<string, string?>? filterValues,
            int? maxRows = null)
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

                await using var reader = await conn.ExecuteReaderAsync(effectiveSql, dynamicParams, commandTimeout: 30);
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
                while (await reader.ReadAsync())
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

