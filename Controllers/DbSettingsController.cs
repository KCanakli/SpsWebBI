using System.Security.Claims;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SpsWebBI.Data;
using SpsWebBI.Models;
using SpsWebBI.Security;
using SpsWebBI.Services;

namespace SpsWebBI.Controllers
{
    public class DbSettingsRequest : DbConnectionInfoDto
    {
        /// <summary>
        /// Oturum açık değilken bağlantı ayarlarını değiştirmek için gereken Admin şifresi (mevcut DB çalışıyorsa kontrol edilir).
        /// </summary>
        public string? AdminPassword { get; set; }
    }

    [ApiController]
    [Route("api/db-settings")]
    public class DbSettingsController : ControllerBase
    {
        private readonly DbConnectionManager _connManager;
        private readonly DynamicQueryService _queryService;

        public DbSettingsController(DbConnectionManager connManager, DynamicQueryService queryService)
        {
            _connManager = connManager;
            _queryService = queryService;
        }

        private bool IsAuthenticatedAdmin()
        {
            if (User?.Identity?.IsAuthenticated != true) return false;
            var authMethod = User.FindFirstValue("AuthMethod") ?? "Password";
            if (authMethod == "Passwordless") return false;
            var role = User.FindFirstValue(ClaimTypes.Role) ?? "";
            return string.Equals(role, "admin", System.StringComparison.OrdinalIgnoreCase);
        }

        [HttpGet]
        public async Task<IActionResult> GetCurrentSettings()
        {
            if (!IsAuthenticatedAdmin())
            {
                return Unauthorized(new { success = false, message = "Veri kaynağı ayarlarına yalnızca Admin kullanıcısı erişebilir." });
            }

            var connections = _connManager.GetAllConnections();
            var settings = _connManager.GetCurrentSettings();
            var test = await _connManager.TestConnectionAsync(settings);

            return Ok(new
            {
                settings.Id,
                settings.EnvironmentName,
                settings.Description,
                settings.DbType,
                settings.SqliteFilePath,
                settings.IsActive,
                settings.Server,
                settings.Port,
                settings.Database,
                settings.UserId,
                settings.HasSavedPassword,
                settings.CacheEnabled,
                settings.MaxCacheableRows,
                settings.MaxEntrySizeKB,
                settings.SlidingExpirationSeconds,
                IsConnected = test.Success,
                StatusMessage = test.Message,
                Connections = connections
            });
        }

        [HttpPost("test")]
        public async Task<IActionResult> TestConnection([FromBody] DbSettingsRequest request)
        {
            if (!IsAuthenticatedAdmin())
            {
                return Unauthorized(new { success = false, message = "Veri kaynağı ayarlarını test etmek için Admin oturumu gereklidir." });
            }

            var result = await _connManager.TestConnectionAsync(request);
            return Ok(new { success = result.Success, message = result.Message });
        }

        [HttpPost("save")]
        public async Task<IActionResult> SaveConnection([FromBody] DbSettingsRequest request)
        {
            if (!IsAuthenticatedAdmin())
            {
                return Unauthorized(new { success = false, message = "Veri kaynağı ayarlarını değiştirmek için Admin oturumu gereklidir." });
            }

            var saveResult = await _connManager.SaveConnectionSettingsAsync(request);
            if (!saveResult.Success)
            {
                return BadRequest(new { success = false, message = saveResult.Message });
            }

            _queryService.InvalidateAllDataSourceCaches();

            return Ok(new
            {
                success = true,
                savedId = saveResult.SavedId,
                connections = _connManager.GetAllConnections(),
                message = saveResult.Message
            });
        }

        [HttpPost("delete/{id}")]
        public IActionResult DeleteConnection(string id)
        {
            if (!IsAuthenticatedAdmin())
            {
                return Unauthorized(new { success = false, message = "Bağlantı profilini silmek için Admin oturumu gereklidir." });
            }

            var result = _connManager.DeleteConnection(id);
            if (!result.Success)
            {
                return BadRequest(new { success = false, message = result.Message });
            }

            _queryService.InvalidateAllDataSourceCaches();

            return Ok(new
            {
                success = true,
                message = result.Message,
                connections = _connManager.GetAllConnections()
            });
        }
    }
}

