using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using SpsWebBI.Models;
using SpsWebBI.Security;
using SpsWebBI.Services;

namespace SpsWebBI.Controllers
{
    public class LoginRequest
    {
        public string UserCode { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string? Mode { get; set; }
    }

    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly AuthService _authService;

        public AuthController(AuthService authService)
        {
            _authService = authService;
        }

        /// <summary>
        /// Madde 7 ve Madde 8 (8.1, 8.2, 8.3, 8.4, 8.5) Açılış Parametrelerini Değerlendirir.
        /// Siber Güvenlik: Security=0 ile şifresiz giriş yapılabilmesi için User parametresinin
        /// UserTokenProtector ile üretilmiş şifreli/imzalı bir token (UT1_...) olması zorunludur.
        /// Düz metin kullanıcı adı yazılırsa şifresiz giriş reddedilir ve Şifre/PIN sorulur.
        /// </summary>
        [HttpGet("evaluate-entry")]
        public async Task<IActionResult> EvaluateEntry(
            [FromQuery(Name = "User")] string? user,
            [FromQuery(Name = "Security")] string? security,
            [FromQuery(Name = "Mode")] string? mode)
        {
            string? ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            string normalizedMode = NormalizeMode(mode);

            // 1. User parametresi gönderilmişse (Madde 8.1, 8.2, 8.3, 8.4)
            if (!string.IsNullOrWhiteSpace(user))
            {
                string trimmedParam = user.Trim();

                // Security=0 ise yalnızca şifreli/imzalı token (UT1_...) ile şifresiz girişe izin ver
                if (security == "0")
                {
                    if (!UserTokenProtector.TryValidateAndExtractUserCode(trimmedParam, out string decryptedUserCode))
                    {
                        // Dışarıdan biri elle ?User=Ahmet&Security=0 yazmış -> Şifresiz girişi reddet, Şifre/PIN ekranına düşür!
                        return Ok(new
                        {
                            scenario = "8.4_PIN_REQUIRED",
                            requiresAuth = true,
                            lockUserCode = true,
                            userCode = trimmedParam,
                            mode = normalizedMode,
                            message = $"Güvenlik Uyarısı: '{trimmedParam}' için düz metin ile şifresiz giriş yapılamaz. Lütfen şifre / PIN giriniz."
                        });
                    }

                    var authResult = await _authService.AuthenticatePasswordlessAsync(decryptedUserCode, ip);
                    if (!authResult.Success || authResult.User == null)
                    {
                        return Ok(new
                        {
                            scenario = "8.4_PIN_REQUIRED",
                            requiresAuth = true,
                            lockUserCode = true,
                            userCode = decryptedUserCode,
                            mode = normalizedMode,
                            message = authResult.Message
                        });
                    }

                    // Oturuma gerçek user_id ve user_code bilgisini yaz (layout kaydetme vb. işlemler gerçek kullanıcı üzerinden yürür)
                    await SignInUserCookieAsync(authResult.User, authResult.AuthMethod);

                    // Madde 8.1: Mode=Slayt gönderildiyse doğrudan Slayt gösterim ekranı başlar
                    if (normalizedMode.Equals("Slayt", StringComparison.OrdinalIgnoreCase))
                    {
                        return Ok(new
                        {
                            scenario = "8.1_TV_SLIDESHOW",
                            requiresAuth = false,
                            action = "START_SLIDESHOW",
                            mode = "Slayt",
                            user = MapUserResponse(authResult.User, authResult.AuthMethod)
                        });
                    }

                    // Madde 8.2 & 8.3: Datasource listesi, filtreler ve Slayt/Otomatik Yenileme seçim ekranı
                    return Ok(new
                    {
                        scenario = "8.2_8.3_DIRECT_CONTROL_PANEL",
                        requiresAuth = false,
                        action = "SHOW_CONTROL_PANEL",
                        mode = normalizedMode,
                        user = MapUserResponse(authResult.User, authResult.AuthMethod)
                    });
                }

                // Madde 8.4: Security=1 -> Şifreli token veya düz kullanıcı adı çözümlenir ve Şifre/PIN doğrulama ekranı gelir
                string resolvedUserCode = UserTokenProtector.ResolveUserCodeOrPlain(trimmedParam);
                return Ok(new
                {
                    scenario = "8.4_PIN_REQUIRED",
                    requiresAuth = true,
                    lockUserCode = true,
                    userCode = resolvedUserCode,
                    mode = normalizedMode,
                    message = $"'{resolvedUserCode}' kullanıcısı için lütfen şifre / PIN giriniz."
                });
            }

            // 2. Parametresiz açılış (Madde 8.5): Mevcut geçerli bir oturum var mı kontrol et
            if (User?.Identity?.IsAuthenticated == true)
            {
                return Ok(new
                {
                    scenario = "8.5_SESSION_ACTIVE",
                    requiresAuth = false,
                    action = "SHOW_CONTROL_PANEL",
                    mode = normalizedMode,
                    user = new
                    {
                        userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0"),
                        userCode = User.FindFirstValue("UserCode") ?? "",
                        fullName = User.FindFirstValue(ClaimTypes.Name) ?? "",
                        role = User.FindFirstValue(ClaimTypes.Role) ?? "viewer",
                        authMethod = User.FindFirstValue("AuthMethod") ?? "Password"
                    }
                });
            }

            // Oturum yoksa standart Login ekranı açılır (Madde 8.5.1)
            return Ok(new
            {
                scenario = "8.5_FULL_LOGIN_REQUIRED",
                requiresAuth = true,
                lockUserCode = false,
                userCode = "",
                mode = normalizedMode,
                message = "Devam etmek için kullanıcı kodu ve şifrenizle giriş yapınız."
            });
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            string? ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            string resolvedUserCode = UserTokenProtector.ResolveUserCodeOrPlain(request.UserCode);
            var result = await _authService.AuthenticateWithPasswordAsync(resolvedUserCode, request.Password, ip);

            if (!result.Success || result.User == null)
            {
                return Unauthorized(new { success = false, message = result.Message });
            }

            await SignInUserCookieAsync(result.User, result.AuthMethod);

            string normalizedMode = NormalizeMode(request.Mode);
            string nextAction = normalizedMode.Equals("Slayt", StringComparison.OrdinalIgnoreCase)
                ? "START_SLIDESHOW"
                : "SHOW_CONTROL_PANEL";

            return Ok(new
            {
                success = true,
                message = result.Message,
                action = nextAction,
                mode = normalizedMode,
                user = MapUserResponse(result.User, result.AuthMethod)
            });
        }

