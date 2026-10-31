namespace ApexPost.Application.Persistence;

// Who is making the current change, for the CreatedBy audit column. Null when
// no staff member is signed in, such as during seeding.
public interface ICurrentStaffAccessor
{
	string? StaffId { get; }
}
