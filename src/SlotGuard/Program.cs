using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SlotGuard;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<BookingDb>((services, options) =>
{
    var config = services.GetRequiredService<IConfiguration>();
    var connectionString = config.GetConnectionString("SlotGuard")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:SlotGuard.");
    options.UseNpgsql(connectionString);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "SlotGuard.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var protection = builder.Services.AddDataProtection().SetApplicationName("SlotGuard");
if (builder.Configuration["DataProtection:KeyDirectory"] is { Length: > 0 } keyDirectory)
    protection.PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));

var app = builder.Build();
// The demo runs one application instance and applies pending migrations on startup.
await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<BookingDb>().Database.MigrateAsync();

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapOpenApi();
app.MapGet("/", () => Results.Ok(new { service = "SlotGuard" }));
app.Run();

public partial class Program;
