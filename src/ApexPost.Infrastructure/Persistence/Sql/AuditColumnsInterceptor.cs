using ApexPost.Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ApexPost.Infrastructure.Persistence.Sql;

// Stamps the shadow audit columns on every save. CreatedAtUtc keeps its
// database default. CreatedBy keeps its default when no staff member is known.
public sealed class AuditColumnsInterceptor(
	TimeProvider timeProvider,
	ICurrentStaffAccessor currentStaff) : SaveChangesInterceptor
{
	private const string CreatedBy = "CreatedBy";
	private const string ModifiedAtUtc = "ModifiedAtUtc";

	public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
		DbContextEventData eventData,
		InterceptionResult<int> result,
		CancellationToken cancellationToken = default)
	{
		Stamp(eventData.Context);
		return base.SavingChangesAsync(eventData, result, cancellationToken);
	}

	public override InterceptionResult<int> SavingChanges(
		DbContextEventData eventData,
		InterceptionResult<int> result)
	{
		Stamp(eventData.Context);
		return base.SavingChanges(eventData, result);
	}

	private void Stamp(DbContext? context)
	{
		if (context is null)
			return;

		var modifiedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
		foreach (var entry in context.ChangeTracker.Entries())
		{
			if (entry.Metadata.FindProperty(ModifiedAtUtc) is null)
				continue;

			if (entry.State == EntityState.Added)
				StampCreatedBy(entry);
			else if (entry.State == EntityState.Modified)
				entry.Property(ModifiedAtUtc).CurrentValue = modifiedAtUtc;
		}
	}

	private void StampCreatedBy(EntityEntry entry)
	{
		if (!string.IsNullOrWhiteSpace(currentStaff.StaffId))
			entry.Property(CreatedBy).CurrentValue = currentStaff.StaffId;
	}
}
