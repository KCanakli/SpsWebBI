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
        private readonly DatabaseInitializer _dbInitializer;

        public DbSettingsController(DbConnectionManager connManager, DatabaseInitializer dbInitializer)
        {
            _connManager = connManager;
            _dbInitializer = dbInitializer;
        }

        [HttpGet]
        public async Task<IActionResult> GetCurrentSettings()
        {
            var connections = _connManager.GetAllConnections();
            var settings = _connManager.GetCurrentSettings();
            var test = await _connManager.TestConnectionAsync(settings);

            return Ok(new
            {
                settings.Id,
                settings.EnvironmentName,
                settings.Description,
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
        public async Task<IActionResult> TestConnection([FromBody] DbSettingsRequest request)
        {
            if (!await IsAuthorizedToChangeDbAsync(request.AdminPassword))
            {
                return Unauthorized(new { success = false, message = "Güvenlik doğrulaması başarısız: Mevcut veritabanı aktif olduğu için Admin oturumu veya Admin şifresi gereklidir." });
            }

            var result = await _connManager.TestConnectionAsync(request);
            return Ok(new { success = result.Success, message = result.Message });
        }

        [HttpPost("save")]
        public async Task<IActionResult> SaveConnection([FromBody] DbSettingsRequest request)
        {
            if (!await IsAuthorizedToChangeDbAsync(request.AdminPassword))
            {
                return Unauthorized(new { success = false, message = "Güvenlik doğrulaması başarısız: Mevcut veritabanı aktif olduğu için Admin oturumu veya Admin şifresi gereklidir." });
            }

            var saveResult = await _connManager.SaveConnectionSettingsAsync(request);
            if (!saveResult.Success)
            {
                return BadRequest(new { success = false, message = saveResult.Message });
            }

            // Bağlantı Aktif olarak kaydedildiyse ilgili veritabanında sistem tablolarını (bi_*) doğrula / oluştur
            if (request.IsActive)
            {
                var initResult = await _dbInitializer.InitializeAsync();
                return Ok(new
                {
                    success = initResult.Success,
                    savedId = saveResult.SavedId,
                    connections = _connManager.GetAllConnections(),
                    message = initResult.Success
                        ? $"{saveResult.Message} {initResult.Message}"
                        : $"Bağlantı kaydedildi ancak şema kurulumunda uyarı: {initResult.Message}"
                });
            }

            return Ok(new
            {
                success = true,
                savedId = saveResult.SavedId,
                connections = _connManager.GetAllConnections(),
                message = saveResult.Message
            });
        }

        /// <summary>
        /// Siber Güvenlik Kuralı:
        /// 1. Kullanıcı zaten 'admin' rolüyle giriş yapmışsa izin ver.
        /// 2. Mevcut veritabanına bağlanılamıyorsa (ilk kurulum veya IP/şifre değişmişse) kurtarma/kurulum amacıyla izin ver.
        /// 3. Mevcut veritabanı aktifse ancak oturum yoksa, gönderilen AdminPassword'ü bi_users tablosundaki admin şifresiyle doğrula.
        /// </summary>
        private async Task<bool> IsAuthorizedToChangeDbAsync(string? adminPassword)
        {
            if (User?.Identity?.IsAuthenticated == true &&
                User.FindFirstValue(ClaimTypes.Role) == "admin")
            {
                return true;
            }

            var currentSettings = _connManager.GetCurrentSettings();
            var currentTest = await _connManager.TestConnectionAsync(currentSettings);

            // Mevcut DB'ye erişilemiyorsa (ilk kurulum veya bağlantı kopukluğu) ayar yapılmasına izin ver
            if (!currentTest.Success)
            {
                return true;
            }

            // Mevcut DB çalışıyorsa Admin şifresini doğrula
            if (string.IsNullOrWhiteSpace(adminPassword))
            {
                return false;
            }

            try
            {
                await using var conn = new MySqlConnection(_connManager.GetActiveConnectionString());
                await conn.OpenAsync();
                var adminUser = await conn.QueryFirstOrDefaultAsync<BiUser>(
                    "SELECT * FROM bi_users WHERE user_code = 'admin' AND is_active = 1 LIMIT 1;");

                if (adminUser == null)
                    return true; // Henüz admin kullanıcısı oluşmamışsa izin ver

                return PasswordHasher.VerifyPassword(adminPassword, adminUser.password_hash);
            }
            catch
            {
                return true;
            }
        }
    }
}

