using System.Text.Json;
using Polly;
using Shared.Correlation;
using Shared.Entities;
using Shared.Models;
using Shared.Services.Interfaces;
using System.Diagnostics;

namespace Manager.Orchestrator.Services;

/// <summary>
/// HTTP client for communication with other entity managers with resilience patterns
/// </summary>
public class ManagerHttpClient : IManagerHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ManagerHttpClient> _logger;
    private readonly IConfiguration _configuration;
    private readonly ISchemaValidator _schemaValidator;
    private readonly IAsyncPolicy<HttpResponseMessage> _resilientPolicy;
    private readonly JsonSerializerOptions _jsonOptions;

    public ManagerHttpClient(
        HttpClient httpClient,
        ILogger<ManagerHttpClient> logger,
        IConfiguration configuration,
        ISchemaValidator schemaValidator)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration;
        _schemaValidator = schemaValidator;

        // Configure JSON options
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        // Configure resilience policy
        _resilientPolicy = Policy
            .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .Or<HttpRequestException>()
            .WaitAndRetryAsync(
                retryCount: _configuration.GetValue<int>("HttpClient:MaxRetries", 3),
                sleepDurationProvider: retryAttempt => TimeSpan.FromMilliseconds(
                    _configuration.GetValue<int>("HttpClient:RetryDelayMs", 1000) * Math.Pow(2, retryAttempt - 1)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    _logger.LogWarningWithCorrelation("HTTP request retry {RetryCount} after {Delay}ms. Reason: {Reason}",
                        retryCount, timespan.TotalMilliseconds, outcome.Exception?.Message ?? outcome.Result?.ReasonPhrase);
                });
    }

    public async Task<OrchestratedFlowEntity?> GetOrchestratedFlowAsync(Guid orchestratedFlowId)
    {
        var stopwatch = Stopwatch.StartNew();
        var baseUrl = _configuration["ManagerUrls:OrchestratedFlow"];
        var url = $"{baseUrl}/api/OrchestratedFlow/{orchestratedFlowId}";

        _logger.LogInformationWithCorrelation("Retrieving orchestrated flow. OrchestratedFlowId: {OrchestratedFlowId}, Url: {Url}",
            orchestratedFlowId, url);

        try
        {
            var response = await _resilientPolicy.ExecuteAsync(async () =>
            {
                var requestStopwatch = Stopwatch.StartNew();
                var httpResponse = await _httpClient.GetAsync(url);
                requestStopwatch.Stop();

                _logger.LogInformationWithCorrelation("HTTP request completed. Url: {Url}, StatusCode: {StatusCode}, Duration: {DurationMs}ms",
                    url, httpResponse.StatusCode, requestStopwatch.ElapsedMilliseconds);

                return httpResponse;
            });

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var entity = JsonSerializer.Deserialize<OrchestratedFlowEntity>(content, _jsonOptions);

                stopwatch.Stop();
                _logger.LogInformationWithCorrelation("Successfully retrieved orchestrated flow. OrchestratedFlowId: {OrchestratedFlowId}, WorkflowId: {WorkflowId}, AssignmentCount: {AssignmentCount}, TotalDuration: {TotalDurationMs}ms",
                    orchestratedFlowId, entity?.WorkflowId, entity?.AssignmentIds?.Count ?? 0, stopwatch.ElapsedMilliseconds);

                return entity;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                stopwatch.Stop();
                _logger.LogWarningWithCorrelation("Orchestrated flow not found. OrchestratedFlowId: {OrchestratedFlowId}, TotalDuration: {TotalDurationMs}ms",
                    orchestratedFlowId, stopwatch.ElapsedMilliseconds);
                return null;
            }

            stopwatch.Stop();
            _logger.LogErrorWithCorrelation("Failed to retrieve orchestrated flow. OrchestratedFlowId: {OrchestratedFlowId}, StatusCode: {StatusCode}, Reason: {Reason}, TotalDuration: {TotalDurationMs}ms",
                orchestratedFlowId, response.StatusCode, response.ReasonPhrase, stopwatch.ElapsedMilliseconds);

            throw new HttpRequestException($"Failed to retrieve orchestrated flow: {response.StatusCode} - {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogErrorWithCorrelation(ex, "Error retrieving orchestrated flow. OrchestratedFlowId: {OrchestratedFlowId}, TotalDuration: {TotalDurationMs}ms",
                orchestratedFlowId, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<StepManagerModel> GetStepManagerDataAsync(Guid workflowId)
    {
        var totalStopwatch = Stopwatch.StartNew();
        _logger.LogInformationWithCorrelation("Retrieving step manager data. WorkflowId: {WorkflowId}",
            workflowId);

        try
        {
            // Step 1: Get workflow from Workflow Manager to get step IDs
            var workflowStopwatch = Stopwatch.StartNew();
            var workflowBaseUrl = _configuration["ManagerUrls:Workflow"];
            var workflowUrl = $"{workflowBaseUrl}/api/Workflow/{workflowId}";

            _logger.LogInformationWithCorrelation("Retrieving workflow to get step IDs. WorkflowId: {WorkflowId}, BaseUrl: {BaseUrl}, FullUrl: {Url}",
                workflowId, workflowBaseUrl, workflowUrl);

            var workflowResponse = await _resilientPolicy.ExecuteAsync(async () =>
            {
                var requestStopwatch = Stopwatch.StartNew();
                var httpResponse = await _httpClient.GetAsync(workflowUrl);
                requestStopwatch.Stop();

                _logger.LogInformationWithCorrelation("Workflow HTTP request completed. Url: {Url}, StatusCode: {StatusCode}, Duration: {DurationMs}ms",
                    workflowUrl, httpResponse.StatusCode, requestStopwatch.ElapsedMilliseconds);

                return httpResponse;
            });

            if (!workflowResponse.IsSuccessStatusCode)
            {
                workflowStopwatch.Stop();
                totalStopwatch.Stop();
                _logger.LogErrorWithCorrelation("Failed to retrieve workflow. WorkflowId: {WorkflowId}, StatusCode: {StatusCode}, Reason: {Reason}, WorkflowDuration: {WorkflowDurationMs}ms, TotalDuration: {TotalDurationMs}ms",
                    workflowId, workflowResponse.StatusCode, workflowResponse.ReasonPhrase, workflowStopwatch.ElapsedMilliseconds, totalStopwatch.ElapsedMilliseconds);
                throw new HttpRequestException($"Failed to retrieve workflow: {workflowResponse.StatusCode} - {workflowResponse.ReasonPhrase}");
            }

            var workflowContent = await workflowResponse.Content.ReadAsStringAsync();
            var workflow = JsonSerializer.Deserialize<WorkflowEntity>(workflowContent, _jsonOptions);
            workflowStopwatch.Stop();

            if (workflow == null || !workflow.StepIds.Any())
            {
                totalStopwatch.Stop();
                _logger.LogWarningWithCorrelation("Workflow not found or has no steps. WorkflowId: {WorkflowId}, WorkflowDuration: {WorkflowDurationMs}ms, TotalDuration: {TotalDurationMs}ms",
                    workflowId, workflowStopwatch.ElapsedMilliseconds, totalStopwatch.ElapsedMilliseconds);
                return new StepManagerModel();
            }

            _logger.LogInformationWithCorrelation("Retrieved workflow with {StepCount} steps. WorkflowId: {WorkflowId}, StepIds: {StepIds}, WorkflowDuration: {WorkflowDurationMs}ms",
                workflow.StepIds.Count, workflowId, string.Join(",", workflow.StepIds), workflowStopwatch.ElapsedMilliseconds);

            // Step 2: Get individual steps from Step Manager
            var stepsStopwatch = Stopwatch.StartNew();
            var stepBaseUrl = _configuration["ManagerUrls:Step"];
            var stepTasks = workflow.StepIds.Select(async stepId =>
            {
                var stepUrl = $"{stepBaseUrl}/api/Step/{stepId}";

                var stepResponse = await _resilientPolicy.ExecuteAsync(async () =>
                {
                    var requestStopwatch = Stopwatch.StartNew();
                    var httpResponse = await _httpClient.GetAsync(stepUrl);
                    requestStopwatch.Stop();

                    _logger.LogInformationWithCorrelation("Step HTTP request completed. Url: {Url}, StatusCode: {StatusCode}, Duration: {DurationMs}ms",
                        stepUrl, httpResponse.StatusCode, requestStopwatch.ElapsedMilliseconds);

                    return httpResponse;
                });

                if (stepResponse.IsSuccessStatusCode)
                {
                    var stepContent = await stepResponse.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<StepEntity>(stepContent, _jsonOptions);
                }

                _logger.LogWarningWithCorrelation("Failed to retrieve step. StepId: {StepId}, StatusCode: {StatusCode}",
                    stepId, stepResponse.StatusCode);
                return null;
            });

            var stepResults = await Task.WhenAll(stepTasks);
            var steps = stepResults.Where(s => s != null).Cast<StepEntity>().ToList();
            stepsStopwatch.Stop();

            var model = new StepManagerModel
            {
                ProcessorIds = steps.Select(s => s.ProcessorId).Distinct().ToList(),
                StepIds = steps.Select(s => s.Id).ToList(),
                NextStepIds = steps.SelectMany(s => s.NextStepIds).Distinct().ToList(),
                StepEntities = steps.ToDictionary(s => s.Id, s => s)
            };

            totalStopwatch.Stop();
            _logger.LogInformationWithCorrelation("Successfully retrieved step manager data. WorkflowId: {WorkflowId}, StepCount: {StepCount}, ProcessorCount: {ProcessorCount}, NextStepCount: {NextStepCount}, WorkflowDuration: {WorkflowDurationMs}ms, StepsDuration: {StepsDurationMs}ms, TotalDuration: {TotalDurationMs}ms",
                workflowId, model.StepIds.Count, model.ProcessorIds.Count, model.NextStepIds.Count, workflowStopwatch.ElapsedMilliseconds, stepsStopwatch.ElapsedMilliseconds, totalStopwatch.ElapsedMilliseconds);

            return model;
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            _logger.LogErrorWithCorrelation(ex, "Error retrieving step manager data. WorkflowId: {WorkflowId}, TotalDuration: {TotalDurationMs}ms",
                workflowId, totalStopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<AssignmentManagerModel> GetAssignmentManagerDataAsync(List<Guid> assignmentIds)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var model = new AssignmentManagerModel();

        if (!assignmentIds.Any())
        {
            totalStopwatch.Stop();
            _logger.LogInformationWithCorrelation("No assignment IDs provided, returning empty assignment manager model. Duration: {DurationMs}ms",
                totalStopwatch.ElapsedMilliseconds);
            return model;
        }

        var baseUrl = _configuration["ManagerUrls:Assignment"];

        _logger.LogInformationWithCorrelation("Retrieving assignment manager data. AssignmentIds: {AssignmentIds}",
            string.Join(",", assignmentIds));

        try
        {
            // Get all assignments in parallel
            var assignmentTasks = assignmentIds.Select(async assignmentId =>
            {
                var url = $"{baseUrl}/api/Assignment/{assignmentId}";

                var response = await _resilientPolicy.ExecuteAsync(async () =>
                {
                    var requestStopwatch = Stopwatch.StartNew();
                    var httpResponse = await _httpClient.GetAsync(url);
                    requestStopwatch.Stop();

                    _logger.LogInformationWithCorrelation("Assignment HTTP request completed. Url: {Url}, StatusCode: {StatusCode}, Duration: {DurationMs}ms",
                        url, httpResponse.StatusCode, requestStopwatch.ElapsedMilliseconds);

                    return httpResponse;
                });

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<AssignmentEntity>(content, _jsonOptions);
                }

                _logger.LogWarningWithCorrelation("Failed to retrieve assignment. AssignmentId: {AssignmentId}, StatusCode: {StatusCode}",
                    assignmentId, response.StatusCode);
                return null;
            });

            var assignments = await Task.WhenAll(assignmentTasks);
            var validAssignments = assignments.Where(a => a != null).Cast<AssignmentEntity>().ToList();

            // Group assignments by step ID and process entities
            foreach (var assignment in validAssignments)
            {
                if (!model.Assignments.ContainsKey(assignment.StepId))
                {
                    model.Assignments[assignment.StepId] = new List<AssignmentModel>();
                }

                // Process each entity ID in the assignment
                foreach (var entityId in assignment.EntityIds)
                {
                    var assignmentModel = await ProcessAssignmentEntityAsync(entityId);
                    if (assignmentModel != null)
                    {
                        model.Assignments[assignment.StepId].Add(assignmentModel);
                    }
                }
            }

            var totalAssignments = model.Assignments.Values.Sum(list => list.Count);
            totalStopwatch.Stop();
            _logger.LogInformationWithCorrelation("Successfully retrieved assignment manager data. AssignmentCount: {AssignmentCount}, TotalEntities: {TotalEntities}, TotalDuration: {TotalDurationMs}ms",
                validAssignments.Count, totalAssignments, totalStopwatch.ElapsedMilliseconds);

            return model;
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            _logger.LogErrorWithCorrelation(ex, "Error retrieving assignment manager data. AssignmentIds: {AssignmentIds}, TotalDuration: {TotalDurationMs}ms",
                string.Join(",", assignmentIds), totalStopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private async Task<AssignmentModel?> ProcessAssignmentEntityAsync(Guid entityId)
    {
        try
        {
            // Try to get as address entity first
            var addressAssignmentModel = await TryGetAddressEntityAsync(entityId);
            if (addressAssignmentModel != null)
            {
                return addressAssignmentModel;
            }

            // Try to get as delivery entity
            var deliveryAssignmentModel = await TryGetDeliveryEntityAsync(entityId);
            if (deliveryAssignmentModel != null)
            {
                return deliveryAssignmentModel;
            }

            // Try to get as plugin entity
            var pluginAssignmentModel = await TryGetPluginEntityAsync(entityId);
            if (pluginAssignmentModel != null)
            {
                return pluginAssignmentModel;
            }

            _logger.LogWarningWithCorrelation("Entity not found in Address, Delivery, or Plugin managers. EntityId: {EntityId}", entityId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCorrelation(ex, "Error processing assignment entity. EntityId: {EntityId}", entityId);
            return null;
        }
    }

    private async Task<AddressAssignmentModel?> TryGetAddressEntityAsync(Guid entityId)
    {
        try
        {
            var baseUrl = _configuration["ManagerUrls:Address"];
            var url = $"{baseUrl}/api/Address/{entityId}";

            var response = await _resilientPolicy.ExecuteAsync(async () =>
            {
                return await _httpClient.GetAsync(url);
            });

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var addressEntity = JsonSerializer.Deserialize<AddressEntity>(content, _jsonOptions);

                if (addressEntity != null)
                {
                    // Validate payload against schema (optional for AddressEntity)
                    // Schema validation is automatically skipped if SchemaId is empty (Guid.Empty)
                    // This is expected behavior since AddressEntity schema validation is not mandatory
                    await ValidateEntityPayloadAsync(addressEntity.Payload, addressEntity.SchemaId, "Address", addressEntity.Name, addressEntity.Version);

                    return new AddressAssignmentModel
                    {
                        EntityId = entityId,
                        Name = addressEntity.Name,
                        Version = addressEntity.Version,
                        Payload = addressEntity.Payload,
                        SchemaId = addressEntity.SchemaId,
                        ConnectionString = addressEntity.ConnectionString
                    };
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCorrelation(ex, "Failed to retrieve entity as address. EntityId: {EntityId}", entityId);
            return null;
        }
    }

    private async Task<DeliveryAssignmentModel?> TryGetDeliveryEntityAsync(Guid entityId)
    {
        try
        {
            var baseUrl = _configuration["ManagerUrls:Delivery"];
            var url = $"{baseUrl}/api/Delivery/{entityId}";

            var response = await _resilientPolicy.ExecuteAsync(async () =>
            {
                return await _httpClient.GetAsync(url);
            });

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var deliveryEntity = JsonSerializer.Deserialize<DeliveryEntity>(content, _jsonOptions);

                if (deliveryEntity != null)
                {
                    // Validate payload against schema
                    await ValidateEntityPayloadAsync(deliveryEntity.Payload, deliveryEntity.SchemaId, "Delivery", deliveryEntity.Name, deliveryEntity.Version);

                    return new DeliveryAssignmentModel
                    {
                        EntityId = entityId,
                        Name = deliveryEntity.Name,
                        Version = deliveryEntity.Version,
                        Payload = deliveryEntity.Payload,
                        SchemaId = deliveryEntity.SchemaId
                    };
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCorrelation(ex, "Failed to retrieve entity as delivery. EntityId: {EntityId}", entityId);
            return null;
        }
    }

    private async Task<PluginAssignmentModel?> TryGetPluginEntityAsync(Guid entityId)
    {
        try
        {
            var baseUrl = _configuration["ManagerUrls:Plugin"];
            var url = $"{baseUrl}/api/Plugin/{entityId}";

            var response = await _resilientPolicy.ExecuteAsync(async () =>
            {
                return await _httpClient.GetAsync(url);
            });

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var pluginEntity = JsonSerializer.Deserialize<PluginEntity>(content, _jsonOptions);

                if (pluginEntity != null)
                {
                    // Validate payload against main schema (optional for PluginEntity)
                    // Schema validation is automatically skipped if SchemaId is empty (Guid.Empty)
                    // This is expected behavior since PluginEntity schema validation is not mandatory
                    await ValidateEntityPayloadAsync(pluginEntity.Payload, pluginEntity.SchemaId, "Plugin", pluginEntity.Name, pluginEntity.Version);

                    _logger.LogDebugWithCorrelation("Retrieved plugin entity. EntityId: {EntityId}, InputSchemaId: {InputSchemaId}, OutputSchemaId: {OutputSchemaId}",
                        entityId, pluginEntity.InputSchemaId, pluginEntity.OutputSchemaId);

                    var pluginAssignmentModel = new PluginAssignmentModel
                    {
                        EntityId = entityId,
                        Name = pluginEntity.Name,
                        Version = pluginEntity.Version,
                        Payload = pluginEntity.Payload,
                        SchemaId = pluginEntity.SchemaId,
                        InputSchemaId = pluginEntity.InputSchemaId,
                        OutputSchemaId = pluginEntity.OutputSchemaId,
                        EnableInputValidation = pluginEntity.EnableInputValidation,
                        EnableOutputValidation = pluginEntity.EnableOutputValidation,
                        AssemblyBasePath = pluginEntity.AssemblyBasePath,
                        AssemblyName = pluginEntity.AssemblyName,
                        AssemblyVersion = pluginEntity.AssemblyVersion,
                        TypeName = pluginEntity.TypeName,
                        ExecutionTimeoutMs = pluginEntity.ExecutionTimeoutMs,
                        IsStateless = pluginEntity.IsStateless
                    };

                    // Populate input schema definition if InputSchemaId is not empty
                    if (pluginEntity.InputSchemaId != Guid.Empty)
                    {
                        try
                        {
                            pluginAssignmentModel.InputSchemaDefinition = await GetSchemaDefinitionAsync(pluginEntity.InputSchemaId);
                            _logger.LogDebugWithCorrelation("Successfully retrieved input schema definition for plugin. EntityId: {EntityId}, InputSchemaId: {InputSchemaId}",
                                entityId, pluginEntity.InputSchemaId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarningWithCorrelation(ex, "Failed to retrieve input schema definition for plugin. EntityId: {EntityId}, InputSchemaId: {InputSchemaId}",
                                entityId, pluginEntity.InputSchemaId);
                            // Continue without input schema definition - processor can handle missing schema
                        }
                    }

                    // Populate output schema definition if OutputSchemaId is not empty
                    if (pluginEntity.OutputSchemaId != Guid.Empty)
                    {
                        try
                        {
                            pluginAssignmentModel.OutputSchemaDefinition = await GetSchemaDefinitionAsync(pluginEntity.OutputSchemaId);
                            _logger.LogDebugWithCorrelation("Successfully retrieved output schema definition for plugin. EntityId: {EntityId}, OutputSchemaId: {OutputSchemaId}",
                                entityId, pluginEntity.OutputSchemaId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarningWithCorrelation(ex, "Failed to retrieve output schema definition for plugin. EntityId: {EntityId}, OutputSchemaId: {OutputSchemaId}",
                                entityId, pluginEntity.OutputSchemaId);
                            // Continue without output schema definition - processor can handle missing schema
                        }
                    }

                    return pluginAssignmentModel;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCorrelation(ex, "Failed to retrieve entity as plugin. EntityId: {EntityId}", entityId);
            return null;
        }
    }

    /// <summary>
    /// Validates entity payload against its schema using ISchemaValidator.
    /// Automatically handles optional validation for AddressEntity and PluginEntity when SchemaId is empty.
    /// </summary>
    private async Task ValidateEntityPayloadAsync(string payload, Guid schemaId, string entityType, string name, string version)
    {
        try
        {
            // Skip validation if schemaId is empty (optional for AddressEntity and PluginEntity)
            if (schemaId == Guid.Empty)
            {
                _logger.LogDebugWithCorrelation("Schema validation skipped for {EntityType} entity - SchemaId is empty (expected for optional validation). Name: {Name}, Version: {Version}",
                    entityType, name, version);
                return;
            }

            var schemaDefinition = await GetSchemaDefinitionAsync(schemaId);

            if (string.IsNullOrEmpty(schemaDefinition))
            {
                _logger.LogWarningWithCorrelation("Schema definition is missing for {EntityType} entity. SchemaId: {SchemaId}, Name: {Name}, Version: {Version}",
                    entityType, schemaId, name, version);
                return;
            }

            // Perform actual schema validation using ISchemaValidator
            // Let the schema validator handle empty payload - it should fail validation if payload is required by schema
            var isValid = await _schemaValidator.ValidateAsync(payload, schemaDefinition);

            if (isValid)
            {
                _logger.LogDebugWithCorrelation("Payload validation passed for {EntityType} entity. SchemaId: {SchemaId}, Name: {Name}, Version: {Version}",
                    entityType, schemaId, name, version);
            }
            else
            {
                _logger.LogWarningWithCorrelation("Payload validation failed for {EntityType} entity. SchemaId: {SchemaId}, Name: {Name}, Version: {Version}",
                    entityType, schemaId, name, version);

                // For now, we log the validation failure but don't throw an exception
                // This allows the system to continue processing while logging validation issues
                // In the future, this could be made configurable based on validation policy
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCorrelation(ex, "Failed to validate payload for {EntityType} entity. SchemaId: {SchemaId}, Name: {Name}, Version: {Version}",
                entityType, schemaId, name, version);

            // Don't throw exception to avoid breaking the processing pipeline
            // Validation errors are logged for monitoring and debugging
        }
    }

    public async Task<string> GetSchemaDefinitionAsync(Guid schemaId)
    {
        var baseUrl = _configuration["ManagerUrls:Schema"];
        var url = $"{baseUrl}/api/Schema/{schemaId}";

        _logger.LogDebugWithCorrelation("Retrieving schema definition. SchemaId: {SchemaId}, Url: {Url}",
            schemaId, url);

        try
        {
            var response = await _resilientPolicy.ExecuteAsync(async () =>
            {
                return await _httpClient.GetAsync(url);
            });

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var schemaEntity = JsonSerializer.Deserialize<SchemaEntity>(content, _jsonOptions);

                _logger.LogDebugWithCorrelation("Successfully retrieved schema definition. SchemaId: {SchemaId}, DefinitionLength: {DefinitionLength}",
                    schemaId, schemaEntity?.Definition?.Length ?? 0);

                var definition = schemaEntity?.Definition ?? string.Empty;

                // Check if the definition is JSON-escaped (starts with quotes and contains escaped quotes)
                if (!string.IsNullOrEmpty(definition) && definition.StartsWith("\"") && definition.Contains("\\\""))
                {
                    try
                    {
                        // The definition is JSON-escaped, so we need to deserialize it to get the raw JSON
                        var unescapedDefinition = JsonSerializer.Deserialize<string>(definition);
                        _logger.LogDebugWithCorrelation("Schema definition was JSON-escaped, unescaped successfully. SchemaId: {SchemaId}, OriginalLength: {OriginalLength}, UnescapedLength: {UnescapedLength}",
                            schemaId, definition.Length, unescapedDefinition?.Length ?? 0);
                        return unescapedDefinition ?? string.Empty;
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarningWithCorrelation(ex, "Failed to unescape JSON-escaped schema definition, returning as-is. SchemaId: {SchemaId}",
                            schemaId);
                        return definition;
                    }
                }

                return definition;
            }

            _logger.LogWarningWithCorrelation("Failed to retrieve schema definition. SchemaId: {SchemaId}, StatusCode: {StatusCode}",
                schemaId, response.StatusCode);

            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCorrelation(ex, "Error retrieving schema definition. SchemaId: {SchemaId}",
                schemaId);
            return string.Empty;
        }
    }
}
