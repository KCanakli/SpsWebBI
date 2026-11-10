using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SpsWebBI.Services;

namespace SpsWebBI.Controllers
{
    public class SaveLayoutRequest
    {
        public string LayoutJson { get; set; } = "{}";
    }

    public class SaveAdminDashboardRequest
    {
        public int? DashboardId { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int PrimaryDataSourceId { get; set; }
        public bool IsActive { get; set; } = true;
        public string LayoutJson { get; set; } = "{}";
    }

    [ApiController]
    [Route("api/dashboards")]
    [Authorize]
    public class DashboardController : ControllerBase
    {
        private readonly BiDbConnectionManager _biConnManager;
        private readonly AuthService _authService;

        public DashboardController(BiDbConnectionManager biConnManager, AuthService authService)
        {
            _biConnManager = biConnManager;
            _authService = authService;
        }

        private int CurrentUserId => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int id) ? id : 0;
        private string CurrentUserCode => User.FindFirstValue("UserCode") ?? string.Empty;
        private string CurrentUserRole => User.FindFirstValue(ClaimTypes.Role) ?? "viewer";
        private string CurrentAuthMethod => User.FindFirstValue("AuthMethod") ?? "Password";

        /// <summary>
        /// Madde 10: Son kullanıcının kendi yetkisinde olan dashboard üzerinde yaptığı tasarım/yerleşim
        /// değişikliklerini kendi hesabına (bi_user_dashboards.custom_layout_json) kaydeder.
        /// </summary>
        [HttpPost("{dashboardId:int}/user-layout")]
        public async Task<IActionResult> SaveUserCustomLayout(int dashboardId, [FromBody] SaveLayoutRequest request)
        {
            if (CurrentUserId <= 0) return Unauthorized();

            await using var conn = new MySqlConnection(_biConnManager.GetBiConnectionString());
            await conn.OpenAsync();

            var perm = await conn.QueryFirstOrDefaultAsync<SpsWebBI.Models.BiUserDashboard>(@"
                SELECT user_dashboard_id, can_view, can_edit_layout
                  FROM bi_user_dashboards
                 WHERE user_id = @UserId AND dashboard_id = @DashId LIMIT 1;",
                new { UserId = CurrentUserId, DashId = dashboardId });

            if (perm == null || !perm.can_view)
            {
                return Forbid();
            }

            if (!perm.can_edit_layout &&
                !string.Equals(CurrentUserRole, "admin", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { success = false, message = "Bu dashboard üzerinde kişisel düzenleme yetkiniz bulunmamaktadır." });
            }

            await conn.ExecuteAsync(@"
                UPDATE bi_user_dashboards
                   SET custom_layout_json = @LayoutJson
                 WHERE user_id = @UserId AND dashboard_id = @DashId;",
                new
                {
                    LayoutJson = request.LayoutJson,
                    UserId = CurrentUserId,
                    DashId = dashboardId
                });

            string? ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            await _authService.WriteAuditLogAsync(conn, CurrentUserCode, "USER_LAYOUT_SAVE", ip, $"Dashboard ID={dashboardId} kişisel layout kaydedildi.");

            return Ok(new
            {
                success = true,
                message = "Kişisel dashboard tasarımınız kaydedildi. Yeniden açtığınızda bu tasarım yüklenecektir."
            });
        }

        /// <summary>
        /// Son kullanıcının kendi yaptığı tasarım özelleştirmesini sıfırlayıp ana şablona (bi_dashboards.layout_json) dönmesini sağlar.
        /// </summary>
        [HttpDelete("{dashboardId:int}/user-layout")]
        public async Task<IActionResult> ResetUserCustomLayout(int dashboardId)
        {
            if (CurrentUserId <= 0) return Unauthorized();

            await using var conn = new MySqlConnection(_biConnManager.GetBiConnectionString());
            await conn.OpenAsync();

            await conn.ExecuteAsync(@"
                UPDATE bi_user_dashboards
                   SET custom_layout_json = NULL
                 WHERE user_id = @UserId AND dashboard_id = @DashId;",
                new { UserId = CurrentUserId, DashId = dashboardId });

            return Ok(new
            {
                success = true,
                message = "Tasarım varsayılan şablona sıfırlandı."
            });
        }

