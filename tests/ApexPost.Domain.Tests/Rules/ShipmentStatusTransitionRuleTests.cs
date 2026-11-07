using ApexPost.Domain.Enums;
using ApexPost.Domain.Rules;

namespace ApexPost.Domain.Tests.Rules;

public class ShipmentStatusTransitionRuleTests
{
	private static readonly (ShipmentStatus From, ShipmentStatus To)[] Allowed =
	[
		(ShipmentStatus.Created, ShipmentStatus.PickedUp),
		(ShipmentStatus.PickedUp, ShipmentStatus.InTransit),
		(ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery),
		(ShipmentStatus.OutForDelivery, ShipmentStatus.Delivered),
		(ShipmentStatus.OutForDelivery, ShipmentStatus.Failed)
	];

	public static TheoryData<ShipmentStatus, ShipmentStatus> AllPairs()
	{
		var pairs = new TheoryData<ShipmentStatus, ShipmentStatus>();
		foreach (var from in Enum.GetValues<ShipmentStatus>())
		{
			foreach (var to in Enum.GetValues<ShipmentStatus>())
				pairs.Add(from, to);
		}

		return pairs;
	}

	// All 36 pairs: exactly the five steps of the lifecycle are allowed.
	[Theory]
	[MemberData(nameof(AllPairs))]
	public void IsAllowed_OnlyTheNextStepOfTheLifecycle(ShipmentStatus from, ShipmentStatus to)
	{
		var rule = new ShipmentStatusTransitionRule();

		Assert.Equal(Allowed.Contains((from, to)), rule.IsAllowed(from, to));
	}

	[Fact]
	public void EnsureAllowed_RefusedChange_ThrowsNamingBothStatuses()
	{
		var rule = new ShipmentStatusTransitionRule();

		var exception = Assert.Throws<InvalidOperationException>(() =>
			rule.EnsureAllowed(ShipmentStatus.Delivered, ShipmentStatus.Failed));

		Assert.Equal("A shipment cannot move from Delivered to Failed.", exception.Message);
	}
}
