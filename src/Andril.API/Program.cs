// Minimal scaffolding endpoint only. Protected ANDRIL features are NOT implemented.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
app.UseHttpsRedirection();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .WithName("HealthCheck");
app.Run();
