namespace ApexPost.Domain.Entities;

// An immutable history row for one change to a branch's stock.
public sealed class StockAdjustment
{
	public Guid Id { get; }
	public BranchCode BranchCode { get; }
	public Sku ProductSku { get; }
	public int Delta { get; }
	public string Reason { get; }
	public string ChangedByStaffId { get; }
	public DateTimeOffset OccurredAtUtc { get; }

	public StockAdjustment(
		Guid id,
		BranchCode branchCode,
		Sku productSku,
		int delta,
		string reason,
		string changedByStaffId,
		DateTimeOffset occurredAtUtc)
	{
		ArgumentNullException.ThrowIfNull(branchCode);
		ArgumentNullException.ThrowIfNull(productSku);

		if (id == Guid.Empty)
			throw new ArgumentException(
				"Stock adjustment ID cannot be empty.", nameof(id));

		if (delta == 0)
			throw new ArgumentOutOfRangeException(
				nameof(delta), "A stock adjustment must change the quantity.");

		if (string.IsNullOrWhiteSpace(reason))
			throw new ArgumentException(
				"A reason is required to adjust stock.", nameof(reason));

		if (string.IsNullOrWhiteSpace(changedByStaffId))
			throw new ArgumentException(
				"The staff member making the adjustment is required.",
				nameof(changedByStaffId));

		Id = id;
		BranchCode = branchCode;
		ProductSku = productSku;
		Delta = delta;
		Reason = reason.Trim();
		ChangedByStaffId = changedByStaffId.Trim();
		OccurredAtUtc = occurredAtUtc.ToUniversalTime();
	}
}
