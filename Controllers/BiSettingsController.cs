using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using SpsWebBI.Data;
using SpsWebBI.Services;

namespace SpsWebBI.Controllers
{
    public class BiSettingsSaveRequest : DbConnectionInfoDto
    {
        /// <summary>
        /// Oturum açık değilken BI ayarlarını değiştirmek için gereken Admin şifresi (mevcut sistem çalışıyorsa doğrulanır).
        /// </summary>
        public string? AdminPassword { get; set; }
    }

    [ApiController]
    [Route("api/bi-settings")]
    public class BiSettingsController : ControllerBase
    {
        private readonly BiDbConnectionManager _biConnManager;
        private readonly DatabaseInitializer _dbInitializer;
        private readonly AuthService _authService;

        public BiSettingsController(
            BiDbConnectionManager biConnManager,
            DatabaseInitializer dbInitializer,
            AuthService authService)
        {
            _biConnManager = biConnManager;
            _dbInitializer = dbInitializer;
            _authService = authService;
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
        public async Task<IActionResult> GetSettings()
        {
            bool hasAdmin = await _biConnManager.HasWorkingAdminUserAsync();
            bool isAdmin = IsAuthenticatedAdmin();

            var connections = _biConnManager.GetAllConnections();
            var settings = _biConnManager.GetCurrentSettings();
            var test = await _biConnManager.TestBiConnectionAsync(settings);

            return Ok(new
            {
                isInitialSetup = !hasAdmin,
                isAuthenticatedAdmin = isAdmin,
                requiresAdminPassword = hasAdmin && !isAdmin,
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
                IsConnected = test.Success,
                StatusMessage = test.Message,
                Connections = connections
            });
        }

        [HttpPost("test")]
        public async Task<IActionResult> TestConnection([FromBody] DbConnectionInfoDto dto)
        {
            var result = await _biConnManager.TestBiConnectionAsync(dto);
            return Ok(new { success = result.Success, message = result.Message });
        }

        [HttpPost("save")]
        public async Task<IActionResult> SaveConnection([FromBody] BiSettingsSaveRequest dto)
        {
            bool hasAdmin = await _biConnManager.HasWorkingAdminUserAsync();
            bool isAdmin = IsAuthenticatedAdmin();

            // Eğer sistemde çalışan bir veritabanı varsa ve oturum açık değilse admin şifresini doğrula
            if (hasAdmin && !isAdmin)
            {
                if (string.IsNullOrWhiteSpace(dto.AdminPassword))
                {
                    return Unauthorized(new { success = false, message = "Mevcut BI ayarlarını değiştirmek için Admin şifresi gereklidir." });
                }

                var authTest = await _authService.AuthenticateWithPasswordAsync("admin", dto.AdminPassword, HttpContext.Connection.RemoteIpAddress?.ToString());
                if (!authTest.Success)
                {
                    return Unauthorized(new { success = false, message = "Sistem Admin şifresi hatalı." });
                }
            }

            var saveResult = await _biConnManager.SaveBiConnectionSettingsAsync(dto);
            if (!saveResult.Success)
            {
                return BadRequest(new { success = false, message = saveResult.Message });
            }

            if (dto.IsActive)
            {
                var initResult = await _dbInitializer.InitializeAsync();
                return Ok(new
                {
                    success = initResult.Success,
                    savedId = saveResult.SavedId,
                    connections = _biConnManager.GetAllConnections(),
                    message = initResult.Success
                        ? $"{saveResult.Message} {initResult.Message}"
                        : $"Ayarlar kaydedildi ancak BI şema kurulumunda uyarı: {initResult.Message}"
                });
            }

            return Ok(new
            {
                success = true,
                savedId = saveResult.SavedId,
                connections = _biConnManager.GetAllConnections(),
                message = saveResult.Message
            });
        }

        [HttpPost("delete/{id}")]
        public async Task<IActionResult> DeleteConnection(string id, [FromBody] BiSettingsSaveRequest? dto)
        {
            bool hasAdmin = await _biConnManager.HasWorkingAdminUserAsync();
            bool isAdmin = IsAuthenticatedAdmin();

            if (hasAdmin && !isAdmin)
            {
                if (string.IsNullOrWhiteSpace(dto?.AdminPassword))
                {
                    return Unauthorized(new { success = false, message = "BI bağlantı profilini silmek için Admin şifresi gereklidir." });
                }

                var authTest = await _authService.AuthenticateWithPasswordAsync("admin", dto.AdminPassword, HttpContext.Connection.RemoteIpAddress?.ToString());
                if (!authTest.Success)
                {
                    return Unauthorized(new { success = false, message = "Sistem Admin şifresi hatalı." });
                }
            }

            var result = _biConnManager.DeleteConnection(id);
            if (!result.Success)
            {
                return BadRequest(new { success = false, message = result.Message });
            }

            return Ok(new
            {
                success = true,
                message = result.Message,
                connections = _biConnManager.GetAllConnections()
            });
        }
    }
}
