using BillingService.Services;
using MassTransit;
using Shared.Application.Events;

namespace BillingService.Consumers;

public class AiTokensConsumedBillingConsumer(BillingAccountService account, ILogger<AiTokensConsumedBillingConsumer> logger)
    : IConsumer<AiTokensConsumedEvent>
{
    public async Task Consume(ConsumeContext<AiTokensConsumedEvent> context)
    {
        var msg = context.Message;
        if (msg.TokensUsed <= 0)
            return;

        await account.RecordUsageAsync(
            msg.CompanyId,
            BillingPlanConstants.QuotaAiTokens,
            msg.TokensUsed,
            msg.ReferenceId,
            $"AI tokens consumed in action {msg.ReferenceId}",
            context.CancellationToken);
    }
}
