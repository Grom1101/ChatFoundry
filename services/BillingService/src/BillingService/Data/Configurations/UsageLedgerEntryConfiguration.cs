using BillingService.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Infrastructure.EntityTypeConfiguration;

namespace BillingService.Data.Configurations;

public class UsageLedgerEntryConfiguration : BaseEntityTypeConfiguration<UsageLedgerEntry>
{
    public override void Configure(EntityTypeBuilder<UsageLedgerEntry> builder)
    {
        base.Configure(builder);
        builder.ToTable("usage_ledger_entries");

        builder.Property(x => x.QuotaType).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(512);

        builder.HasIndex(x => new { x.CompanyId, x.ReferenceId, x.QuotaType })
            .IsUnique()
            .HasFilter(@"""ReferenceId"" IS NOT NULL");

        builder.HasIndex(x => new { x.CompanyId, x.QuotaType, x.CreatedAt });
    }
}
