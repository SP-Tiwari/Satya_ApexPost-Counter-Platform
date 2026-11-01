using ApexPost.Application.Auditing;
using ApexPost.Application.Caching;
using ApexPost.Application.Persistence;
using ApexPost.Application.Security;
using ApexPost.Domain.Entities;
using ApexPost.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ApexPost.Infrastructure.Persistence.Sql;

// Audit entries commit in a scope of their own, separate from the request's
// unit of work, so a rolled-back sale keeps the record of the attempt.
public sealed class SqlAuditStore(IServiceScopeFactory scopes) : IAuditStore
{
	public async Task AppendAsync(
		AuditRecord record,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(record);

		await using var scope = scopes.CreateAsyncScope();
		var database = scope.ServiceProvider.GetRequiredService<ApexPostDbContext>();
		database.AuditEntries.Add(record);
		await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
	}
}

public sealed class SqlStaffCredentialRepository(ApexPostDbContext database)
	: IStaffCredentialRepository, IStaffCredentialProvisioning
{
	// The column collation ignores case and the domain does not, so the ID is
	// compared again after the query.
	public async Task<StaffCredential?> FindAsync(
		string staffId,
		CancellationToken cancellationToken = default)
	{
		var credential = await database.StaffCredentials
			.AsNoTracking()
			.SingleOrDefaultAsync(
				candidate => candidate.StaffId == staffId, cancellationToken)
			.ConfigureAwait(false);
		return string.Equals(credential?.StaffId, staffId, StringComparison.Ordinal)
			? credential
			: null;
	}

	public async Task<IReadOnlySet<string>> GetStaffIdsAsync(
		CancellationToken cancellationToken = default) =>
		(await database.StaffCredentials
			.AsNoTracking()
			.Select(credential => credential.StaffId)
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false))
		.ToHashSet(StringComparer.Ordinal);

	public async Task<int> AddMissingAsync(
		IReadOnlyCollection<StaffCredential> credentials,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(credentials);

		var existing = await GetStaffIdsAsync(cancellationToken).ConfigureAwait(false);
		var missing = credentials
			.Where(credential => !existing.Contains(credential.StaffId))
			.ToArray();
		database.StaffCredentials.AddRange(missing);
		await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		return missing.Length;
	}
}

// The cache is a singleton, so each load opens a short-lived scope.
public sealed class SqlReferenceDataSource(IServiceScopeFactory scopes)
	: IReferenceDataSource
{
	public async Task<(IReadOnlyList<Product> Products, IReadOnlyList<ShippingRate> ShippingRates)>
		LoadAsync(CancellationToken cancellationToken = default)
	{
		await using var scope = scopes.CreateAsyncScope();
		var products = scope.ServiceProvider.GetRequiredService<IProductRepository>();
		var productList = await products.GetProductsAsync(cancellationToken)
			.ConfigureAwait(false);
		var shippingRates = await products.GetShippingRatesAsync(cancellationToken)
			.ConfigureAwait(false);
		return (productList, shippingRates);
	}
}
