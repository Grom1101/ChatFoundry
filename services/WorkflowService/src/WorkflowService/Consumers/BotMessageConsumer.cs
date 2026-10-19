using MassTransit;
using Microsoft.Extensions.Caching.Distributed;
using Shared.Application.Events;
using WorkflowService.Entities;
using WorkflowService.Events;
using WorkflowService.Interfaces;
using WorkflowService.Utils;

using WorkflowService.Actions.Factories;

namespace WorkflowService.Consumers;

public class BotMessageConsumer(
    IBotRepository botRepository,
    ISessionResolver sessionResolver,
    ISessionRepository sessionRepository,
    IActionFactory actionFactory,
    IActionRepository actionRepository,
    IActionExecutorFactory executorFactory,
    ITopicProducer<string, ExecuteActionCommand> producer,
    IWorkflowGraphCache graphCache,
    IDistributedCache distributedCache) : IConsumer<BotIncomingMessage>
{
    public async Task Consume(ConsumeContext<BotIncomingMessage> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var redisKey = $"livechat:{msg.ChannelId}:{msg.ExternalUserId}";
        var isActiveLiveChat = await distributedCache.GetStringAsync(redisKey, ct);
        if (isActiveLiveChat != null)
            return;

        var botIds = await botRepository.GetBotIdsByChannelIdAsync(msg.ChannelId, ct);
        if (botIds.Count == 0)
            return;

        foreach (var botId in botIds)
        {
            Session? session = null;
            try
            {
                var existingSession = await sessionRepository.FindActiveAsync(msg.ChannelId, msg.ExternalUserId, botId, ct);
                var isNewSession = existingSession == null;

                session = await sessionResolver.ResolveForBotAsync(msg, botId, ct);

                var graph = await graphCache.GetGraphAsync(session.WorkflowId, ct);
                var currentNode = graph.GetNode(session.CurrentNodeId!.Value);

                var partitionKey = $"{msg.ChannelId}:{msg.ExternalUserId}";

                if (isNewSession)
                {
                    var action = await actionFactory.CreateAsync(
                        session,
                        currentNode,
                        currentNode.Type,
                        msg.Payload,
                        msg.MessageKind,
                        ct);
                    await actionRepository.AddAsync(action, ct);

                    await producer.Produce(partitionKey, new ExecuteActionCommand(action.Id, msg.ExternalUserId, msg.Channel, msg.ChannelId), ct);
                }
                else
                {
                    var executor = executorFactory.Get(currentNode.Type);
                    if (!executor.CanHandleUserInput)
                    {
                        continue;
                    }

                    await executor.HandleUserInputAsync(session, currentNode, msg, ct);
                }
            }
            catch
            {
                if (session != null)
                    await sessionResolver.CloseSessionAndHierarchyAsync(session.Id, ct);
            }
        }
    }
}

