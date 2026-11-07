namespace ApexPost.Application.Services;

public sealed record ShipmentDetails(
	string RecipientName,
	string RecipientPhone,
	string RecipientAddress,
	string RecipientCity,
	decimal WeightKg);

public sealed record CreateTrackedShipmentCommand(
	string TrackingNumber,
	ShipmentDetails Details);

// One scan as a courier device uploaded it (Week 4 Part A).
public sealed record UploadedScan(
	string ScanId,
	string TrackingNumber,
	ShipmentScanType ScanType,
	string CourierId,
	string Location,
	DateTimeOffset ScannedAtUtc,
	string? Notes);

public enum ShipmentChangeOutcome
{
	Succeeded,
	NotFound,
	Conflict
}

// The outcome of a change. Shipment is set when it succeeded; Problem
// explains a conflict.
public sealed record ShipmentChangeResult(
	ShipmentChangeOutcome Outcome,
	TrackedShipment? Shipment = null,
	string? Problem = null)
{
	public static ShipmentChangeResult Succeeded(TrackedShipment shipment) =>
		new(ShipmentChangeOutcome.Succeeded, shipment);

	public static ShipmentChangeResult NotFound() => new(ShipmentChangeOutcome.NotFound);

	public static ShipmentChangeResult Conflict(string problem) =>
		new(ShipmentChangeOutcome.Conflict, Problem: problem);
}

public sealed record RejectedScan(string ScanId, string Reason);

public sealed record ScanUploadResult(
	int Accepted,
	int DuplicatesSkipped,
	IReadOnlyList<RejectedScan> Rejected);

