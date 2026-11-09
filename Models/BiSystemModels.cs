using System;

namespace SpsWebBI.Models
{
    public class BiUser
    {
        public int user_id { get; set; }
        public string user_code { get; set; } = string.Empty;
        public string full_name { get; set; } = string.Empty;
        public string password_hash { get; set; } = string.Empty;
        public string role { get; set; } = "viewer"; // admin, designer, viewer
        public bool allow_passwordless { get; set; } // Security=0 izni
        public bool is_active { get; set; } = true;
        public int failed_login_count { get; set; }
        public DateTime? locked_until { get; set; }
        public DateTime created_at { get; set; }
    }

    public class BiDataSource
    {
        public int datasource_id { get; set; }
        public string code { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public string? category { get; set; }
        public string? connection_id { get; set; }
        public string sql_query { get; set; } = string.Empty;
        public string? columns_schema_json { get; set; }
        public bool is_active { get; set; } = true;
        public DateTime created_at { get; set; }
        public DateTime? updated_at { get; set; }
    }

    public class BiDataSourceParam
    {
        public int param_id { get; set; }
        public int datasource_id { get; set; }
        public string param_name { get; set; } = string.Empty;
        public string label { get; set; } = string.Empty;
        public string param_type { get; set; } = "text"; // date, select, text, number
        public string? default_value { get; set; }
        public string? lookup_sql { get; set; }
        public int display_order { get; set; } = 1;
    }

    public class BiDashboard
    {
        public int dashboard_id { get; set; }
        public string code { get; set; } = string.Empty;
        public string title { get; set; } = string.Empty;
        public string? description { get; set; }
        public int primary_datasource_id { get; set; }
        public string layout_json { get; set; } = "{}";
        public bool is_active { get; set; } = true;
        public DateTime created_at { get; set; }
        public DateTime? updated_at { get; set; }
    }

    public class BiUserDashboard
    {
        public int user_dashboard_id { get; set; }
        public int user_id { get; set; }
        public int dashboard_id { get; set; }
        public bool can_view { get; set; } = true;
        public bool can_edit_layout { get; set; } = true;
        public int display_order { get; set; } = 1;
        public int refresh_seconds { get; set; } = 60;
        public int slide_duration_seconds { get; set; } = 30;
        public string? custom_layout_json { get; set; }
    }

    /// <summary>
    /// Kullanıcının yetkili olduğu dashboard, veri kaynağı ve süre bilgilerini birleştiren görünüm modeli.
    /// </summary>
    public class AuthorizedDashboardView
    {
        public int dashboard_id { get; set; }
        public string dashboard_code { get; set; } = string.Empty;
        public string title { get; set; } = string.Empty;
        public string? description { get; set; }
        public int primary_datasource_id { get; set; }
        public string datasource_code { get; set; } = string.Empty;
        public string datasource_name { get; set; } = string.Empty;
        public string? category { get; set; }
        public int display_order { get; set; }
        public int refresh_seconds { get; set; }
        public int slide_duration_seconds { get; set; }
        public bool can_edit_layout { get; set; }
        public string effective_layout_json { get; set; } = "{}";
        public bool has_custom_layout { get; set; }
    }
}

