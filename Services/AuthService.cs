using System;
using System.Data.Common;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using SpsWebBI.Models;
using SpsWebBI.Security;

namespace SpsWebBI.Services
{
    public class AuthResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public BiUser? User { get; set; }
        public string AuthMethod { get; set; } = "Password"; // Password veya Passwordless
    }

    /// <summary>
    /// Kullanıcı doğrulama, Brute-Force hesap kilitleme, Security=0 (şifresiz giriş) yetki kontrolü
    /// ve siber güvenlik denetim (Audit Log) işlemlerini yürüten servis.
    /// </summary>
    public class AuthService
    {
        private readonly BiDbConnectionManager _biConnManager;
        private readonly IConfiguration _configuration;

        public AuthService(BiDbConnectionManager biConnManager, IConfiguration configuration)
        {
            _biConnManager = biConnManager;
            _configuration = configuration;
        }

        public async Task<AuthResult> AuthenticateWithPasswordAsync(string userCode, string password, string? ipAddress)
        {
            if (string.IsNullOrWhiteSpace(userCode) || string.IsNullOrEmpty(password))
            {
                return new AuthResult { Success = false, Message = "Kullanıcı kodu ve şifre zorunludur." };
            }

            try
            {
                await using DbConnection conn = await _biConnManager.OpenBiConnectionAsync();

                var user = await conn.QueryFirstOrDefaultAsync<BiUser>(
                    "SELECT * FROM bi_users WHERE user_code = @UserCode LIMIT 1;",
                    new { UserCode = userCode.Trim() });

                if (user == null || !user.is_active)
                {
                    await WriteAuditLogAsync(conn, userCode, "LOGIN_FAIL", ipAddress, "Kullanıcı bulunamadı veya pasif.");
                    return new AuthResult { Success = false, Message = "Kullanıcı kodu veya şifre hatalı." };
                }

                // Brute-Force Kilitleme Kontrolü
                if (user.locked_until.HasValue && user.locked_until.Value > DateTime.Now)
                {
                    int remainingMin = (int)Math.Ceiling((user.locked_until.Value - DateTime.Now).TotalMinutes);
                    await WriteAuditLogAsync(conn, user.user_code, "LOGIN_LOCKED", ipAddress, $"Kilitli hesaba giriş denemesi ({remainingMin} dk kaldı).");
                    return new AuthResult
                    {
                        Success = false,
                        Message = $"Çok fazla hatalı deneme yapıldı. Hesabınız {remainingMin} dakika boyunca kilitlidir."
                    };
                }

                bool isValid = PasswordHasher.VerifyPassword(password, user.password_hash);
                if (!isValid)
                {
                    int maxAttempts = _configuration.GetValue<int>("SecuritySettings:MaxFailedLoginAttempts", 5);
                    int lockoutMinutes = _configuration.GetValue<int>("SecuritySettings:LockoutMinutes", 15);
                    int newFailCount = user.failed_login_count + 1;
                    DateTime? lockUntil = newFailCount >= maxAttempts ? DateTime.Now.AddMinutes(lockoutMinutes) : null;

                    await conn.ExecuteAsync(@"
                        UPDATE bi_users 
                           SET failed_login_count = @FailCount,
                               locked_until = @LockedUntil
                         WHERE user_id = @UserId;",
                        new { FailCount = newFailCount, LockedUntil = lockUntil, UserId = user.user_id });

                    await WriteAuditLogAsync(conn, user.user_code, "LOGIN_FAIL", ipAddress, $"Hatalı şifre denemesi ({newFailCount}/{maxAttempts}).");
                    return new AuthResult { Success = false, Message = "Kullanıcı kodu veya şifre hatalı." };
                }

                // Başarılı giriş: hata sayacını sıfırla
                await conn.ExecuteAsync(@"
                    UPDATE bi_users 
                       SET failed_login_count = 0,
                           locked_until = NULL
                     WHERE user_id = @UserId;",
                    new { UserId = user.user_id });

                await WriteAuditLogAsync(conn, user.user_code, "LOGIN_SUCCESS", ipAddress, "Şifreli doğrulama başarılı.");

                return new AuthResult
                {
                    Success = true,
                    Message = "Giriş başarılı.",
                    User = user,
                    AuthMethod = "Password"
                };
            }
            catch (Exception ex)
            {
                return new AuthResult { Success = false, Message = $"Veritabanı bağlantı hatası: {ex.Message}" };
            }
        }

        /// <summary>
        /// Madde 8.1, 8.2, 8.3: Security=0 parametresi ile şifresiz giriş doğrulaması.
        /// Yalnızca allow_passwordless yetkisi açık olan kullanıcılar için izin verir.
        /// </summary>
        public async Task<AuthResult> AuthenticatePasswordlessAsync(string userCode, string? ipAddress)
        {
            if (string.IsNullOrWhiteSpace(userCode))
            {
                return new AuthResult { Success = false, Message = "Kullanıcı parametresi (User) boş olamaz." };
            }

            try
            {
                await using DbConnection conn = await _biConnManager.OpenBiConnectionAsync();

                var user = await conn.QueryFirstOrDefaultAsync<BiUser>(
                    "SELECT * FROM bi_users WHERE user_code = @UserCode LIMIT 1;",
                    new { UserCode = userCode.Trim() });

                if (user == null || !user.is_active)
                {
                    await WriteAuditLogAsync(conn, userCode, "PASSWORDLESS_DENIED", ipAddress, "Kullanıcı bulunamadı veya pasif.");
                    return new AuthResult { Success = false, Message = $"'{userCode}' kullanıcısı bulunamadı veya pasif durumda." };
                }

                if (!user.allow_passwordless)
                {
                    await WriteAuditLogAsync(conn, user.user_code, "PASSWORDLESS_DENIED", ipAddress, "Kullanıcının Security=0 (şifresiz giriş) yetkisi yok.");
                    return new AuthResult
                    {
                        Success = false,
                        Message = $"'{user.user_code}' kullanıcısının şifresiz giriş (Security=0) yetkisi bulunmamaktadır. Lütfen şifre ile giriş yapınız."
                    };
                }

                await WriteAuditLogAsync(conn, user.user_code, "PASSWORDLESS_ACCESS", ipAddress, "Security=0 parametresi ile doğrudan gösterim erişimi sağlandı.");

                return new AuthResult
                {
                    Success = true,
                    Message = "Şifresiz gösterim girişi onaylandı.",
                    User = user,
                    AuthMethod = "Passwordless"
                };
            }
            catch (Exception ex)
            {
                return new AuthResult { Success = false, Message = $"Veritabanı bağlantı hatası: {ex.Message}" };
            }
        }

        public async Task WriteAuditLogAsync(DbConnection conn, string? userCode, string actionType, string? ipAddress, string? details)
        {
            try
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO bi_audit_logs (user_code, action_type, ip_address, details)
                    VALUES (@UserCode, @ActionType, @IpAddress, @Details);",
                    new
                    {
                        UserCode = userCode,
                        ActionType = actionType,
                        IpAddress = ipAddress,
                        Details = details
                    });
            }
            catch
            {
                // Log hatası ana akışı durdurmamalı
            }
        }
    }
}