// The Week 4 API's shipment use cases. Each change is one unit of work; see
// ADR 0028. Scans are history and never change a shipment's status: only
// ChangeStatusAsync does.
public sealed class TrackedShipmentApplicationService(
	ITrackedShipmentRepository shipments,
	IUnitOfWork unitOfWork)
{
	private static readonly ShipmentStatusTransitionRule TransitionRule = new();

	public Task<TrackedShipmentPage> GetPageAsync(
		int page,
		int pageSize,
		ShipmentStatus? status,
		string? city,
		CancellationToken cancellationToken = default) =>
		shipments.GetPageAsync(page, pageSize, status, city, cancellationToken);

	// Null when no shipment has the tracking number.
	public Task<TrackedShipment?> FindAsync(
		string trackingNumber,
		CancellationToken cancellationToken = default) =>
		shipments.FindForReadAsync(trackingNumber, cancellationToken);

	// Oldest first. Null when no shipment has the tracking number.
	public async Task<IReadOnlyList<TrackedShipmentScan>?> GetScansAsync(
		string trackingNumber,
		CancellationToken cancellationToken = default)
	{
		var shipment = await shipments.FindForReadAsync(trackingNumber, cancellationToken)
			.ConfigureAwait(false);
		return shipment?.Scans
			.OrderBy(scan => scan.ScannedAtUtc)
			.ThenBy(scan => scan.ScanId, StringComparer.Ordinal)
			.ToArray();
	}

	// A duplicate tracking number is a primary-key conflict, so two creates at
	// the same time cannot both succeed.
	public async Task<ShipmentChangeResult> CreateAsync(
		CreateTrackedShipmentCommand command,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(command);

		var details = command.Details;
		var shipment = new TrackedShipment(
			command.TrackingNumber,
			details.RecipientName,
			details.RecipientPhone,
			details.RecipientAddress,
			details.RecipientCity,
			new Weight(details.WeightKg));
		try
		{
			await unitOfWork.ExecuteInTransactionAsync(async token =>
			{
				shipments.Add(shipment);
				await unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
			}, cancellationToken).ConfigureAwait(false);
		}
		catch (DuplicateRecordException)
		{
			return ShipmentChangeResult.Conflict(
				$"Shipment {command.TrackingNumber} already exists.");
		}

		return ShipmentChangeResult.Succeeded(shipment);
	}

	public Task<ShipmentChangeResult> ReplaceAsync(
		string trackingNumber,
		ShipmentDetails details,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(details);

		return ChangeAsync(trackingNumber, shipment =>
		{
			shipment.ReplaceDetails(
				details.RecipientName,
				details.RecipientPhone,
				details.RecipientAddress,
				details.RecipientCity,
				new Weight(details.WeightKg));
			return null;
		}, cancellationToken);
	}

	public Task<ShipmentChangeResult> ChangeStatusAsync(
		string trackingNumber,
		ShipmentStatus newStatus,
		CancellationToken cancellationToken = default) =>
		ChangeAsync(trackingNumber, shipment =>
		{
			if (!TransitionRule.IsAllowed(shipment.Status, newStatus))
				return $"A shipment cannot move from {shipment.Status} to {newStatus}.";

			shipment.ChangeStatus(newStatus);
			return null;
		}, cancellationToken);

	public Task<ShipmentChangeResult> DeleteAsync(
		string trackingNumber,
		CancellationToken cancellationToken = default) =>
		ChangeAsync(trackingNumber, shipment =>
		{
			if (!shipment.CanBeDeleted)
			{
				return $"Shipment {trackingNumber} is {shipment.Status}; " +
					$"only a {ShipmentStatus.Created} shipment can be deleted.";
			}

			shipments.Remove(shipment);
			return null;
		}, cancellationToken);

	// Idempotent by scan ID. A scan already recorded for this shipment, or
	// repeated in the request, is skipped. Null when no shipment has the
	// tracking number.
	public async Task<ScanUploadResult?> UploadScansAsync(
		string trackingNumber,
		IReadOnlyList<UploadedScan> scans,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(scans);

		try
		{
			return await UploadOnceAsync(trackingNumber, scans, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (DuplicateRecordException)
		{
			// Another upload stored one of these scans after this one read the
			// stored IDs. Running again reads them afresh, so that scan is
			// counted as a duplicate or as another shipment's.
			return await UploadOnceAsync(trackingNumber, scans, cancellationToken)
				.ConfigureAwait(false);
		}
	}

	private Task<ScanUploadResult?> UploadOnceAsync(
		string trackingNumber,
		IReadOnlyList<UploadedScan> scans,
		CancellationToken cancellationToken) =>
		unitOfWork.ExecuteInTransactionAsync<ScanUploadResult?>(async token =>
		{
			var shipment = await shipments.FindAsync(trackingNumber, token)
				.ConfigureAwait(false);
			if (shipment is null)
				return null;

			// Scan IDs are compared ignoring case, as the key column does.
			var recorded = new HashSet<string>(
				shipment.Scans.Select(scan => scan.ScanId),
				StringComparer.OrdinalIgnoreCase);
			var storedElsewhere = new HashSet<string>(
				await shipments.FindStoredScanIdsAsync(
					[.. scans.Select(scan => scan.ScanId)], token).ConfigureAwait(false),
				StringComparer.OrdinalIgnoreCase);
			storedElsewhere.ExceptWith(recorded);

			var accepted = 0;
			var duplicatesSkipped = 0;
			var rejected = new List<RejectedScan>();
			foreach (var uploaded in scans)
			{
				var rejection = RejectionReason(uploaded, trackingNumber, recorded, storedElsewhere);
				if (rejection is not null)
				{
					rejected.Add(new RejectedScan(uploaded.ScanId, rejection));
					continue;
				}

				if (!recorded.Add(uploaded.ScanId))
				{
					duplicatesSkipped++;
					continue;
				}

				shipment.AddScan(new TrackedShipmentScan(
					uploaded.ScanId,
					uploaded.ScanType,
					uploaded.CourierId,
					uploaded.Location,
					uploaded.ScannedAtUtc,
					uploaded.Notes));
				accepted++;
			}

			if (accepted > 0)
				await unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);

			return new ScanUploadResult(accepted, duplicatesSkipped, rejected);
		}, cancellationToken);

	private static string? RejectionReason(
		UploadedScan scan,
		string trackingNumber,
		IReadOnlySet<string> recorded,
		IReadOnlySet<string> storedElsewhere)
	{
		if (!TrackedShipmentScan.IsValidScanId(scan.ScanId))
			return "The scan ID must have the format scan::{Guid}.";

		if (!string.Equals(scan.TrackingNumber, trackingNumber, StringComparison.Ordinal))
			return $"The scan is for {scan.TrackingNumber}, not {trackingNumber}.";

		if (!recorded.Contains(scan.ScanId) && storedElsewhere.Contains(scan.ScanId))
			return "The scan ID is already recorded for another shipment.";

		return null;
	}

	// Loads the shipment, applies the change and saves it, all in one unit of
	// work. The change returns a conflict message, or null when it was made.
	private async Task<ShipmentChangeResult> ChangeAsync(
		string trackingNumber,
		Func<TrackedShipment, string?> change,
		CancellationToken cancellationToken) =>
		await unitOfWork.ExecuteInTransactionAsync(async token =>
		{
			var shipment = await shipments.FindAsync(trackingNumber, token)
				.ConfigureAwait(false);
			if (shipment is null)
				return ShipmentChangeResult.NotFound();

			if (change(shipment) is { } conflict)
				return ShipmentChangeResult.Conflict(conflict);

			await unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
			return ShipmentChangeResult.Succeeded(shipment);
		}, cancellationToken).ConfigureAwait(false);
}
