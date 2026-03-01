using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OwenModbusMonitor;
using System;
using System.IO;
using System.Linq;

// Создаем папку wwwroot, если она отсутствует, чтобы избежать ошибки WebRootPath
Directory.CreateDirectory("wwwroot");

var builder = WebApplication.CreateBuilder(args);

// Настраиваем прослушивание порта 5000
builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(5000));

// Регистрируем DeviceController как Singleton сервис
builder.Services.AddSingleton<DeviceController>(sp =>
{
    var controller = new DeviceController("127.0.0.1", 502);
    controller.StartMonitoring();
    return controller;
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/data", (DeviceController device) =>
{
    return new
    {
        isConnected = device.IsConnected,
        waitingSeconds = device.ConnectionLostTime.HasValue ? (int)(DateTime.Now - device.ConnectionLostTime.Value).TotalSeconds : 0,
        pressure = device.Davlenie.HasValue ? device.Davlenie.CurrentValue : (float?)null,
        setpoint = device.Ustavka.HasValue ? device.Ustavka.CurrentValue : (float?)null,
        start = device.StartVar.CurrentValue,
        stop = device.StopVar.CurrentValue,
        setpointReached = device.SetpointReached.CurrentValue,
        overPressure = device.OverPress.CurrentValue,
        utechka = device.Utechka.CurrentValue,
        errDD = device.ErrDD.CurrentValue,
        errSetpoint = device.ErrSetpoint.CurrentValue,
        success = device.Success.CurrentValue,
        fail = device.Fail.CurrentValue,
        goodCount = device.GoodCount,
        failCount = device.FailCount,
        logCount = device.LogCount,
        timestamp = DateTime.Now
    };
});

// Эндпоинты для управления опросом
app.MapPost("/api/start", async (DeviceController device) => await device.WriteStartAsync());
app.MapPost("/api/stop", async (DeviceController device) => await device.WriteStopAsync());
app.MapPost("/api/setpoint", async (DeviceController device, SetpointRequest req) => await device.WriteUstavkaAsync(req.Value));
app.MapPost("/api/reset", async (DeviceController device) => await device.ResetStatusAsync());
app.MapPost("/api/logs/clear", (DeviceController device) => device.ClearLogs());
app.MapPost("/api/history/clear", (DeviceController device) => device.ClearHistory());

// Эндпоинт для скачивания полного лог-файла
app.MapGet("/api/logs/download", () => 
{
    if (File.Exists("errors.log"))
    {
        return Results.File(Path.GetFullPath("errors.log"), "text/plain", "errors.log");
    }
    return Results.NotFound();
});

app.MapGet("/api/history/download", () => 
{
    if (File.Exists("history.csv"))
    {
        return Results.File(Path.GetFullPath("history.csv"), "text/csv", $"history_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
    }
    return Results.NotFound();
});

// Эндпоинт для чтения логов (с пагинацией)
app.MapGet("/api/logs", (string? filter, int? page, int? pageSize, string? sort) => 
{
    int p = page ?? 1;
    int ps = pageSize ?? 20; // По умолчанию 20 записей на страницу
    if (File.Exists("errors.log"))
    {
        IEnumerable<string> lines = File.ReadAllLines("errors.log");
        
        if (sort != "asc")
        {
            lines = lines.Reverse();
        }

        if (!string.IsNullOrEmpty(filter))
        {
            lines = lines.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }
        
        int total = lines.Count();
        var result = lines.Skip((p - 1) * ps).Take(ps);
        
        return new { total, page = p, pageSize = ps, logs = result };
    }
    return new { total = 0, page = p, pageSize = ps, logs = Enumerable.Empty<string>() };
});

Console.WriteLine("Веб-сервер запущен: http://localhost:5000");
app.Run();

record SetpointRequest(float Value);
