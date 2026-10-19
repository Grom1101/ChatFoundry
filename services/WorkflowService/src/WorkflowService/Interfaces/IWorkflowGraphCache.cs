using WorkflowService.Models.Workflow;

namespace WorkflowService.Interfaces;

public interface IWorkflowGraphCache
{
    Task<WorkflowGraph> GetGraphAsync(Guid workflowId, CancellationToken ct = default);
    WorkflowGraph? TryGetCachedGraph(Guid workflowId);
    Task InvalidateAsync(Guid workflowId, CancellationToken ct = default);
}
