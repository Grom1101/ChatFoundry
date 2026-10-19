namespace Shared.Application.Events;

public record AiTokensConsumedEvent(
    Guid CompanyId,
    int TokensUsed,
    Guid ReferenceId
);
