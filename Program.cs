using PodManager.API.Hubs;
using PodManager.API.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true; // Detaylı hata mesajları
});

builder.Services.AddSingleton<IKubernetesService, KubernetesService>();
builder.Services.AddHostedService<PodMonitorService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
    {
        policy.WithOrigins("http://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// Detaylı loglama
builder.Logging.SetMinimumLevel(LogLevel.Debug);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseCors("AllowAngular");

app.MapControllers();
app.MapHub<TerminalHub>("/terminal");
app.MapHub<PodHub>("/hubs/pod");

app.Run();