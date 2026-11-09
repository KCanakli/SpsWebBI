using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SpsWebBI.Models;
using SpsWebBI.Security;
using SpsWebBI.Services;

namespace SpsWebBI.Controllers
{
    public class ExecuteQueryRequest
    {
        public Dictionary<string, string?>? Filters { get; set; }
    }

    public class PreviewSqlRequest
    {
        public string SqlQuery { get; set; } = string.Empty;
        public Dictionary<string, string?>? SampleFilters { get; set; }
    }

    public class SaveDataSourceRequest
    {
        public int? DataSourceId { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Category { get; set; }
        public string SqlQuery { get; set; } = string.Empty;
        public List<BiDataSourceParam>? Parameters { get; set; }
    }

    [ApiController]
    [Route("api/datasources")]
    [Authorize]
    public class DataSourceController : ControllerBase
    {
        private readonly DynamicQueryService _queryService;
        private readonly DbConnectionManager _connManager;

        public DataSourceController(DynamicQueryService queryService, DbConnectionManager connManager)
        {
            _queryService = queryService;
            _connManager = connManager;
        }

        private int CurrentUserId => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int id) ? id : 0;
        private string CurrentUserRole => User.FindFirstValue(ClaimTypes.Role) ?? "viewer";
        private string CurrentAuthMethod => User.FindFirstValue("AuthMethod") ?? "Password";

        /// <summary>
        /// Oturumdaki kullanıcının yetkili olduğu tüm dashboard ve datasource listesini (display_order sırasıyla) döner.
        /// </summary>
        [HttpGet("authorized-dashboards")]
        public async Task<IActionResult> GetAuthorizedDashboards()
        {
            if (CurrentUserId <= 0) return Unauthorized();

            var dashboards = await _queryService.GetAuthorizedDashboardsAsync(CurrentUserId);
            return Ok(new
            {
                success = true,
                dashboards
            });
        }

        /// <summary>
        /// Seçilen datasource için tanımlı filtre parametrelerini ve açılır liste (lookup) verilerini döner.
        /// </summary>
        [HttpGet("{datasourceId:int}/parameters")]
        public async Task<IActionResult> GetParameters(int datasourceId)
        {
            if (!await _queryService.IsUserAuthorizedForDataSourceAsync(CurrentUserId, CurrentUserRole, datasourceId))
            {
                return Forbid();
            }

            var parameters = await _queryService.GetDataSourceParametersAsync(datasourceId);
            return Ok(new
            {
                success = true,
                parameters
            });
        }

        /// <summary>
        /// Seçilen datasource'un SQL sorgusunu kullanıcının girdiği filtre parametreleriyle çalıştırır.
        /// </summary>
        [HttpPost("{datasourceId:int}/execute")]
        public async Task<IActionResult> ExecuteDataSource(int datasourceId, [FromBody] ExecuteQueryRequest? request)
        {
            if (!await _queryService.IsUserAuthorizedForDataSourceAsync(CurrentUserId, CurrentUserRole, datasourceId))
            {
                return Forbid();
            }

            var result = await _queryService.ExecuteDataSourceAsync(datasourceId, request?.Filters);
            if (!result.Success)
            {
                return BadRequest(new { success = false, message = result.Message });
            }

            return Ok(new
            {
                success = true,
                columns = result.Columns,
                rows = result.Rows,
                totalRowCount = result.TotalRowCount
            });
        }

        /// <summary>
        /// Madde 2.4 & 2.5: Tasarım ekranında Admin'in yazdığı SQL sorgusunu doğrular, @Parametre'leri bulur ve ilk 100 satırı önizler.
        /// Güvenlik: Security=0 (Passwordless) ile girilmiş oturumlar bu ucu çağıramaz.
        /// </summary>
        [HttpPost("preview-sql")]
        public async Task<IActionResult> PreviewSql([FromBody] PreviewSqlRequest request)
        {
            if (!IsDesignerOrAdminWithPassword())
            {
                return Forbid();
            }

            var validation = SqlSecurityValidator.ValidateSelectQuery(request.SqlQuery);
            if (!validation.IsValid)
            {
                return BadRequest(new { success = false, message = validation.ErrorMessage });
            }

            var extractedParams = SqlSecurityValidator.ExtractParameters(request.SqlQuery);

            await using var conn = new MySqlConnection(_connManager.GetActiveConnectionString());
            await conn.OpenAsync();

            var result = await _queryService.ExecuteRawSelectQueryAsync(conn, request.SqlQuery, request.SampleFilters, maxRows: 100);
            if (!result.Success)
            {
                return BadRequest(new { success = false, message = result.Message, extractedParameters = extractedParams });
            }

            return Ok(new
            {
                success = true,
                extractedParameters = extractedParams,
                columns = result.Columns,
                previewRows = result.Rows
            });
        }

