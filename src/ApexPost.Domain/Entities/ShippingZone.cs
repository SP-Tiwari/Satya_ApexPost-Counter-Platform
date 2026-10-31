namespace ApexPost.Domain.Entities;

// A destination that shipping rates and shipments refer to by its code.
public sealed class ShippingZone
{
	public string Code { get; }
	public string Name { get; }

	public ShippingZone(string code, string name)
	{
		if (string.IsNullOrWhiteSpace(code))
			throw new ArgumentException("Zone code cannot be empty.", nameof(code));

		if (string.IsNullOrWhiteSpace(name))
			throw new ArgumentException("Zone name cannot be empty.", nameof(name));

		Code = code.Trim();
		Name = name.Trim();
	}
}
