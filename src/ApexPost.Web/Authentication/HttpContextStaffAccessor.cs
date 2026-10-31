using ApexPost.Application.Persistence;

namespace ApexPost.Web.Authentication;

public sealed class HttpContextStaffAccessor(IHttpContextAccessor httpContext)
	: ICurrentStaffAccessor
{
	public string? StaffId =>
		httpContext.HttpContext?.User.FindFirst("StaffId")?.Value;
}