        /// <summary>
        /// Madde 2.4, 2.5, 4, 5, 9: Yeni bir Veri Kaynağını (SQL Sorgusu) ve filtre parametrelerini MariaDB'ye kaydeder.
        /// </summary>
        [HttpPost("save")]
        public async Task<IActionResult> SaveDataSource([FromBody] SaveDataSourceRequest request)
        {
            if (!IsDesignerOrAdminWithPassword())
            {
                return Forbid();
            }

            if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest(new { success = false, message = "Veri kaynağı kodu ve adı zorunludur." });
            }

            var validation = SqlSecurityValidator.ValidateSelectQuery(request.SqlQuery);
            if (!validation.IsValid)
            {
                return BadRequest(new { success = false, message = validation.ErrorMessage });
            }

            await using var conn = new MySqlConnection(_connManager.GetActiveConnectionString());
            await conn.OpenAsync();

            // Şemayı otomatik çıkar
            var testExec = await _queryService.ExecuteRawSelectQueryAsync(conn, request.SqlQuery, null, maxRows: 5);
            if (!testExec.Success)
            {
                return BadRequest(new { success = false, message = $"SQL doğrulama hatası: {testExec.Message}" });
            }

            string schemaJson = JsonSerializer.Serialize(testExec.Columns);
            int dsId = request.DataSourceId ?? 0;

            if (dsId > 0)
            {
                await conn.ExecuteAsync(@"
                    UPDATE bi_datasources
                       SET code = @Code,
                           name = @Name,
                           category = @Category,
                           sql_query = @SqlQuery,
                           columns_schema_json = @SchemaJson,
                           is_active = 1,
                           updated_at = NOW()
                     WHERE datasource_id = @Id;",
                    new
                    {
                        Id = dsId,
                        Code = request.Code.Trim(),
                        Name = request.Name.Trim(),
                        Category = request.Category?.Trim(),
                        SqlQuery = request.SqlQuery,
                        SchemaJson = schemaJson
                    });
            }
            else
            {
                dsId = await conn.ExecuteScalarAsync<int>(@"
                    INSERT INTO bi_datasources (code, name, category, sql_query, columns_schema_json, is_active)
                    VALUES (@Code, @Name, @Category, @SqlQuery, @SchemaJson, 1);
                    SELECT LAST_INSERT_ID();",
                    new
                    {
                        Code = request.Code.Trim(),
                        Name = request.Name.Trim(),
                        Category = request.Category?.Trim(),
                        SqlQuery = request.SqlQuery,
                        SchemaJson = schemaJson
                    });
            }

            if (request.Parameters != null)
            {
                await conn.ExecuteAsync("DELETE FROM bi_datasource_params WHERE datasource_id = @DsId;", new { DsId = dsId });
                int order = 1;
                foreach (var p in request.Parameters)
                {
                    if (string.IsNullOrWhiteSpace(p.param_name)) continue;
                    await conn.ExecuteAsync(@"
                        INSERT INTO bi_datasource_params (datasource_id, param_name, label, param_type, default_value, lookup_sql, display_order)
                        VALUES (@DsId, @ParamName, @Label, @ParamType, @DefaultValue, @LookupSql, @DisplayOrder);",
                        new
                        {
                            DsId = dsId,
                            ParamName = p.param_name.Trim().TrimStart('@'),
                            Label = string.IsNullOrWhiteSpace(p.label) ? p.param_name.Trim() : p.label.Trim(),
                            ParamType = string.IsNullOrWhiteSpace(p.param_type) ? "text" : p.param_type.Trim(),
                            DefaultValue = p.default_value,
                            LookupSql = p.lookup_sql,
                            DisplayOrder = p.display_order > 0 ? p.display_order : order++
                        });
                }
            }

            return Ok(new
            {
                success = true,
                datasourceId = dsId,
                columns = testExec.Columns,
                message = "Veri kaynağı ve parametreleri başarıyla kaydedildi."
            });
        }

        /// <summary>
        /// Seçilen veri kaynağını veritabanından fiziksel olarak silmeden pasif (is_active = 0) hale getirir.
        /// </summary>
        [HttpPost("{datasourceId:int}/deactivate")]
        public async Task<IActionResult> DeactivateDataSource(int datasourceId)
        {
            if (!IsDesignerOrAdminWithPassword())
            {
                return Forbid();
            }

            if (datasourceId <= 0)
            {
                return BadRequest(new { success = false, message = "Geçersiz veri kaynağı seçimi." });
            }

            await using var conn = new MySqlConnection(_connManager.GetActiveConnectionString());
            await conn.OpenAsync();

            int affected = await conn.ExecuteAsync(@"
                UPDATE bi_datasources
                   SET is_active = 0,
                       updated_at = NOW()
                 WHERE datasource_id = @Id;",
                new { Id = datasourceId });

            if (affected <= 0)
            {
                return NotFound(new { success = false, message = "Veri kaynağı bulunamadı." });
            }

            return Ok(new
            {
                success = true,
                datasourceId,
                message = "Veri kaynağı pasif hale getirildi."
            });
        }

        private bool IsDesignerOrAdminWithPassword()
        {
            if (CurrentAuthMethod == "Passwordless") return false;
            return string.Equals(CurrentUserRole, "admin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(CurrentUserRole, "designer", StringComparison.OrdinalIgnoreCase);
        }
    }
}

