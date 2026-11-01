
namespace ApexPost.Domain.Rules;

public sealed class TransactionAccessRule
{
	public bool CanAccess(Staff staff, TransactionBasket basket)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(basket);

		return staff.IsActive &&
			staff.Role == StaffRole.BranchManager &&
			basket.BranchCode is not null &&
			basket.BranchCode == staff.HomeBranchCode;
	}

	// Read-only access. A Branch Manager can view every transaction at their
	// branch (CanAccess). A Counter Clerk can view only the counter sales they
	// processed at their branch, so the receipt and shipping label are
	// available after the sale. Voids and refunds still require CanAccess.
	public bool CanView(Staff staff, TransactionBasket basket) =>
		CanAccess(staff, basket) ||
		CanView(staff, basket.BranchCode, basket.SaleDetails?.ClerkStaffId);

	// The same decision from a transaction's stored facts, for read screens
	// that project them rather than load the basket.
	public bool CanView(Staff staff, BranchCode? branchCode, string? clerkStaffId)
	{
		ArgumentNullException.ThrowIfNull(staff);

		return staff.IsActive &&
			branchCode is not null &&
			branchCode == staff.HomeBranchCode &&
			(staff.Role == StaffRole.BranchManager ||
				staff.Role == StaffRole.CounterClerk &&
				string.Equals(clerkStaffId, staff.Id, StringComparison.Ordinal));
	}

	public void EnsureCanAccess(Staff staff, TransactionBasket basket)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(basket);

		if (!staff.IsActive)
			throw new InvalidOperationException(
				"Inactive staff cannot access transactions.");

		if (staff.Role != StaffRole.BranchManager)
			throw new InvalidOperationException(
				"An active Branch Manager is required to access transactions.");

		if (basket.BranchCode is null ||
			basket.BranchCode != staff.HomeBranchCode)
		{
			throw new InvalidOperationException(
				"Transactions are accessible only from the assigned branch.");
		}
	}
}
