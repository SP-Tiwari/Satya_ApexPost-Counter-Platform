@shipment-api
Feature: Shipment creation
	As an ApexPost administrator
	I want each parcel registered once, with a weight the network can carry
	So that every tracking number identifies exactly one shipment

	Runs against the Shipment API in memory, on a disposable SQL Server database.

Background:
	Given I am signed in to the Shipment API with permission to create shipments

Scenario: A valid shipment is created
	When I create shipment "APX-10000001" weighing 2.5 kg
	Then the shipment is created
	And shipment "APX-10000001" can be found with status "Created" and weight 2.5 kg

Scenario: A tracking number cannot be used twice
	Given shipment "APX-10000002" already exists, weighing 2.5 kg
	When I create shipment "APX-10000002" weighing 4 kg
	Then the shipment is refused because it already exists
	And shipment "APX-10000002" can be found with status "Created" and weight 2.5 kg

Scenario Outline: Only weights from 0.1 kg to 70 kg are accepted
	When I create shipment "<tracking number>" weighing <weight> kg
	Then the shipment is <outcome>

	Examples:
		| tracking number | weight | outcome                    |
		| APX-10000003    | 0.05   | refused for its weight     |
		| APX-10000004    | 0.1    | created                    |
		| APX-10000005    | 70     | created                    |
		| APX-10000006    | 70.1   | refused for its weight     |
