using System;
using System.Security.Cryptography;
using System.Text;

namespace SpsWebBI.Security
{
    /// <summary>
    /// Security=0 (şifresiz TV / Exe girişi) senaryolarında URL'deki User= parametresine
    /// dışarıdan birinin elle kullanıcı adı yazarak girmesini engellemek için
    /// kullanıcı kodunu AES-256 + HMAC-SHA256 ile şifreler ve doğrular.
    /// </summary>
    public static class UserTokenProtector
    {
        private const string TokenPrefix = "UT1_";
        // Sunucuya özel sabit kriptografik anahtar türetici (Deterministik URL üretimi ve doğrulaması için)
        private static readonly byte[] AesKey = SHA256.HashData(Encoding.UTF8.GetBytes("SpsWebBI_UrlUserToken_AesKey_2026_v1"));
        private static readonly byte[] HmacKey = SHA256.HashData(Encoding.UTF8.GetBytes("SpsWebBI_UrlUserToken_HmacKey_2026_v1"));

        /// <summary>
        /// Verilen user_code (örn: "A" veya "AHMET") için dışarıdan tahmin edilemeyen şifreli ve imzalı URL parametresi üretir.
        /// </summary>
        public static string CreateToken(string userCode)
        {
            if (string.IsNullOrWhiteSpace(userCode))
                throw new ArgumentException("Kullanıcı kodu boş olamaz.", nameof(userCode));

            string normalized = userCode.Trim();
            byte[] plainBytes = Encoding.UTF8.GetBytes(normalized);

            // Kullanıcı kodundan deterministik IV (16 byte) üret (aynı kullanıcı için sabit kısayol linki oluşturulabilsin)
            byte[] fullIvHash = HMACSHA256.HashData(HmacKey, Encoding.UTF8.GetBytes("IV:" + normalized.ToUpperInvariant()));
            byte[] iv = new byte[16];
            Buffer.BlockCopy(fullIvHash, 0, iv, 0, 16);

            using var aes = Aes.Create();
            aes.Key = AesKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

            // IV + Cipher birleştir ve HMAC-SHA256 imzası ekle (12 byte tag)
            byte[] payload = new byte[iv.Length + cipherBytes.Length];
            Buffer.BlockCopy(iv, 0, payload, 0, iv.Length);
            Buffer.BlockCopy(cipherBytes, 0, payload, iv.Length, cipherBytes.Length);

            byte[] mac = HMACSHA256.HashData(HmacKey, payload);
            byte[] finalBytes = new byte[payload.Length + 12];
            Buffer.BlockCopy(payload, 0, finalBytes, 0, payload.Length);
            Buffer.BlockCopy(mac, 0, finalBytes, payload.Length, 12);

            return TokenPrefix + Base64UrlEncode(finalBytes);
        }

        /// <summary>
        /// URL'den gelen User parametresinin geçerli bir şifreli token olup olmadığını kontrol eder ve içindeki gerçek user_code'u çözer.
        /// Düz metin (örn: "A" veya "Ahmet") gönderilmişse false döner.
        /// </summary>
        public static bool TryValidateAndExtractUserCode(string? token, out string userCode)
        {
            userCode = string.Empty;
            if (string.IsNullOrWhiteSpace(token))
                return false;

            string trimmed = token.Trim();
            if (!trimmed.StartsWith(TokenPrefix, StringComparison.Ordinal))
                return false;

            try
            {
                byte[] finalBytes = Base64UrlDecode(trimmed.Substring(TokenPrefix.Length));
                if (finalBytes.Length < 16 + 16 + 12) // Min: 16 IV + 16 Cipher + 12 MAC
                    return false;

                int payloadLength = finalBytes.Length - 12;
                byte[] payload = new byte[payloadLength];
                byte[] receivedMac = new byte[12];
                Buffer.BlockCopy(finalBytes, 0, payload, 0, payloadLength);
                Buffer.BlockCopy(finalBytes, payloadLength, receivedMac, 0, 12);

                byte[] fullComputedMac = HMACSHA256.HashData(HmacKey, payload);
                byte[] expectedMac = new byte[12];
                Buffer.BlockCopy(fullComputedMac, 0, expectedMac, 0, 12);

                if (!CryptographicOperations.FixedTimeEquals(receivedMac, expectedMac))
                    return false;

                byte[] iv = new byte[16];
                int cipherLen = payloadLength - 16;
                byte[] cipherBytes = new byte[cipherLen];
                Buffer.BlockCopy(payload, 0, iv, 0, 16);
                Buffer.BlockCopy(payload, 16, cipherBytes, 0, cipherLen);

                using var aes = Aes.Create();
                aes.Key = AesKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using var decryptor = aes.CreateDecryptor();
                byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                userCode = Encoding.UTF8.GetString(plainBytes);
                return !string.IsNullOrWhiteSpace(userCode);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Security=1 (Şifreli giriş) senaryosunda hem şifreli token hem de düz kullanıcı kodu gönderilmiş olabilir; ikisini de çözümler.
        /// </summary>
        public static string ResolveUserCodeOrPlain(string rawUserParam)
        {
            if (TryValidateAndExtractUserCode(rawUserParam, out string extracted))
            {
                return extracted;
            }
            return rawUserParam.Trim();
        }

        private static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static byte[] Base64UrlDecode(string input)
        {
            string base64 = input.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            return Convert.FromBase64String(base64);
        }
    }
}

