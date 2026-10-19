namespace Shared.Application.Events;

public record ActionCompletedEvent(
    Guid ActionId,
    bool Success = true
);
