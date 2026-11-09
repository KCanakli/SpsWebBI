using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
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
    public class SaveUserRequest
    {
        public int? UserId { get; set; }
        public string UserCode { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string? Password { get; set; }
        public string Role { get; set; } = "viewer";
        public bool AllowPasswordless { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class UserPermissionItemDto
    {
        public int DashboardId { get; set; }
        public string DashboardCode { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string DataSourceName { get; set; } = string.Empty;
        public bool CanView { get; set; }
        public bool CanEditLayout { get; set; } = true;
        public int DisplayOrder { get; set; } = 1;
        public int RefreshSeconds { get; set; } = 60;
        public int SlideDurationSeconds { get; set; } = 30;
    }

    public class SavePermissionsRequest
    {
        public List<UserPermissionItemDto> Permissions { get; set; } = new();
    }

    [ApiController]
    [Route("api/admin")]
    [Authorize]
    public class AdminController : ControllerBase
    {
        private readonly DbConnectionManager _connManager;
        private readonly AuthService _authService;

        public AdminController(DbConnectionManager connManager, AuthService authService)
        {
            _connManager = connManager;
            _authService = authService;
        }

        private string CurrentUserCode => User.FindFirstValue("UserCode") ?? string.Empty;
        private string CurrentUserRole => User.FindFirstValue(ClaimTypes.Role) ?? "viewer";
        private string CurrentAuthMethod => User.FindFirstValue("AuthMethod") ?? "Password";

        private bool IsDesignerOrAdmin()
        {
            if (CurrentAuthMethod == "Passwordless") return false;
            return string.Equals(CurrentUserRole, "admin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(CurrentUserRole, "designer", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsAdminOnly()
        {
            if (CurrentAuthMethod == "Passwordless") return false;
            return string.Equals(CurrentUserRole, "admin", StringComparison.OrdinalIgnoreCase);
        }

        [HttpGet("datasources")]
        public async Task<IActionResult> GetAllDataSources()
        {
            if (!IsDesignerOrAdmin()) return Forbid();

            await using var conn = new MySqlConnection(_connManager.GetActiveConnectionString());
            await conn.OpenAsync();

            var sources = (await conn.QueryAsync<BiDataSource>("SELECT * FROM bi_datasources ORDER BY datasource_id ASC;")).ToList();
            var allParams = (await conn.QueryAsync<BiDataSourceParam>("SELECT * FROM bi_datasource_params ORDER BY display_order ASC;")).ToList();
            var connections = _connManager.GetAllConnections();

            var result = sources.Select(ds => new
            {
                ds.datasource_id,
                ds.code,
                ds.name,
                ds.category,
                ds.connection_id,
                ds.sql_query,
                ds.columns_schema_json,
                ds.is_active,
                parameters = allParams.Where(p => p.datasource_id == ds.datasource_id).ToList()
            });

            return Ok(new { success = true, datasources = result, connections });
        }

        [HttpGet("dashboards")]
        public async Task<IActionResult> GetAllDashboards()
        {
            if (!IsDesignerOrAdmin()) return Forbid();

            await using var conn = new MySqlConnection(_connManager.GetActiveConnectionString());
            await conn.OpenAsync();

            var dashboards = await conn.QueryAsync<BiDashboard>("SELECT * FROM bi_dashboards ORDER BY dashboard_id ASC;");
            return Ok(new { success = true, dashboards });
        }

        [HttpGet("users")]
        public async Task<IActionResult> GetAllUsers()
        {
            if (!IsAdminOnly()) return Forbid();

            await using var conn = new MySqlConnection(_connManager.GetActiveConnectionString());
            await conn.OpenAsync();

            var users = await conn.QueryAsync<BiUser>("SELECT * FROM bi_users ORDER BY user_id ASC;");
            var list = users.Select(u =>
            {
                string token = UserTokenProtector.CreateToken(u.user_code);
                return new
                {
                    u.user_id,
                    u.user_code,
                    u.full_name,
                    u.role,
                    u.allow_passwordless,
                    u.is_active,
                    u.failed_login_count,
                    u.locked_until,
                    encryptedToken = token,
                    tvSlideUrl = $"/?User={token}&Security=0&Mode=Slayt",
                    exeDirectUrl = $"/?User={token}&Security=0",
                    pinRequiredUrl = $"/?User={token}&Security=1"
                };
            });

            return Ok(new { success = true, users = list });
        }

        [HttpPost("users/save")]
        public async Task<IActionResult> SaveUser([FromBody] SaveUserRequest request)
        {
            if (!IsAdminOnly()) return Forbid();

            if (string.IsNullOrWhiteSpace(request.UserCode) || string.IsNullOrWhiteSpace(request.FullName))
            {
                return BadRequest(new { success = false, message = "Kullanıcı kodu ve ad soyad zorunludur." });
            }

            await using var conn = new MySqlConnection(_connManager.GetActiveConnectionString());
            await conn.OpenAsync();

            int userId = request.UserId ?? 0;
            if (userId > 0)
            {
                if (!string.IsNullOrWhiteSpace(request.Password))
                {
                    string hash = PasswordHasher.HashPassword(request.Password);
                    await conn.ExecuteAsync(@"
                        UPDATE bi_users
                           SET user_code = @Code,
                               full_name = @FullName,
                               password_hash = @Hash,
                               role = @Role,
                               allow_passwordless = @AllowPwdless,
                               is_active = @IsActive,
                               failed_login_count = 0,
                               locked_until = NULL
                         WHERE user_id = @Id;",
                        new
                        {
                            Id = userId,
                            Code = request.UserCode.Trim(),
                            FullName = request.FullName.Trim(),
                            Hash = hash,
                            Role = request.Role.Trim(),
                            AllowPwdless = request.AllowPasswordless ? 1 : 0,
                            IsActive = request.IsActive ? 1 : 0
                        });
                }
                else
                {
                    await conn.ExecuteAsync(@"
                        UPDATE bi_users
                           SET user_code = @Code,
                               full_name = @FullName,
                               role = @Role,
                               allow_passwordless = @AllowPwdless,
                               is_active = @IsActive,
                               failed_login_count = 0,
                               locked_until = NULL
                         WHERE user_id = @Id;",
                        new
                        {
                            Id = userId,
                            Code = request.UserCode.Trim(),
                            FullName = request.FullName.Trim(),
                            Role = request.Role.Trim(),
                            AllowPwdless = request.AllowPasswordless ? 1 : 0,
                            IsActive = request.IsActive ? 1 : 0
                        });
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(request.Password))
                {
                    return BadRequest(new { success = false, message = "Yeni kullanıcı için şifre / PIN belirlenmesi zorunludur." });
                }

                string hash = PasswordHasher.HashPassword(request.Password);
                userId = await conn.ExecuteScalarAsync<int>(@"
                    INSERT INTO bi_users (user_code, full_name, password_hash, role, allow_passwordless, is_active)
                    VALUES (@Code, @FullName, @Hash, @Role, @AllowPwdless, @IsActive);
                    SELECT LAST_INSERT_ID();",
                    new
                    {
                        Code = request.UserCode.Trim(),
                        FullName = request.FullName.Trim(),
                        Hash = hash,
                        Role = request.Role.Trim(),
                        AllowPwdless = request.AllowPasswordless ? 1 : 0,
                        IsActive = request.IsActive ? 1 : 0
                    });
            }

            string? ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            await _authService.WriteAuditLogAsync(conn, CurrentUserCode, "USER_SAVE", ip, $"Kullanıcı kaydedildi: {request.UserCode} (ID={userId})");

            return Ok(new { success = true, userId, message = "Kullanıcı bilgileri başarıyla kaydedildi." });
        }

        [HttpGet("users/{userId:int}/permissions")]
        public async Task<IActionResult> GetUserPermissions(int userId)
        {
            if (!IsAdminOnly()) return Forbid();

            await using var conn = new MySqlConnection(_connManager.GetActiveConnectionString());
            await conn.OpenAsync();

            const string sql = @"
                SELECT d.dashboard_id AS DashboardId,
                       d.code AS DashboardCode,
                       d.title AS Title,
                       ds.name AS DataSourceName,
                       COALESCE(ud.can_view, 0) AS CanView,
                       COALESCE(ud.can_edit_layout, 1) AS CanEditLayout,
                       COALESCE(ud.display_order, 1) AS DisplayOrder,
                       COALESCE(ud.refresh_seconds, 60) AS RefreshSeconds,
                       COALESCE(ud.slide_duration_seconds, 30) AS SlideDurationSeconds
                  FROM bi_dashboards d
                  INNER JOIN bi_datasources ds ON ds.datasource_id = d.primary_datasource_id
                  LEFT JOIN bi_user_dashboards ud ON ud.dashboard_id = d.dashboard_id AND ud.user_id = @UserId
                 WHERE d.is_active = 1
                 ORDER BY COALESCE(ud.display_order, 999) ASC, d.dashboard_id ASC;";

            var list = await conn.QueryAsync<UserPermissionItemDto>(sql, new { UserId = userId });
            return Ok(new { success = true, permissions = list });
        }

        /// <summary>
        /// Madde 6: Kullanıcı bazlı olarak dashboard erişim yetkisi, gösterim önceliği (display_order),
        /// refresh süresi (refresh_seconds) ve ekranda kalma süresini (slide_duration_seconds) kaydeder.
        /// </summary>
        [HttpPost("users/{userId:int}/permissions")]
        public async Task<IActionResult> SaveUserPermissions(int userId, [FromBody] SavePermissionsRequest request)
        {
            if (!IsAdminOnly()) return Forbid();

            await using var conn = new MySqlConnection(_connManager.GetActiveConnectionString());
            await conn.OpenAsync();

            foreach (var item in request.Permissions)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO bi_user_dashboards (user_id, dashboard_id, can_view, can_edit_layout, display_order, refresh_seconds, slide_duration_seconds)
                    VALUES (@UserId, @DashId, @CanView, @CanEdit, @Order, @RefreshSec, @SlideSec)
                    ON DUPLICATE KEY UPDATE
                        can_view = @CanView,
                        can_edit_layout = @CanEdit,
                        display_order = @Order,
                        refresh_seconds = @RefreshSec,
                        slide_duration_seconds = @SlideSec;",
                    new
                    {
                        UserId = userId,
                        DashId = item.DashboardId,
                        CanView = item.CanView ? 1 : 0,
                        CanEdit = item.CanEditLayout ? 1 : 0,
                        Order = item.DisplayOrder > 0 ? item.DisplayOrder : 1,
                        RefreshSec = item.RefreshSeconds >= 5 ? item.RefreshSeconds : 60,
                        SlideSec = item.SlideDurationSeconds >= 5 ? item.SlideDurationSeconds : 30
                    });
            }

            string? ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            await _authService.WriteAuditLogAsync(conn, CurrentUserCode, "PERMISSIONS_SAVE", ip, $"User ID={userId} dashboard yetki ve süreleri güncellendi.");

            return Ok(new { success = true, message = "Kullanıcı dashboard yetkileri, gösterim sırası ve süreleri kaydedildi." });
        }
    }
}

