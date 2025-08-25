using Polly;
using Polly.Extensions.Http;
using Shared.Correlation;

namespace Manager.Assignment.Services;

/// <summary>
/// HTTP client service for communicating with other managers for validation
/// </summary>
public class ManagerHttpClient : IManagerHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ManagerHttpClient> _logger;
    private readonly string _stepManagerBaseUrl;
    private readonly string _orchestratedFlowManagerBaseUrl;
    private readonly IAsyncPolicy<HttpResponseMessage> _resilientPolicy;

    public ManagerHttpClient(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<ManagerHttpClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        // Get manager URLs from configuration
        _stepManagerBaseUrl = configuration["ManagerUrls:Step"] ?? "http://localhost:5170";
        _orchestratedFlowManagerBaseUrl = configuration["ManagerUrls:OrchestratedFlow"] ?? "http://localhost:5140";
        
        // Create resilient policy with retry and circuit breaker
        var retryPolicy = HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    _logger.LogWarningWithCorrelation("Retry {RetryCount} for HTTP request after {Delay}ms. Reason: {Reason}",
                        retryCount, timespan.TotalMilliseconds, outcome.Exception?.Message ?? outcome.Result?.ReasonPhrase);
                });

        var circuitBreakerPolicy = HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (exception, duration) =>
                {
                    _logger.LogWarningWithCorrelation("Circuit breaker opened for {Duration}s. Reason: {Reason}",
                        duration.TotalSeconds, exception.Exception?.Message ?? exception.Result?.ReasonPhrase);
                },
                onReset: () =>
                {
                    _logger.LogInformationWithCorrelation("Circuit breaker reset - requests will be allowed again");
                });

        _resilientPolicy = Policy.WrapAsync(retryPolicy, circuitBreakerPolicy);
    }

    public async Task<bool> CheckStepExists(Guid stepId)
    {
        var url = $"{_stepManagerBaseUrl}/api/step/{stepId}/exists";
        return await ExecuteStepExistenceCheck(url, "StepExistenceCheck", stepId);
    }

    public async Task<bool> CheckEntityExists(Guid entityId)
    {
        // For now, we'll implement a basic validation that checks if the GUID is not empty
        // In a full implementation, this would route to the appropriate manager based on entity type
        // This could be enhanced to check Address, Delivery, Processor, etc. managers

        _logger.LogDebugWithCorrelation("Basic entity existence check for EntityId: {EntityId}", entityId);

        // For demonstration purposes, we'll consider any non-empty GUID as "existing"
        // In reality, this would need to determine the entity type and call the appropriate manager
        if (entityId == Guid.Empty)
        {
            _logger.LogWarningWithCorrelation("Entity existence check failed - empty GUID. EntityId: {EntityId}", entityId);
            return false;
        }

        // Simulate async operation for consistency
        await Task.Delay(1);

        // Simulate a basic check - in reality this would call the appropriate manager
        // For now, we'll assume entities exist if they're not empty GUIDs
        _logger.LogDebugWithCorrelation("Entity existence check passed (basic validation). EntityId: {EntityId}", entityId);
        return true;
    }

    private async Task<bool> ExecuteStepExistenceCheck(string url, string operationName, Guid stepId)
    {
        _logger.LogDebugWithCorrelation("Starting {OperationName} for StepId: {StepId}, URL: {Url}", 
            operationName, stepId, url);

        try
        {
            var response = await _resilientPolicy.ExecuteAsync(async () =>
            {
                var httpResponse = await _httpClient.GetAsync(url);
                
                if (httpResponse.IsSuccessStatusCode)
                {
                    return httpResponse;
                }

                _logger.LogWarningWithCorrelation("{OperationName} returned non-success status. StepId: {StepId}, StatusCode: {StatusCode}, URL: {Url}",
                    operationName, stepId, httpResponse.StatusCode, url);
                
                // For non-success status codes, we'll treat as service unavailable
                throw new HttpRequestException($"Service returned {httpResponse.StatusCode}");
            });

            var content = await response.Content.ReadAsStringAsync();
            var exists = bool.Parse(content);

            _logger.LogDebugWithCorrelation("{OperationName} completed successfully. StepId: {StepId}, Exists: {Exists}",
                operationName, stepId, exists);

            return exists;
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException ex)
        {
            _logger.LogErrorWithCorrelation(ex, "{OperationName} failed - circuit breaker is open. StepId: {StepId}",
                operationName, stepId);

            // Fail-safe: If service unavailable, reject operation for data integrity
            throw new InvalidOperationException($"Step validation service unavailable (circuit breaker open). Operation rejected for data integrity.");
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCorrelation(ex, "{OperationName} failed. StepId: {StepId}, URL: {Url}",
                operationName, stepId, url);
            
            // Fail-safe: If service unavailable, reject operation for data integrity
            throw new InvalidOperationException($"Step validation service unavailable. Operation rejected for data integrity: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if any OrchestratedFlow entities reference the specified assignment ID
    /// </summary>
    /// <param name="assignmentId">The assignment ID to check for references</param>
    /// <returns>True if any OrchestratedFlow entities reference the assignment, false otherwise</returns>
    public async Task<bool> CheckAssignmentReferencesAsync(Guid assignmentId)
    {
        _logger.LogInformationWithCorrelation("Starting assignment reference validation. AssignmentId: {AssignmentId}", assignmentId);

        try
        {
            var url = $"{_orchestratedFlowManagerBaseUrl}/api/orchestratedflow/assignment/{assignmentId}/exists";
            var response = await _resilientPolicy.ExecuteAsync(async () =>
            {
                var httpResponse = await _httpClient.GetAsync(url);

                if (httpResponse.IsSuccessStatusCode)
                {
                    return httpResponse;
                }

                _logger.LogWarningWithCorrelation("Assignment reference validation returned non-success status. AssignmentId: {AssignmentId}, StatusCode: {StatusCode}, URL: {Url}",
                    assignmentId, httpResponse.StatusCode, url);

                // For non-success status codes, we'll treat as service unavailable
                throw new HttpRequestException($"Service returned {httpResponse.StatusCode}");
            });

            var content = await response.Content.ReadAsStringAsync();
            var hasReferences = bool.Parse(content);

            _logger.LogInformationWithCorrelation("Successfully validated assignment references. AssignmentId: {AssignmentId}, HasReferences: {HasReferences}",
                assignmentId, hasReferences);

            return hasReferences;
        }
        catch (HttpRequestException ex)
        {
            // Fail-safe: if service is unavailable, assume there are references
            _logger.LogErrorWithCorrelation(ex, "HTTP error validating assignment references - service may be unavailable. AssignmentId: {AssignmentId}",
                assignmentId);
            return true;
        }
        catch (TaskCanceledException ex)
        {
            // Fail-safe: if request times out, assume there are references
            _logger.LogErrorWithCorrelation(ex, "Timeout validating assignment references. AssignmentId: {AssignmentId}", assignmentId);
            return true;
        }
        catch (Exception ex)
        {
            // Fail-safe: if any other error occurs, assume there are references
            _logger.LogErrorWithCorrelation(ex, "Unexpected error validating assignment references. AssignmentId: {AssignmentId}", assignmentId);
            return true;
        }
    }
}