        /// <summary>
        /// Admin kullanıcılarının TV veya Exe kısayolları için şifreli User= token'ı üretebilmesini sağlar.
        /// </summary>
        [HttpGet("generate-token")]
        public IActionResult GenerateUserToken([FromQuery] string userCode)
        {
            if (User?.Identity?.IsAuthenticated != true || User.FindFirstValue(ClaimTypes.Role) != "admin")
            {
                return Unauthorized(new { message = "Token üretmek için Admin oturumu gereklidir." });
            }

            if (string.IsNullOrWhiteSpace(userCode))
            {
                return BadRequest(new { message = "Kullanıcı kodu boş olamaz." });
            }

            string token = UserTokenProtector.CreateToken(userCode);
            return Ok(new
            {
                userCode = userCode.Trim(),
                encryptedUserToken = token,
                tvSlideUrl = $"/?User={token}&Security=0&Mode=Slayt",
                exeDirectUrl = $"/?User={token}&Security=0",
                pinRequiredUrl = $"/?User={token}&Security=1"
            });
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Ok(new { success = true });
        }

        [HttpGet("me")]
        public IActionResult GetCurrentUser()
        {
            if (User?.Identity?.IsAuthenticated != true)
            {
                return Unauthorized(new { authenticated = false });
            }

            return Ok(new
            {
                authenticated = true,
                userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0"),
                userCode = User.FindFirstValue("UserCode") ?? "",
                fullName = User.FindFirstValue(ClaimTypes.Name) ?? "",
                role = User.FindFirstValue(ClaimTypes.Role) ?? "viewer",
                authMethod = User.FindFirstValue("AuthMethod") ?? "Password"
            });
        }

        private async Task SignInUserCookieAsync(BiUser user, string authMethod)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.user_id.ToString()),
                new("UserCode", user.user_code),
                new(ClaimTypes.Name, user.full_name),
                new(ClaimTypes.Role, user.role),
                new("AuthMethod", authMethod)
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    AllowRefresh = true,
                    IssuedUtc = DateTimeOffset.UtcNow
                });
        }

        private static string NormalizeMode(string? mode)
        {
            if (!string.IsNullOrWhiteSpace(mode) &&
                (mode.Equals("Slayt", StringComparison.OrdinalIgnoreCase) ||
                 mode.Equals("Slide", StringComparison.OrdinalIgnoreCase)))
            {
                return "Slayt";
            }
            return "Standalone";
        }

        private static object MapUserResponse(BiUser user, string authMethod)
        {
            return new
            {
                userId = user.user_id,
                userCode = user.user_code,
                fullName = user.full_name,
                role = user.role,
                authMethod
            };
        }
    }
}
