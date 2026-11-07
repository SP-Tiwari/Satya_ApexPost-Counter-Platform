using ApexPost.Application.Persistence;

namespace ApexPost.Api.Persistence;

// The Week 3 audit interceptor asks who is making a change. Part B has no
// sign-in, so CreatedBy keeps its database default.
internal sealed class NoSignedInStaff : ICurrentStaffAccessor
{
	public string? StaffId => null;
}
