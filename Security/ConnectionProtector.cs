using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace SpsWebBI.Security
{
    /// <summary>
    /// Veritabanı bağlantı dizesindeki (Connection String) parolayı açık metin olarak tutmamak için
    /// Windows DPAPI (CurrentUser) ile şifreler ve çalışma zamanında çözer.
    /// </summary>
    public static class ConnectionProtector
    {
        private const string Prefix = "ENC:";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SpsWebBI_MariaDB_DPAPI_Entropy_v1");

        public static string EncryptSecret(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return string.Empty;
            if (plainText.StartsWith(Prefix, StringComparison.Ordinal)) return plainText;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                byte[] data = Encoding.UTF8.GetBytes(plainText);
                byte[] encrypted = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(encrypted);
            }

            return plainText;
        }

        public static string DecryptSecret(string? cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return string.Empty;
            if (!cipherText.StartsWith(Prefix, StringComparison.Ordinal)) return cipherText;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    string base64 = cipherText.Substring(Prefix.Length);
                    byte[] encrypted = Convert.FromBase64String(base64);
                    byte[] decrypted = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(decrypted)).Length > 0
                        ? Encoding.UTF8.GetString(decrypted)
                        : string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }

            return cipherText;
        }

        public static string GetUsableConnectionString(string rawConnectionString, string dbType = "MariaDB")
        {
            if (string.IsNullOrWhiteSpace(rawConnectionString))
                return string.Empty;

            string typeNorm = (dbType ?? "MariaDB").Trim();
            if (string.Equals(typeNorm, "SQLite", StringComparison.OrdinalIgnoreCase))
            {
                return rawConnectionString;
            }

            if (string.Equals(typeNorm, "MSSQL", StringComparison.OrdinalIgnoreCase))
            {
                var sqlB = new SqlConnectionStringBuilder(rawConnectionString);
                if (!string.IsNullOrEmpty(sqlB.Password) && sqlB.Password.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    sqlB.Password = DecryptSecret(sqlB.Password);
                }
                return sqlB.ConnectionString;
            }

            if (string.Equals(typeNorm, "PostgreSQL", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeNorm, "Supabase", StringComparison.OrdinalIgnoreCase))
            {
                var pgB = new NpgsqlConnectionStringBuilder(rawConnectionString);
                if (!string.IsNullOrEmpty(pgB.Password) && pgB.Password.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    pgB.Password = DecryptSecret(pgB.Password);
                }
                return pgB.ConnectionString;
            }

            var builder = new MySqlConnectionStringBuilder(rawConnectionString);
            if (!string.IsNullOrEmpty(builder.Password) && builder.Password.StartsWith(Prefix, StringComparison.Ordinal))
            {
                builder.Password = DecryptSecret(builder.Password);
            }
            // Güvenlik: Yerel dosya yüklemeyi sürücü seviyesinde kapat
            builder.AllowLoadLocalInfile = false;
            return builder.ConnectionString;
        }

        public static string ProtectConnectionStringPassword(string rawConnectionString, string dbType = "MariaDB")
        {
            if (string.IsNullOrWhiteSpace(rawConnectionString))
                return string.Empty;

            string typeNorm = (dbType ?? "MariaDB").Trim();
            if (string.Equals(typeNorm, "SQLite", StringComparison.OrdinalIgnoreCase))
            {
                return rawConnectionString;
            }

            if (string.Equals(typeNorm, "MSSQL", StringComparison.OrdinalIgnoreCase))
            {
                var sqlB = new SqlConnectionStringBuilder(rawConnectionString);
                if (!string.IsNullOrEmpty(sqlB.Password) && !sqlB.Password.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    sqlB.Password = EncryptSecret(sqlB.Password);
                }
                return sqlB.ConnectionString;
            }

            if (string.Equals(typeNorm, "PostgreSQL", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeNorm, "Supabase", StringComparison.OrdinalIgnoreCase))
            {
                var pgB = new NpgsqlConnectionStringBuilder(rawConnectionString);
                if (!string.IsNullOrEmpty(pgB.Password) && !pgB.Password.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    pgB.Password = EncryptSecret(pgB.Password);
                }
                return pgB.ConnectionString;
            }

            var builder = new MySqlConnectionStringBuilder(rawConnectionString);
            if (!string.IsNullOrEmpty(builder.Password) && !builder.Password.StartsWith(Prefix, StringComparison.Ordinal))
            {
                builder.Password = EncryptSecret(builder.Password);
            }
            return builder.ConnectionString;
        }
    }
}

