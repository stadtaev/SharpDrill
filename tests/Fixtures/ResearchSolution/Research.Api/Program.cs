using Research.Api;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
var app = builder.Build();
app.UseExceptionHandler("/error");
app.MapControllers();
app.Run();
