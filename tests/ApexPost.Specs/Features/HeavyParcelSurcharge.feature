Feature: Heavy-parcel surcharge
	As an ApexPost pricing manager
	I want heavy parcels priced with a fixed surcharge
	So that shipments over the weight threshold recover their additional handling cost

	Runs the shipping pricing service directly, at service level: no API host and no database.

Background:
	Given the Standard rate to zone "ZONE-A" is 100.00 INR plus 10.00 INR per kg
	And the heavy-parcel surcharge is 50.00 INR

Scenario Outline: Parcels are charged a surcharge only when their weight is strictly above 30 kg
	When I calculate the shipping price for a "Parcel" weighing <weight> kg
	Then the total shipping price is <total> INR

	Examples:
		| weight | total  |
		| 29.99  | 399.90 |
		| 30     | 400.00 |
		| 30.01  | 450.10 |
		| 31     | 460.00 |

Scenario: A letter over 30 kg does not pay the heavy-parcel surcharge
	When I calculate the shipping price for a "Letter" weighing 31 kg
	Then the total shipping price is 410.00 INR

Scenario: A parcel over 30 kg is refused when no surcharge is configured
	Given no heavy-parcel surcharge is configured
	When I calculate the shipping price for a "Parcel" weighing 31 kg
	Then pricing is refused because no heavy-parcel surcharge is configured
