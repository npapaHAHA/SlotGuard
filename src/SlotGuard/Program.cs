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
builder.Services.AddOptions<WaitlistOptions>()
    .BindConfiguration("Waitlist")
    .Validate(options => options.HoldSeconds is >= 1 and <= 600, "Waitlist:HoldSeconds must be between 1 and 600.")
    .ValidateOnStart();
builder.Services.AddScoped<SlotBookings>();
builder.Services.AddScoped<OrganizationScope>();
builder.Services.AddHostedService<HoldExpirationWorker>();
builder.Services.AddOptions<RabbitMqOptions>()
    .BindConfiguration("RabbitMq")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Queue), "A RabbitMQ queue name is required.")
    .Validate(options => string.IsNullOrWhiteSpace(options.Uri)
        || (Uri.TryCreate(options.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "amqp" or "amqps"),
        "RabbitMq:Uri must use amqp or amqps.")
    .ValidateOnStart();
builder.Services.AddSingleton<HoldQueue>();
builder.Services.AddHostedService<HoldOutboxWorker>();
builder.Services.AddHostedService<HoldNotificationWorker>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "SlotGuard.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Events.OnValidatePrincipal = context =>
        {
            var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            if (context.Principal?.Identity?.Name is not null && string.IsNullOrEmpty(config["DemoAccounts:Password"]))
                context.RejectPrincipal();
            return Task.CompletedTask;
        };
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
app.MapBookingEndpoints();
app.Run();

public partial class Program;
