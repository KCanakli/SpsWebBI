using System;
using System.IO;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SpsWebBI.Data;
using SpsWebBI.Services;

var builder = WebApplication.CreateBuilder(args);

var cacheSection = builder.Configuration.GetSection("QueryCache");
builder.Services.Configure<QueryCacheOptions>(cacheSection);
int cacheSizeLimitMb = Math.Clamp(cacheSection.GetValue<int?>("SizeLimitMB") ?? 128, 16, 16384);
double cacheCompaction = Math.Clamp(cacheSection.GetValue<double?>("CompactionPercentage") ?? 0.25, 0.05, 0.80);

// Güvenli Cookie Oturum Yönetimi (HttpOnly, SameSite=Strict)
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "SpsWebBI.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
    });

builder.Services.AddAuthorization();
builder.Services.AddMemoryCache(options =>
{
    options.SizeLimit = (long)cacheSizeLimitMb * 1024L * 1024L;
    options.CompactionPercentage = cacheCompaction;
});
builder.Services.AddControllers();
builder.Services.AddSingleton<DbConnectionManager>();
builder.Services.AddSingleton<BiDbConnectionManager>();
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<DynamicQueryService>();

var app = builder.Build();

// Siber Güvenlik HTTP Başlıkları (Security Headers)
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Kök dizin (/) ve (/login) açılış parametrelerini değerlendiren giriş sayfasına yönlendirir
app.MapGet("/", async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "login.html"));
});

app.MapGet("/login", async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "login.html"));
});

app.MapGet("/app", async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "app.html"));
});

app.MapGet("/designer", async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "designer.html"));
});

app.MapGet("/designer-v2", context =>
{
    context.Response.Redirect("/designer");
    return System.Threading.Tasks.Task.CompletedTask;
});

app.MapGet("/admin", async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "admin.html"));
});

app.MapGet("/db-settings", async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "db-settings.html"));
});

app.MapGet("/bi-settings", async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "bi-settings.html"));
});

app.MapControllers();

// Uygulama açılışında şifreyi DPAPI'ye dönüştür ve MariaDB sistem tablolarını kontrol et / oluştur
using (var scope = app.Services.CreateScope())
{
    var dbInit = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await dbInit.InitializeAsync();
}

app.Run();
