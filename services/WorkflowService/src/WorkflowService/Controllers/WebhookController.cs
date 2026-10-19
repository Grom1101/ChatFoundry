using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MassTransit;
using Shared.Application.Events;
using Shared.Domain.Enums;
using WorkflowService.Interfaces;

namespace WorkflowService.Controllers;

[ApiController]
[Route("api/webhook")]
public class WebhookController(
    IActionRepository actionRepository,
    ISessionRepository sessionRepository,
    IVariableService variableService,
    ITopicProducer<string, ActionCompletedEvent> producer) : ControllerBase
{
    [HttpPost("{botId:guid}/{clientId}")]
    [Authorize]
    public async Task<IActionResult> HandleWebhook(
        Guid botId,
        string clientId,
        [FromQuery] string channel,
        CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);

        var channelStr = channel?.Trim() ?? string.Empty;

        if (!Enum.TryParse<DefaultChannel>(channelStr, true, out var parsedChannel))
        {
            return BadRequest(new { error = $"Invalid channel: {channel}" });
        }

        var action = await actionRepository.GetProcessingWebhookActionAsync(botId, clientId, parsedChannel, ct);
        if (action == null || action.Session == null)
        {
            return NotFound(new { error = "No active session waiting for webhook found for the specified bot, client, and channel." });
        }

        var session = action.Session;
        var nodeId = action.NodeId;

        variableService.SetVariable(session, $"$node.{nodeId}.output", body);
        variableService.SetVariable(session, "$webhook.payload", body);
        await variableService.SyncIfDirtyAsync(session, ct);
        await sessionRepository.SaveAsync(session, ct);

        var partitionKey = $"{session.ChannelId}:{session.ClientId}";
        await producer.Produce(partitionKey, new ActionCompletedEvent(action.Id, Success: true), ct);

        return Ok(new { status = "accepted", nodeId });
    }
}
