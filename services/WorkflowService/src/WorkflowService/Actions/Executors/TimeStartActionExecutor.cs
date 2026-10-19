using MassTransit;
using Shared.Application.Events;
using WorkflowService.Entities;
using WorkflowService.Enums;
using WorkflowService.Events;

namespace WorkflowService.Actions.Executors;

public class TimeStartActionExecutor(ITopicProducer<string, ActionCompletedEvent> actionCompletedProducer
) : IActionExecutor
{
    public WorkflowNodeType WorkflowNodeType => WorkflowNodeType.TimerStart;

    public async Task ExecuteAsync(ActionEntity action, ExecuteActionCommand message, CancellationToken ct)
    {
        Console.WriteLine("Start timer message");
        var partitionKey = message.ChannelId.HasValue ? $"{message.ChannelId.Value}:{message.ExternalUserId}" : $"{message.Channel}:{message.ExternalUserId}";
        await actionCompletedProducer.Produce(partitionKey, new ActionCompletedEvent(action.Id, Success: true), ct);
    }
}