using Lab_13;

var builder = WebApplication.CreateBuilder(args);

// Подключаем серверные Razor Components без внешних frontend-зависимостей.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Регистрируем сервисы через DI, чтобы не использовать глобальные переменные для координации.
builder.Services.AddSingleton<CompressionAlgorithmRegistry>();
builder.Services.AddSingleton<WorkerRegistry>();
builder.Services.AddSingleton<CompressionJobStore>();
builder.Services.AddSingleton<IWorkerSelectionStrategy, LeastLoadedWorkerSelectionStrategy>();
builder.Services.AddSingleton<TcpCompressionClient>();
builder.Services.AddSingleton<CompressionPipeline>();
builder.Services.AddSingleton<MasterCompressionService>();

// HostedService запускает 4 воркера, а монитор отдельно проверяет heartbeat.
builder.Services.AddHostedService<WorkerClusterHostedService>();
builder.Services.AddHostedService<WorkerMonitorHostedService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseAntiforgery();

// Папка создается заранее, чтобы результаты сжатия всегда сохранялись в корне проекта.
var outputDirectory = Path.Combine(app.Environment.ContentRootPath, "CompressedFiles");
Directory.CreateDirectory(outputDirectory);

// Отдаем готовый файл по безопасному имени, не позволяя выйти за пределы папки результатов.
app.MapGet(
    "/compressed/{fileName}",
    (string fileName, IWebHostEnvironment environment) =>
    {
        var safeFileName = Path.GetFileName(fileName);
        var path = Path.Combine(environment.ContentRootPath, "CompressedFiles", safeFileName);

        return File.Exists(path)
            ? Results.File(path, "application/octet-stream", safeFileName)
            : Results.NotFound("Файл не найден.");
    }
);

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
