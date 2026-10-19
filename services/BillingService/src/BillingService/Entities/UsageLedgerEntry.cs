using BillingService.Enums;
using Shared.Domain.Entities;

namespace BillingService.Entities;

public class UsageLedgerEntry : EntityBase
{
    public Guid CompanyId { get; set; }
    public string QuotaType { get; set; } = null!;
    public LedgerEntryType Type { get; set; } = LedgerEntryType.Debit;
    public long Amount { get; set; }
    public Guid? ReferenceId { get; set; }
    public string? Description { get; set; }
}
