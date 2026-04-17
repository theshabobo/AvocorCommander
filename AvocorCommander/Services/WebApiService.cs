using AvocorCommander.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;

namespace AvocorCommander.Services;

/// <summary>
/// Embeds an ASP.NET Core Kestrel HTTP server alongside the WPF UI in the same process.
/// Hosts the REST API, WebSocket hub, auth middleware, and CORS configuration.
/// Runs entirely on a background thread — does not block the WPF dispatcher.
/// </summary>
public sealed class WebApiService : IDisposable
{
    private readonly DatabaseService    _db;
    private readonly ConnectionManager  _connMgr;
    private readonly SchedulerService   _scheduler;
    private readonly MacroRunnerService _macroRunner;

    private ServerDbService?  _serverDb;
    private JwtService?       _jwt;
    private WebSocketHub?     _wsHub;
    private WebApplication?   _app;
    private Task?             _runTask;

    /// <summary>True while the HTTP server is running.</summary>
    private volatile bool _isRunning;
    public bool IsRunning => _isRunning;

    /// <summary>Effective port the server is listening on.</summary>
    public int Port { get; private set; } = 5105;

    /// <summary>Last startup error, if any. Empty when running OK.</summary>
    public string LastError { get; private set; } = "";

    /// <summary>Raised on the thread pool when the server starts or stops.</summary>
    public event EventHandler<bool>? StatusChanged;

    /// <summary>Raised with log messages for UI display.</summary>
    public event EventHandler<string>? LogMessage;

    public WebApiService(
        DatabaseService    db,
        ConnectionManager  connMgr,
        SchedulerService   scheduler,
        MacroRunnerService macroRunner)
    {
        _db          = db;
        _connMgr     = connMgr;
        _scheduler   = scheduler;
        _macroRunner = macroRunner;
    }

    /// <summary>
    /// Starts the embedded HTTP/WS server on a background thread.
    /// Safe to call from the WPF UI thread.
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning) return;

        _serverDb = new ServerDbService();
        _jwt      = new JwtService(_serverDb.GetConfig("jwt.secret"));

        // Read port from server config
        if (int.TryParse(_serverDb.GetConfig("api.port", "5105"), out int cfgPort) && cfgPort > 0)
            Port = cfgPort;

        _wsHub = new WebSocketHub(_serverDb, _jwt, _connMgr, _scheduler, _macroRunner);

        // Wire web prompt handler so API-initiated macros push prompts to web clients
        _macroRunner.WebPromptHandler = _wsHub.HandleWebPromptAsync;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
        });

        // Configure Kestrel
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.ListenAnyIP(Port);
        });

        // Suppress noisy ASP.NET Core logs in a WPF context
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // CORS service registration (required before UseCors)
        builder.Services.AddCors();

        // JSON options
        builder.Services.ConfigureHttpJsonOptions(opts =>
        {
            opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            opts.SerializerOptions.DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        });

        _app = builder.Build();

        // CORS — allow all (the user controls network access via firewall)
        _app.UseCors(policy => policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader());

        // Static files — web portal is embedded in the exe as resources.
        // A physical wwwroot/ folder (if present) takes priority, allowing
        // custom logo uploads and local overrides without recompiling.
        var wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
        var embeddedProvider = new Microsoft.Extensions.FileProviders.ManifestEmbeddedFileProvider(
            typeof(WebApiService).Assembly, "wwwroot");

        Microsoft.Extensions.FileProviders.IFileProvider fileProvider;
        if (Directory.Exists(wwwroot))
        {
            // Composite: physical folder first (overrides), then embedded fallback
            fileProvider = new Microsoft.Extensions.FileProviders.CompositeFileProvider(
                new Microsoft.Extensions.FileProviders.PhysicalFileProvider(wwwroot),
                embeddedProvider);
        }
        else
        {
            fileProvider = embeddedProvider;
        }

        _app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
        _app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });

        // WebSocket support
        _app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });

        // Auth middleware (skips /api/auth/login and non-API routes)
        _app.UseMiddleware<AuthMiddleware>(_serverDb, _jwt);

        // WebSocket endpoint (before API routes so /ws is handled first)
        _app.Map("/ws", async context => await _wsHub.HandleAsync(context));

        // API routes
        _app.MapApiRoutes(_db, _connMgr, _macroRunner, _scheduler, _serverDb, _jwt, _wsHub);

        // Health check
        _app.MapGet("/health", () => Microsoft.AspNetCore.Http.Results.Ok(new
        {
            status    = "ok",
            version   = "4.0.0",
            uptime    = (DateTime.UtcNow - _startTime).TotalSeconds,
            wsClients = _wsHub.ConnectedClientCount,
        }));

        // Run on a background thread so it doesn't block WPF
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _runTask = Task.Run(async () =>
        {
            try
            {
                var lifetime = _app.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
                lifetime.ApplicationStarted.Register(() => readyTcs.TrySetResult(true));

                _isRunning = true;
                StatusChanged?.Invoke(this, true);
                Log($"API server started on port {Port}");

                await _app.RunAsync();
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
            catch (Exception ex)
            {
                readyTcs.TrySetResult(false);
                LastError = ex.ToString();
                Log($"API server error: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[WebApi] STARTUP FAILED: {ex}");
            }
            finally
            {
                _isRunning = false;
                StatusChanged?.Invoke(this, false);
                Log("API server stopped");
            }
        });

        // Wait for Kestrel to actually bind (or timeout after 10s)
        var ready = await Task.WhenAny(readyTcs.Task, Task.Delay(10_000));
        if (ready != readyTcs.Task)
        {
            // Server didn't start in time
            _isRunning = false;
        }
    }

    private readonly DateTime _startTime = DateTime.UtcNow;

    /// <summary>Gracefully stops the HTTP server.</summary>
    public async Task StopAsync()
    {
        if (!_isRunning || _app == null) return;

        Log("Stopping API server...");
        await _app.StopAsync();

        if (_runTask != null)
        {
            try { await _runTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }

        _wsHub?.Dispose();
        _serverDb?.Dispose();

        _app = null;
        _wsHub = null;
        _serverDb = null;
        _jwt = null;
    }

    private void Log(string message) => LogMessage?.Invoke(this, message);

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}
