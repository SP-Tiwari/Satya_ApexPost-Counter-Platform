using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using ApexPost.Domain.Rules;

namespace ApexPost.Domain.Entities;

// A shipment tracked by its APX-######## number through the courier
// lifecycle, with the scans uploaded by courier devices. It is separate from
// the counter's ShippingItem; see ADR 0028.
public sealed partial class TrackedShipment
{
	private static readonly ShipmentStatusTransitionRule TransitionRule = new();
	private readonly List<TrackedShipmentScan> _scans = [];

	public string TrackingNumber { get; }
	public string RecipientName { get; private set; }
	public string RecipientPhone { get; private set; }
	public string RecipientAddress { get; private set; }
	public string RecipientCity { get; private set; }
	public Weight Weight { get; private set; }
	public ShipmentStatus Status { get; private set; }
	public IReadOnlyList<TrackedShipmentScan> Scans => _scans.AsReadOnly();

	// A shipment may be deleted only before a courier has picked it up.
	public bool CanBeDeleted => Status == ShipmentStatus.Created;

	// Used by EF Core when it materializes a stored row.
	private TrackedShipment()
	{
		TrackingNumber = null!;
		RecipientName = null!;
		RecipientPhone = null!;
		RecipientAddress = null!;
		RecipientCity = null!;
		Weight = null!;
	}

	public TrackedShipment(
		string trackingNumber,
		string recipientName,
		string recipientPhone,
		string recipientAddress,
		string recipientCity,
		Weight weight)
	{
		if (trackingNumber is null || !TrackingNumberPattern().IsMatch(trackingNumber))
		{
			throw new ArgumentException(
				"A tracking number must be APX- followed by eight digits.",
				nameof(trackingNumber));
		}

		TrackingNumber = trackingNumber;
		Status = ShipmentStatus.Created;
		ReplaceDetails(recipientName, recipientPhone, recipientAddress, recipientCity, weight);
	}

	// The editable details: everything except the tracking number, the status
	// and the scans.
	[MemberNotNull(
		nameof(RecipientName),
		nameof(RecipientPhone),
		nameof(RecipientAddress),
		nameof(RecipientCity),
		nameof(Weight))]
	public void ReplaceDetails(
		string recipientName,
		string recipientPhone,
		string recipientAddress,
		string recipientCity,
		Weight weight)
	{
		ArgumentNullException.ThrowIfNull(weight);

		RecipientName = Required(recipientName, nameof(recipientName));
		RecipientPhone = Required(recipientPhone, nameof(recipientPhone));
		RecipientAddress = Required(recipientAddress, nameof(recipientAddress));
		RecipientCity = Required(recipientCity, nameof(recipientCity));
		Weight = weight;
	}

	public void ChangeStatus(ShipmentStatus newStatus)
	{
		if (!Enum.IsDefined(newStatus))
			throw new ArgumentOutOfRangeException(nameof(newStatus));

		TransitionRule.EnsureAllowed(Status, newStatus);
		Status = newStatus;
	}

	// Scans are history: adding one never changes the status. Scan IDs are
	// compared ignoring case, as the database column does.
	public void AddScan(TrackedShipmentScan scan)
	{
		ArgumentNullException.ThrowIfNull(scan);

		if (_scans.Any(existing => string.Equals(
			existing.ScanId, scan.ScanId, StringComparison.OrdinalIgnoreCase)))
		{
			throw new InvalidOperationException(
				$"Scan {scan.ScanId} is already recorded for this shipment.");
		}

		_scans.Add(scan);
	}

	private static string Required(string value, string parameterName)
	{
		if (string.IsNullOrWhiteSpace(value))
			throw new ArgumentException($"{parameterName} cannot be empty.", parameterName);

		return value.Trim();
	}

	// [0-9], not \d: \d also matches non-ASCII digits, which the varchar
	// column cannot hold.
	[GeneratedRegex("^APX-[0-9]{8}$")]
	private static partial Regex TrackingNumberPattern();
}
