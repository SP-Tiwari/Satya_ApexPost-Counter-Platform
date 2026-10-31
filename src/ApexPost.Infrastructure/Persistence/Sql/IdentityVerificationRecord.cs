using ApexPost.Domain.ValueObjects;

namespace ApexPost.Infrastructure.Persistence.Sql;

// The persistence model for a money order's identity check, kept apart from
// the IdentityVerification value object. See ADR 0019.
public sealed class IdentityVerificationRecord
{
	public const int MaskedIdentifierLength = 8;

	public Guid MoneyOrderId { get; private set; }
	public string DocumentType { get; private set; } = null!;
	public string MaskedDocumentIdentifier { get; private set; } = null!;
	public bool IsSuccessful { get; private set; }

	private IdentityVerificationRecord()
	{
	}

	internal IdentityVerificationRecord(
		Guid moneyOrderId,
		string documentType,
		string maskedDocumentIdentifier,
		bool isSuccessful)
	{
		MoneyOrderId = moneyOrderId;
		DocumentType = documentType;
		MaskedDocumentIdentifier = maskedDocumentIdentifier;
		IsSuccessful = isSuccessful;
	}
}

public static class IdentityVerificationMapper
{
	private const string StoredMask = "****";

	// Only the last four characters are stored, behind a fixed mask, so the
	// stored value does not reveal the length of the document number either.
	public static IdentityVerificationRecord ToRecord(
		Guid moneyOrderId,
		IdentityVerification verification)
	{
		ArgumentNullException.ThrowIfNull(verification);

		return new IdentityVerificationRecord(
			moneyOrderId,
			verification.DocumentType,
			StoredMask + verification.MaskedDocumentIdentifier[^4..],
			verification.IsSuccessful);
	}

	public static IdentityVerification ToDomain(IdentityVerificationRecord record)
	{
		ArgumentNullException.ThrowIfNull(record);

		return IdentityVerification.FromMaskedIdentifier(
			record.DocumentType,
			record.MaskedDocumentIdentifier,
			record.IsSuccessful);
	}
}
