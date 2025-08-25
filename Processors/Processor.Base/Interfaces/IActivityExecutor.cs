using Shared.Models;
using Processor.Base.Models;

namespace Processor.Base.Interfaces;

/// <summary>
/// Interface for the abstract activity execution logic that concrete processors must implement
/// </summary>
public interface IActivityExecutor
{
    /// <summary>
    /// Executes an activity with the provided parameters
    /// This method must be implemented by concrete processor applications
    /// </summary>
    /// <param name="processorId">ID of the processor executing the activity</param>
    /// <param name="orchestratedFlowEntityId">ID of the orchestrated flow entity</param>
    /// <param name="stepId">ID of the step being executed</param>
    /// <param name="executionId">Unique execution ID for this activity instance</param>
    /// <param name="publishId">Unique publish ID for this activity instance</param>
    /// <param name="entities">Collection of base entities to process</param>
    /// <param name="inputData">Input data retrieved from cache (validated against InputSchema)</param>
    /// <param name="correlationId">Correlation ID for tracking</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>Collection of result data that will be validated against OutputSchema and saved to cache</returns>
    Task<IEnumerable<ActivityExecutionResult>> ExecuteActivityAsync(
        Guid processorId,
        Guid orchestratedFlowEntityId,
        Guid stepId,
        Guid executionId,
        Guid publishId,
        List<AssignmentModel> entities,
        string inputData,
        Guid correlationId = default,
        CancellationToken cancellationToken = default);
}
