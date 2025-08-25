using System.Diagnostics;
using Shared.Correlation;

namespace Manager.Orchestrator.Services;

/// <summary>
/// Service that pre-warms HTTP client connections to all manager services during application startup.
/// This significantly reduces the "cold start" delay for the first HTTP requests by establishing
/// TCP connections, performing SSL handshakes, and warming up the connection pool.
/// </summary>
public class HttpClientWarmupService : IHostedService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<HttpClientWarmupService> _logger;
    private readonly SemaphoreSlim _concurrencyLimiter;
    private WarmupMetrics _lastWarmupMetrics = new();

    public HttpClientWarmupService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<HttpClientWarmupService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _concurrencyLimiter = new SemaphoreSlim(5, 5); // Limit concurrent warmup requests
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCorrelation("🔥 Starting HTTP client connection pre-warming...");
        
        var overallStopwatch = Stopwatch.StartNew();
        var managerUrls = GetAllManagerUrls();
        
        if (!managerUrls.Any())
        {
            _logger.LogWarningWithCorrelation("No manager URLs found for pre-warming");
            return;
        }

        var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(15);

        var warmupTasks = managerUrls.Select(async managerInfo =>
        {
            await _concurrencyLimiter.WaitAsync(cancellationToken);
            try
            {
                return await WarmupConnectionWithRetry(httpClient, managerInfo.Name, managerInfo.Url, cancellationToken);
            }
            finally
            {
                _concurrencyLimiter.Release();
            }
        });

        var results = await Task.WhenAll(warmupTasks);
        overallStopwatch.Stop();

        // Collect metrics
        _lastWarmupMetrics = new WarmupMetrics
        {
            TotalManagers = managerUrls.Count,
            SuccessfulWarmups = results.Count(r => r.Success),
            FailedWarmups = results.Count(r => !r.Success),
            TotalDuration = overallStopwatch.Elapsed,
            ManagerDurations = results.ToDictionary(r => r.ManagerName, r => r.Duration),
            FailedManagers = results.Where(r => !r.Success).Select(r => r.ManagerName).ToList()
        };

        var successCount = _lastWarmupMetrics.SuccessfulWarmups;
        var totalCount = _lastWarmupMetrics.TotalManagers;
        
        if (successCount == totalCount)
        {
            _logger.LogInformationWithCorrelation(
                "🎯 HTTP client pre-warming completed successfully: {SuccessCount}/{TotalCount} managers warmed up in {TotalDurationMs}ms",
                successCount, totalCount, overallStopwatch.ElapsedMilliseconds);
        }
        else if (successCount > 0)
        {
            _logger.LogWarningWithCorrelation(
                "⚠️ HTTP client pre-warming partially completed: {SuccessCount}/{TotalCount} managers warmed up in {TotalDurationMs}ms. Failed: [{FailedManagers}]",
                successCount, totalCount, overallStopwatch.ElapsedMilliseconds, string.Join(", ", _lastWarmupMetrics.FailedManagers));
        }
        else
        {
            _logger.LogErrorWithCorrelation(
                "❌ HTTP client pre-warming failed: 0/{TotalCount} managers warmed up in {TotalDurationMs}ms",
                totalCount, overallStopwatch.ElapsedMilliseconds);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Gets the metrics from the last warmup operation
    /// </summary>
    public WarmupMetrics GetLastWarmupMetrics() => _lastWarmupMetrics;

    private List<(string Name, string Url)> GetAllManagerUrls()
    {
        var managerUrls = new List<(string Name, string Url)>();
        var managerUrlsSection = _configuration.GetSection("ManagerUrls");

        if (!managerUrlsSection.Exists())
        {
            _logger.LogWarningWithCorrelation("ManagerUrls configuration section not found");
            return managerUrls;
        }

        foreach (var child in managerUrlsSection.GetChildren())
        {
            var url = child.Value;
            if (!string.IsNullOrWhiteSpace(url))
            {
                managerUrls.Add((child.Key, url));
                _logger.LogDebugWithCorrelation("Discovered manager URL: {ManagerName} -> {Url}", child.Key, url);
            }
        }

        _logger.LogInformationWithCorrelation("Discovered {Count} manager URLs for pre-warming: [{ManagerNames}]", 
            managerUrls.Count, string.Join(", ", managerUrls.Select(m => m.Name)));
        
        return managerUrls;
    }

    private async Task<WarmupResult> WarmupConnectionWithRetry(HttpClient httpClient, string managerName, string baseUrl, 
        CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        var delays = new[] { 100, 500, 1000 }; // Progressive delays in milliseconds
        var overallStopwatch = Stopwatch.StartNew();

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var result = await WarmupConnection(httpClient, managerName, baseUrl, cancellationToken);
                overallStopwatch.Stop();
                
                if (result.Success)
                {
                    return new WarmupResult 
                    { 
                        ManagerName = managerName, 
                        Success = true, 
                        Duration = overallStopwatch.Elapsed,
                        AttemptCount = attempt
                    };
                }
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogDebugWithCorrelation(
                    "Warmup attempt {Attempt}/{MaxRetries} failed for {ManagerName}: {Error}. Retrying in {DelayMs}ms...",
                    attempt, maxRetries, managerName, ex.Message, delays[attempt - 1]);
                
                await Task.Delay(delays[attempt - 1], cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarningWithCorrelation(ex, 
                    "❌ Final warmup attempt {Attempt}/{MaxRetries} failed for {ManagerName}",
                    attempt, maxRetries, managerName);
            }
        }

        overallStopwatch.Stop();
        return new WarmupResult 
        { 
            ManagerName = managerName, 
            Success = false, 
            Duration = overallStopwatch.Elapsed,
            AttemptCount = maxRetries
        };
    }

    private async Task<WarmupResult> WarmupConnection(HttpClient httpClient, string managerName, string baseUrl,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // Try multiple endpoints in order of preference
        var warmupEndpoints = new[]
        {
            "/health",           // Standard health check
            "/api/health",       // Alternative health check
            "/",                 // Root endpoint
            ""                   // Base URL only
        };

        Exception? lastException = null;

        foreach (var endpoint in warmupEndpoints)
        {
            try
            {
                var warmupUrl = $"{baseUrl.TrimEnd('/')}{endpoint}";
                _logger.LogDebugWithCorrelation("Attempting warmup for {ManagerName} at {Url}", managerName, warmupUrl);

                var response = await httpClient.GetAsync(warmupUrl, cancellationToken);
                stopwatch.Stop();

                _logger.LogInformationWithCorrelation(
                    "✅ Warmed up {ManagerName} connection. Url: {Url}, Status: {StatusCode}, Duration: {DurationMs}ms",
                    managerName, warmupUrl, response.StatusCode, stopwatch.ElapsedMilliseconds);

                return new WarmupResult
                {
                    ManagerName = managerName,
                    Success = true,
                    Duration = stopwatch.Elapsed,
                    AttemptCount = 1
                };
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogDebugWithCorrelation("Warmup attempt failed for {ManagerName} at endpoint {Endpoint}: {Error}",
                    managerName, endpoint, ex.Message);
            }
        }

        stopwatch.Stop();
        _logger.LogWarningWithCorrelation("❌ Failed to warm up {ManagerName} connection after trying all endpoints. " +
            "Duration: {DurationMs}ms, LastError: {Error}",
            managerName, stopwatch.ElapsedMilliseconds, lastException?.Message);

        return new WarmupResult
        {
            ManagerName = managerName,
            Success = false,
            Duration = stopwatch.Elapsed,
            AttemptCount = 1
        };
    }
}

/// <summary>
/// Metrics collected during the warmup process
/// </summary>
public class WarmupMetrics
{
    public int TotalManagers { get; set; }
    public int SuccessfulWarmups { get; set; }
    public int FailedWarmups { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public Dictionary<string, TimeSpan> ManagerDurations { get; set; } = new();
    public List<string> FailedManagers { get; set; } = new();
    
    public double SuccessRate => TotalManagers > 0 ? (double)SuccessfulWarmups / TotalManagers * 100 : 0;
}

/// <summary>
/// Result of a single manager warmup operation
/// </summary>
public class WarmupResult
{
    public string ManagerName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public TimeSpan Duration { get; set; }
    public int AttemptCount { get; set; }
}
