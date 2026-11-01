

namespace ApexPost.Domain.Entities;

public sealed class Branch
{
	public BranchCode Code { get; }
	public string Name { get; }
	public string Region { get; }
	public string OpeningHours { get; }
	public bool IsActive { get; private set; }

	private readonly List<CounterTerminal> _counterTerminals = [];

	public IReadOnlyList<CounterTerminal> CounterTerminals =>
		_counterTerminals.AsReadOnly();

	// Used by EF Core when it materializes a stored row.
	private Branch()
	{
		Code = null!;
		Name = null!;
		Region = null!;
		OpeningHours = null!;
	}

	public Branch(
		BranchCode code,
		string name,
		string region,
		string openingHours,
		IEnumerable<CounterTerminal> counterTerminals)
	{
		ArgumentNullException.ThrowIfNull(code);
		ArgumentNullException.ThrowIfNull(counterTerminals);

		if (string.IsNullOrWhiteSpace(name))
			throw new ArgumentException("Branch name cannot be empty.", nameof(name));

		if (string.IsNullOrWhiteSpace(region))
			throw new ArgumentException("Branch region cannot be empty.", nameof(region));

		if (string.IsNullOrWhiteSpace(openingHours))
			throw new ArgumentException("Opening hours cannot be empty.", nameof(openingHours));

		var terminals = counterTerminals.ToList();

		if (terminals.Count == 0 || terminals.Any(terminal => terminal is null))
			throw new ArgumentException(
				"A branch must have at least one valid counter terminal.",
				nameof(counterTerminals));

		if (terminals.Select(t => t.Id)
				.Distinct(StringComparer.OrdinalIgnoreCase).Count() != terminals.Count)
		{
			throw new ArgumentException(
				"Counter terminal IDs must be unique.",
				nameof(counterTerminals));
		}

		Code = code;
		Name = name.Trim();
		Region = region.Trim();
		OpeningHours = openingHours.Trim();
		IsActive = true;

		_counterTerminals.AddRange(terminals);
	}

	public void Close() => IsActive = false;

	public void Reopen() => IsActive = true;
}
