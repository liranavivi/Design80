using Polly;
using Polly.Extensions.Http;
using Shared.Correlation;

namespace Manager.OrchestratedFlow.Services;

/// <summary>
/// HTTP client for communicating with other managers with resilience patterns
/// </summary>
public class ManagerHttpClient : IManagerHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ManagerHttpClient> _logger;
    private readonly string _workflowManagerBaseUrl;
    private readonly string _assignmentManagerBaseUrl;
    private readonly IAsyncPolicy<HttpResponseMessage> _resilientPolicy;

    public ManagerHttpClient(
        HttpClient httpClient,
        ILogger<ManagerHttpClient> logger,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        
        // Get manager URLs from configuration (lazy resolution)
        _workflowManagerBaseUrl = configuration["ManagerUrls:Workflow"] ?? "http://localhost:5180";
        _assignmentManagerBaseUrl = configuration["ManagerUrls:Assignment"] ?? "http://localhost:5130";

        // Create resilient policy for HTTP calls
        _resilientPolicy = Policy
            .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .Or<HttpRequestException>()
            .Or<TaskCanceledException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    _logger.LogWarningWithCorrelation("HTTP request failed, retrying in {Delay}ms. Attempt {RetryCount}/3. Reason: {Reason}",
                        timespan.TotalMilliseconds, retryCount, outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString());
                });
    }

    /// <summary>
    /// Validate that a workflow exists in the Workflow Manager
    /// </summary>
    /// <param name="workflowId">The workflow ID to validate</param>
    /// <returns>True if workflow exists, false otherwise</returns>
    public async Task<bool> ValidateWorkflowExistsAsync(Guid workflowId)
    {
        _logger.LogInformationWithCorrelation("Starting workflow existence validation. WorkflowId: {WorkflowId}", workflowId);

        try
        {
            var url = $"{_workflowManagerBaseUrl}/api/workflow/{workflowId}";
            var response = await _resilientPolicy.ExecuteAsync(async () =>
            {
                var httpResponse = await _httpClient.GetAsync(url);
                
                if (httpResponse.IsSuccessStatusCode || httpResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return httpResponse;
                }

                _logger.LogWarningWithCorrelation("Workflow validation returned non-success status. WorkflowId: {WorkflowId}, StatusCode: {StatusCode}, URL: {Url}",
                    workflowId, httpResponse.StatusCode, url);
                
                // For non-success status codes (except 404), we'll treat as service unavailable
                throw new HttpRequestException($"Service returned {httpResponse.StatusCode}");
            });

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformationWithCorrelation("Successfully validated workflow exists. WorkflowId: {WorkflowId}", workflowId);
                return true;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarningWithCorrelation("Workflow not found. WorkflowId: {WorkflowId}", workflowId);
                return false;
            }

            // This shouldn't happen due to the policy above, but just in case
            _logger.LogWarningWithCorrelation("Unexpected response status. WorkflowId: {WorkflowId}, StatusCode: {StatusCode}",
                workflowId, response.StatusCode);
            return false;
        }
        catch (HttpRequestException ex)
        {
            // Fail-safe: if service is unavailable, assume workflow doesn't exist
            _logger.LogErrorWithCorrelation(ex, "HTTP error validating workflow existence - service may be unavailable. WorkflowId: {WorkflowId}",
                workflowId);
            return false;
        }
        catch (TaskCanceledException ex)
        {
            // Fail-safe: if request times out, assume workflow doesn't exist
            _logger.LogErrorWithCorrelation(ex, "Timeout validating workflow existence. WorkflowId: {WorkflowId}", workflowId);
            return false;
        }
        catch (Exception ex)
        {
            // Fail-safe: if any other error occurs, assume workflow doesn't exist
            _logger.LogErrorWithCorrelation(ex, "Unexpected error validating workflow existence. WorkflowId: {WorkflowId}", workflowId);
            return false;
        }
    }

    /// <summary>
    /// Validate that an assignment exists in the Assignment Manager
    /// </summary>
    /// <param name="assignmentId">The assignment ID to validate</param>
    /// <returns>True if assignment exists, false otherwise</returns>
    public async Task<bool> ValidateAssignmentExistsAsync(Guid assignmentId)
    {
        _logger.LogInformationWithCorrelation("Starting assignment existence validation. AssignmentId: {AssignmentId}", assignmentId);

        try
        {
            var url = $"{_assignmentManagerBaseUrl}/api/assignment/{assignmentId}";
            var response = await _resilientPolicy.ExecuteAsync(async () =>
            {
                var httpResponse = await _httpClient.GetAsync(url);
                
                if (httpResponse.IsSuccessStatusCode || httpResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return httpResponse;
                }

                _logger.LogWarningWithCorrelation("Assignment validation returned non-success status. AssignmentId: {AssignmentId}, StatusCode: {StatusCode}, URL: {Url}",
                    assignmentId, httpResponse.StatusCode, url);
                
                // For non-success status codes (except 404), we'll treat as service unavailable
                throw new HttpRequestException($"Service returned {httpResponse.StatusCode}");
            });

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformationWithCorrelation("Successfully validated assignment exists. AssignmentId: {AssignmentId}", assignmentId);
                return true;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarningWithCorrelation("Assignment not found. AssignmentId: {AssignmentId}", assignmentId);
                return false;
            }

            // This shouldn't happen due to the policy above, but just in case
            _logger.LogWarningWithCorrelation("Unexpected response status. AssignmentId: {AssignmentId}, StatusCode: {StatusCode}",
                assignmentId, response.StatusCode);
            return false;
        }
        catch (HttpRequestException ex)
        {
            // Fail-safe: if service is unavailable, assume assignment doesn't exist
            _logger.LogErrorWithCorrelation(ex, "HTTP error validating assignment existence - service may be unavailable. AssignmentId: {AssignmentId}",
                assignmentId);
            return false;
        }
        catch (TaskCanceledException ex)
        {
            // Fail-safe: if request times out, assume assignment doesn't exist
            _logger.LogErrorWithCorrelation(ex, "Timeout validating assignment existence. AssignmentId: {AssignmentId}", assignmentId);
            return false;
        }
        catch (Exception ex)
        {
            // Fail-safe: if any other error occurs, assume assignment doesn't exist
            _logger.LogErrorWithCorrelation(ex, "Unexpected error validating assignment existence. AssignmentId: {AssignmentId}", assignmentId);
            return false;
        }
    }

    /// <summary>
    /// Validate that multiple assignments exist in the Assignment Manager
    /// </summary>
    /// <param name="assignmentIds">The assignment IDs to validate</param>
    /// <returns>True if all assignments exist, false otherwise</returns>
    public async Task<bool> ValidateAssignmentsExistAsync(IEnumerable<Guid> assignmentIds)
    {
        if (assignmentIds == null || !assignmentIds.Any())
        {
            _logger.LogInformationWithCorrelation("No assignment IDs provided for validation - returning true");
            return true;
        }

        var assignmentIdsList = assignmentIds.ToList();
        _logger.LogInformationWithCorrelation("Starting batch assignment existence validation. AssignmentIds: {AssignmentIds}", 
            string.Join(",", assignmentIdsList));

        // Validate all assignments in parallel for performance
        var validationTasks = assignmentIdsList.Select(ValidateAssignmentExistsAsync);
        var results = await Task.WhenAll(validationTasks);

        var allExist = results.All(exists => exists);
        
        _logger.LogInformationWithCorrelation("Completed batch assignment existence validation. AssignmentIds: {AssignmentIds}, AllExist: {AllExist}", 
            string.Join(",", assignmentIdsList), allExist);

        return allExist;
    }
}
