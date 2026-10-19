using Scheduler.Grpc;
using WorkflowService.Entities;
using WorkflowService.Enums;
using WorkflowService.Events;
using WorkflowService.Interfaces;
using WorkflowService.Models.Node;
using WorkflowService.Utils;

namespace WorkflowService.Actions.Executors;

public class WebhookWaitActionExecutor(
    ISessionRepository sessionRepository,
    IWorkflowGraphCache graphCache,
    SchedulerGrpcService.SchedulerGrpcServiceClient? schedulerClient = null)
    : IActionExecutor
{
    public WorkflowNodeType WorkflowNodeType => WorkflowNodeType.WebhookWait;

    public async Task ExecuteAsync(ActionEntity action, ExecuteActionCommand message, CancellationToken ct)
    {
        var session = action.Session ?? await sessionRepository.GetAsync(action.SessionId, ct);
        if (session == null)
            return;

        var graph = await graphCache.GetGraphAsync(session.WorkflowId, ct);
        var node = graph.GetNode(session.CurrentNodeId!.Value);

        if (node.Data is not WebhookWaitNodeData waitData)
            throw new InvalidOperationException($"Node {node.Id} is not a WebhookWait node");

        if (waitData.TimeoutSeconds > 0 && schedulerClient != null)
        {
            var fireAt = DateTimeOffset.UtcNow.AddSeconds(waitData.TimeoutSeconds);
            var jobKey = $"webhook-timeout:{action.Id}";

            var request = new ScheduleWaitJobRequest
            {
                JobKey = jobKey,
                FireAtUnixMs = fireAt.ToUnixTimeMilliseconds(),
                Channel = session.Channel.ToString(),
                ClientId = session.ClientId,
                CompanyId = session.Workflow.Bot.CompanyId.ToString(),
                ActionId = action.Id.ToString(),
            };

            await schedulerClient.ScheduleWaitJobAsync(request, cancellationToken: ct);
        }
    }
}
