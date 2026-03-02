﻿using Microsoft.AspNetCore.Builder;
using ClosedXML.Excel;
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
        timestamp = DateTime.Now,
        recentLogs = device.GetRecentLogs()
    };
});

// Эндпоинты для управления опросом
app.MapPost("/api/start", async (DeviceController device) => await device.WriteStartAsync());
app.MapPost("/api/stop", async (DeviceController device) => await device.WriteStopAsync());
app.MapPost("/api/setpoint", async (DeviceController device, SetpointRequest req) => await device.WriteUstavkaAsync(req.Value));
app.MapPost("/api/reset", async (DeviceController device) => await device.ResetStatusAsync());
app.MapPost("/api/logs/clear", (DeviceController device) => device.ClearLogs());
app.MapPost("/api/history/clear", (DeviceController device) => device.ClearHistory());

// Эндпоинт для удаления одной строки лога
app.MapPost("/api/logs/delete", async (HttpRequest request, DeviceController device) =>
{
    var body = await request.ReadFromJsonAsync<LogRequest>();
    if (body != null && !string.IsNullOrEmpty(body.Line))
    {
        device.RemoveLogLine(body.Line);
        return Results.Ok();
    }
    return Results.BadRequest();
});

// Эндпоинт для загрузки и объединения логов
app.MapPost("/api/logs/upload", async (HttpRequest request, DeviceController device) =>
{
    if (!request.HasFormContentType) return Results.BadRequest();
    
    var form = await request.ReadFormAsync();
    var file = form.Files["file"];
    
    if (file is null || file.Length == 0) return Results.BadRequest();
    
    using var reader = new StreamReader(file.OpenReadStream());
    var content = await reader.ReadToEndAsync();
    
    device.ImportLogs(content);
    return Results.Ok();
});

// Эндпоинт для скачивания полного лог-файла
app.MapGet("/api/logs/download", () => 
{
    if (File.Exists("errors.csv"))
    {
        var lines = File.ReadAllLines("errors.csv", System.Text.Encoding.UTF8);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Время;Уровень;Событие"); // Заголовки CSV

        foreach (var line in lines)
        {
            var parts = line.Split(';');
            if (parts.Length >= 3)
            {
                // Новый формат: Время;Уровень;Событие
                sb.AppendLine(line);
            }
            else if (parts.Length == 2)
            {
                // Старый формат CSV: Время;Событие -> добавляем уровень INFO по умолчанию
                sb.AppendLine($"{parts[0]};INFO;{parts[1]}");
            }
            else
            {
                // Старый формат лога: Время: Событие
                int idx = line.IndexOf(": ");
                if (idx > 0)
                {
                    sb.AppendLine($"{line.Substring(0, idx)};INFO;{line.Substring(idx + 2).Replace(";", ",")}");
                }
                else { sb.AppendLine($";{line.Replace(";", ",")}"); }
            }
        }

        var fileName = $"Журнал событий_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
        // Добавляем BOM для корректного открытия в Excel
        var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return Results.File(bytes, "text/csv", fileName);
    }
    return Results.NotFound();
});

app.MapGet("/api/history/download", () => 
{
    if (File.Exists("history.csv"))
    {
        var lines = File.ReadAllLines("history.csv");
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("История");

        for (int i = 0; i < lines.Length; i++)
        {
            // Удаляем BOM если есть и разбиваем по разделителю
            var line = lines[i].Trim('\uFEFF'); 
            var parts = line.Split(';');
            
            for (int j = 0; j < parts.Length; j++)
            {
                var cell = worksheet.Cell(i + 1, j + 1);
                var value = parts[j];

                // Пытаемся распарсить числа и даты для корректного формата в Excel
                if (i > 0 && j == 0 && DateTime.TryParse(value, out DateTime dt))
                    cell.Value = dt;
                else if (i > 0 && double.TryParse(value, out double num))
                    cell.Value = num;
                else
                    cell.Value = value;
            }
        }

        worksheet.Columns().AdjustToContents(); // Автоширина колонок

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return Results.File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"history_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
    }
    return Results.NotFound();
});

app.MapGet("/api/history/info", () => 
{
    if (File.Exists("history.csv"))
    {
        return new { exists = true, size = new FileInfo("history.csv").Length };
    }
    return new { exists = false, size = 0L };
});

// Эндпоинт для чтения логов (с пагинацией)
app.MapGet("/api/logs", (string? filter, int? page, int? pageSize, string? sort, DateTime? from, DateTime? to) => 
{
    int p = page ?? 1;
    int ps = pageSize ?? 20; // По умолчанию 20 записей на страницу
    if (File.Exists("errors.csv"))
    {
        IEnumerable<string> lines = File.ReadAllLines("errors.csv");
        
        if (from.HasValue || to.HasValue)
        {
            lines = lines.Where(l => 
            {
                // Пробуем разбить по ; (новый формат) или по : (старый)
                var parts = l.Split(';');
                string dateStr = parts.Length > 1 ? parts[0] : l.Split(new[] { ": " }, StringSplitOptions.None)[0];

                if (DateTime.TryParse(dateStr, out DateTime dt))
                {
                    bool after = !from.HasValue || dt.Date >= from.Value.Date;
                    bool before = !to.HasValue || dt.Date <= to.Value.Date;
                    return after && before;
                }
                return false;
            });
        }

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

        // Для отображения в UI заменяем ; обратно на : для красоты
        // Возвращаем объект с сырой строкой, отображаемой и уровнем важности
        var logs = result.Select(l => 
        {
            var parts = l.Split(';');
            string level = "INFO";
            string display = l.Replace(";", ": ");

            if (parts.Length >= 3)
            {
                level = parts[1];
                // Формируем строку для отображения без уровня важности: "Время: Сообщение"
                display = $"{parts[0]}: {string.Join(";", parts.Skip(2))}";
            }
            
            return new { 
                raw = l, 
                display,
                level
            };
        });
        
        return Results.Ok(new { total, page = p, pageSize = ps, logs });
    }
    return Results.Ok(new { total = 0, page = p, pageSize = ps, logs = Array.Empty<object>() });
});

Console.WriteLine("Веб-сервер запущен: http://localhost:5000");
app.Run();

record SetpointRequest(float Value);
record LogRequest(string Line);
