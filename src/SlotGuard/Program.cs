var app = WebApplication.CreateBuilder(args).Build();
app.MapGet("/", () => Results.Ok(new { service = "SlotGuard" }));
app.Run();
