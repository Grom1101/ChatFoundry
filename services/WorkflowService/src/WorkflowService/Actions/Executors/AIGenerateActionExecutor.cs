using MassTransit;
using Shared.Application.Events;
using WorkflowService.Entities;
using WorkflowService.Enums;
using WorkflowService.Events;
using WorkflowService.Interfaces;
using WorkflowService.Models.Node;
using WorkflowService.Models.Workflow;
using WorkflowService.Services;
using WorkflowService.Utils;

namespace WorkflowService.Actions.Executors;

public class AIGenerateActionExecutor(
    IOpenAiService openAiService,
    ISessionRepository sessionRepository,
    IActionRepository actionRepository,
    IVariableService variableService,
    ITopicProducer<string, ActionCompletedEvent> producer,
    ITopicProducer<string, AiTokensConsumedEvent> aiTokensProducer,
    IWorkflowGraphCache graphCache,
    WorkflowTextRenderer workflowTextRenderer,
    BillingQuotaGuard billingQuotaGuard) : IActionExecutor
{
    public WorkflowNodeType WorkflowNodeType => WorkflowNodeType.AIGenerate;

    public async Task ExecuteAsync(ActionEntity action, ExecuteActionCommand message, CancellationToken ct)
    {
        var session = action.Session ?? await sessionRepository.GetAsync(action.SessionId, ct);
        if (session is null)
            return;

        var graph = await graphCache.GetGraphAsync(session.WorkflowId, ct);
        var node = graph.GetNode(session.CurrentNodeId!.Value);

        if (node.Data is not AIGenerateNodeData aiData || string.IsNullOrWhiteSpace(aiData.Prompt))
        {
            var partitionKey = $"{session.ChannelId}:{message.ExternalUserId}";
            await producer.Produce(partitionKey, new ActionCompletedEvent(action.Id, Success: true), ct);
            return;
        }

        var resolvedPrompt = workflowTextRenderer.RenderText(aiData.Prompt, session);

        try
        {
            await billingQuotaGuard.EnsureQuotaAsync(
                graph.CompanyId, "ai_tokens", 0, ct);
        }
        catch (InvalidOperationException ex)
        {
            variableService.SetVariable(session, $"$node.{node.Id}.statusCode", 429);
            variableService.SetVariable(session, $"$node.{node.Id}.success", false);
            await variableService.SyncIfDirtyAsync(session, ct);
            await sessionRepository.SaveAsync(session, ct);

            if (node.Data is not IContinueOnError { ContinueOnError: true }) throw;

            var partitionKeyErr = $"{session.ChannelId}:{message.ExternalUserId}";
            await producer.Produce(partitionKeyErr, new ActionCompletedEvent(action.Id, Success: false), ct);
            return;
        }

        List<(string Role, string Content)>? chatHistory = null;
        if (aiData.IncludeChatContext)
        {
            var actions = await actionRepository.GetBySessionIdAsync(session.Id, ct);
            chatHistory = BuildChatHistoryFromActions(actions, action.Id, session, graph);
            chatHistory.Insert(0, BuildSystemPrompt(session));
        }

        AiCompletionResult aiResult;
        try
        {
            aiResult = await openAiService.GetCompletionAsync(resolvedPrompt, chatHistory, ct);
        }
        catch (HttpRequestException ex)
        {
            var statusCode = (int)(ex.StatusCode ?? System.Net.HttpStatusCode.InternalServerError);
            variableService.SetVariable(session, $"$node.{node.Id}.statusCode", statusCode);
            variableService.SetVariable(session, $"$node.{node.Id}.success", false);
            await variableService.SyncIfDirtyAsync(session, ct);
            await sessionRepository.SaveAsync(session, ct);

            if (!aiData.ContinueOnError) throw;

            var partitionKeyHttpErr = $"{session.ChannelId}:{message.ExternalUserId}";
            await producer.Produce(partitionKeyHttpErr, new ActionCompletedEvent(action.Id, Success: false), ct);
            return;
        }

        variableService.SetVariable(session, $"$node.{node.Id}.output", aiResult.Content);
        variableService.SetVariable(session, $"$node.{node.Id}.statusCode", 200);
        variableService.SetVariable(session, $"$node.{node.Id}.success", true);

        await variableService.SyncIfDirtyAsync(session, ct);
        await sessionRepository.SaveAsync(session, ct);

        if (aiResult.TotalTokens > 0 && graph.CompanyId != Guid.Empty)
        {
            var billingPartitionKey = graph.CompanyId.ToString();
            await aiTokensProducer.Produce(billingPartitionKey, new AiTokensConsumedEvent(
                graph.CompanyId,
                aiResult.TotalTokens,
                action.Id), ct);
        }

        var partitionKeySuccess = $"{session.ChannelId}:{message.ExternalUserId}";
        await producer.Produce(partitionKeySuccess, new ActionCompletedEvent(action.Id, Success: true), ct);
    }

    private List<(string Role, string Content)> BuildChatHistoryFromActions(
        List<ActionEntity> actions,
        Guid currentActionId,
        Session session,
        WorkflowGraph graph)
    {
        var list = new List<(string Role, string Content)>();
        foreach (var a in actions)
        {
            if (a.Id == currentActionId)
                continue;

            switch (a.WorkflowNodeType)
            {
                case WorkflowNodeType.Start:
                case WorkflowNodeType.Input:
                    if (!string.IsNullOrWhiteSpace(a.Payload))
                        list.Add(("user", a.Payload));
                    break;
                case WorkflowNodeType.Message:
                    if (graph.Nodes.TryGetValue(a.NodeId, out var msgNode)
                        && msgNode.Data is MessageNodeData msgData
                        && !string.IsNullOrWhiteSpace(msgData.Text))
                    {
                        var renderedText = workflowTextRenderer.RenderText(msgData.Text, session);
                        list.Add(("assistant", renderedText));
                    }

                    break;
                case WorkflowNodeType.Ask:
                    if (graph.Nodes.TryGetValue(a.NodeId, out var askNode) && askNode.Data is AskNodeData askData
                                                                           && !string.IsNullOrWhiteSpace(askData.Text))
                    {
                        var questionText = workflowTextRenderer.RenderText(askData.Text, session);
                        list.Add(("assistant", questionText));
                    }

                    if (!string.IsNullOrWhiteSpace(a.Payload))
                        list.Add(("user", a.Payload));
                    break;
                case WorkflowNodeType.AIGenerate:
                    var output = variableService.GetVariable(session, $"$node.{a.NodeId}.output");
                    if (!string.IsNullOrWhiteSpace(output))
                        list.Add(("assistant", output));
                    break;
            }
        }

        return list;
    }

    private static (string Role, string Content) BuildSystemPrompt(Session session)
    {
        const string baseInstruction =
            "You are a helpful AI assistant in a chatbot. " +
            "The messages above are the conversation history with the client. " +
            "Answer based on this context. " +
            "Always reply in the same language the client uses.";

        var globals = session.Variables
            .Where(kv => kv.Key.StartsWith("$global.", StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => $"{kv.Key["$global.".Length..]}: {kv.Value}")
            .ToList();

        var content = globals.Count > 0
            ? baseInstruction + "\nClient info: " + string.Join(", ", globals)
            : baseInstruction;

        return ("system", content);
    }
}