        /// <summary>
        /// Madde 8.6 & 9: Admin / Designer yetkisiyle ana dashboard tasarımını (bi_dashboards.layout_json) kaydeder.
        /// </summary>
        [HttpPost("save-master")]
        public async Task<IActionResult> SaveMasterDashboard([FromBody] SaveAdminDashboardRequest request)
        {
            if (CurrentAuthMethod == "Passwordless" ||
                (!string.Equals(CurrentUserRole, "admin", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(CurrentUserRole, "designer", StringComparison.OrdinalIgnoreCase)))
            {
                return Forbid();
            }

            if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Title))
            {
                return BadRequest(new { success = false, message = "Dashboard kodu ve başlığı zorunludur." });
            }

            await using var conn = new MySqlConnection(_biConnManager.GetBiConnectionString());
            await conn.OpenAsync();

            int dashId = request.DashboardId ?? 0;
            int isActiveBit = request.IsActive ? 1 : 0;
            if (dashId > 0)
            {
                await conn.ExecuteAsync(@"
                    UPDATE bi_dashboards
                       SET code = @Code,
                           title = @Title,
                           description = @Description,
                           primary_datasource_id = @DsId,
                           layout_json = @LayoutJson,
                           is_active = @IsActive,
                           updated_at = NOW()
                     WHERE dashboard_id = @Id;",
                    new
                    {
                        Id = dashId,
                        Code = request.Code.Trim(),
                        Title = request.Title.Trim(),
                        Description = request.Description,
                        DsId = request.PrimaryDataSourceId,
                        LayoutJson = request.LayoutJson,
                        IsActive = isActiveBit
                    });
            }
            else
            {
                dashId = await conn.ExecuteScalarAsync<int>(@"
                    INSERT INTO bi_dashboards (code, title, description, primary_datasource_id, layout_json, is_active)
                    VALUES (@Code, @Title, @Description, @DsId, @LayoutJson, @IsActive);
                    SELECT LAST_INSERT_ID();",
                    new
                    {
                        Code = request.Code.Trim(),
                        Title = request.Title.Trim(),
                        Description = request.Description,
                        DsId = request.PrimaryDataSourceId,
                        LayoutJson = request.LayoutJson,
                        IsActive = isActiveBit
                    });

                // Oluşturan kullanıcıya otomatik yetki ver
                await conn.ExecuteAsync(@"
                    INSERT IGNORE INTO bi_user_dashboards (user_id, dashboard_id, can_view, can_edit_layout, display_order, refresh_seconds, slide_duration_seconds)
                    VALUES (@Uid, @DashId, 1, 1, 10, 60, 30);",
                    new { Uid = CurrentUserId, DashId = dashId });
            }

            string? ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            await _authService.WriteAuditLogAsync(conn, CurrentUserCode, "MASTER_DASHBOARD_SAVE", ip, $"Ana Dashboard ID={dashId} ({request.Code}) kaydedildi.");

            return Ok(new
            {
                success = true,
                dashboardId = dashId,
                message = "Ana dashboard tasarımı MariaDB'ye kaydedildi."
            });
        }

        /// <summary>
        /// Seçilen dashboard'u veritabanından fiziksel olarak silmeden pasif (is_active = 0) hale getirir.
        /// </summary>
        [HttpPost("{dashboardId:int}/deactivate")]
        public async Task<IActionResult> DeactivateDashboard(int dashboardId)
        {
            if (CurrentAuthMethod == "Passwordless" ||
                (!string.Equals(CurrentUserRole, "admin", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(CurrentUserRole, "designer", StringComparison.OrdinalIgnoreCase)))
            {
                return Forbid();
            }

            if (dashboardId <= 0)
            {
                return BadRequest(new { success = false, message = "Geçersiz dashboard seçimi." });
            }

            await using var conn = new MySqlConnection(_biConnManager.GetBiConnectionString());
            await conn.OpenAsync();

            int affected = await conn.ExecuteAsync(@"
                UPDATE bi_dashboards
                   SET is_active = 0,
                       updated_at = NOW()
                 WHERE dashboard_id = @Id;",
                new { Id = dashboardId });

            if (affected <= 0)
            {
                return NotFound(new { success = false, message = "Dashboard bulunamadı." });
            }

            string? ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            await _authService.WriteAuditLogAsync(conn, CurrentUserCode, "DASHBOARD_DEACTIVATE", ip, $"Dashboard ID={dashboardId} pasif hale getirildi.");

            return Ok(new
            {
                success = true,
                dashboardId,
                message = "Dashboard pasif hale getirildi."
            });
        }
    }
}
