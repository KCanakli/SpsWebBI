using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SpsWebBI.Security
{
    /// <summary>
    /// Dinamik rapor sorgularının yalnızca salt-okunur (SELECT / WITH) olmasını garanti altına alan,
    /// DDL/DML/Dosya/Sistem komutlarını ve çoklu sorgu (Stacked Query) enjeksiyonlarını engelleyen güvenlik katmanı.
    /// </summary>
    public static class SqlSecurityValidator
    {
        private static readonly string[] ForbiddenKeywords = new[]
        {
            "INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "TRUNCATE",
            "CREATE", "REPLACE", "RENAME", "GRANT", "REVOKE", "LOCK", "UNLOCK",
            "CALL", "EXECUTE", "EXEC", "PREPARE", "DEALLOCATE",
            "OUTFILE", "DUMPFILE", "LOAD_FILE", "LOAD", "SLEEP", "BENCHMARK",
            "SHUTDOWN", "KILL", " PURGE", "RESET", "SET", "USE", "HANDLER"
        };

        private static readonly Regex CommentBlockRegex = new(@"/\*[\s\S]*?\*/", RegexOptions.Compiled);
        private static readonly Regex CommentLineRegex = new(@"(--|#).*?$", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex StringLiteralRegex = new(@"'([^'\\]|\\.)*'", RegexOptions.Compiled);
        private static readonly Regex ParameterRegex = new(@"(?<![@\w])@([a-zA-Z_][a-zA-Z0-9_]*)", RegexOptions.Compiled);
        private static readonly Regex IdentifierRegex = new(@"^[a-zA-Z0-9_]+$", RegexOptions.Compiled);

        public static (bool IsValid, string ErrorMessage) ValidateSelectQuery(string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return (false, "SQL sorgusu boş olamaz.");

            // 1. Yorum satırlarını ve metin sabitlerini temizleyerek analiz et
            string cleaned = CommentBlockRegex.Replace(sql, " ");
            cleaned = CommentLineRegex.Replace(cleaned, " ");
            string withoutStrings = StringLiteralRegex.Replace(cleaned, "''").Trim();

            // Sondaki tek noktalı virgülü kaldır, içeride başka noktalı virgül varsa (Stacked Query) engelle
            if (withoutStrings.EndsWith(";"))
            {
                withoutStrings = withoutStrings.Substring(0, withoutStrings.Length - 1).Trim();
            }

            if (withoutStrings.Contains(';'))
            {
                return (false, "Güvenlik İhlali: Çoklu SQL komutlarına (';') izin verilmemektedir.");
            }

            // 2. Sorgu mutlaka SELECT veya WITH (CTE) ile başlamalıdır
            if (!Regex.IsMatch(withoutStrings, @"^(SELECT|WITH)\b", RegexOptions.IgnoreCase))
            {
                return (false, "Güvenlik İhlali: Yalnızca SELECT veya WITH (salt okunur) sorgularına izin verilmektedir.");
            }

            // 3. Yasaklı anahtar kelimeleri kelime sınırlarıyla (Word Boundary) kontrol et
            foreach (var keyword in ForbiddenKeywords)
            {
                string kw = keyword.Trim();
                if (Regex.IsMatch(withoutStrings, $@"\b{Regex.Escape(kw)}\b", RegexOptions.IgnoreCase))
                {
                    return (false, $"Güvenlik İhlali: Sorgu içerisinde yasaklı '{kw}' komutu tespit edildi.");
                }
            }

            // 4. Sistem şemalarına doğrudan erişimi engelle (information_schema, mysql, performance_schema)
            if (Regex.IsMatch(withoutStrings, @"\b(information_schema|mysql|performance_schema|sys)\b", RegexOptions.IgnoreCase))
            {
                return (false, "Güvenlik İhlali: Sistem veritabanı tablolarına erişim engellenmiştir.");
            }

            return (true, string.Empty);
        }

        /// <summary>
        /// SQL sorgusu içindeki @ParametreAdi biçimindeki tüm dinamik filtre parametrelerini otomatik çıkarır.
        /// </summary>
        public static List<string> ExtractParameters(string? sql)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(sql)) return result;

            string cleaned = CommentBlockRegex.Replace(sql, " ");
            cleaned = CommentLineRegex.Replace(cleaned, " ");
            string withoutStrings = StringLiteralRegex.Replace(cleaned, "''");

            var matches = ParameterRegex.Matches(withoutStrings);
            foreach (Match m in matches)
            {
                string paramName = m.Groups[1].Value;
                if (!result.Contains(paramName, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(paramName);
                }
            }

            return result;
        }

        /// <summary>
        /// Cross-filter ve Drill-down sırasında gelen kolon adlarının güvenli bir SQL tanımlayıcısı olduğunu doğrular.
        /// </summary>
        public static bool IsSafeColumnIdentifier(string? columnName)
        {
            return !string.IsNullOrWhiteSpace(columnName) && IdentifierRegex.IsMatch(columnName);
        }
    }
}

