@courier-offline
Feature: Scan sync
	As a courier who scans parcels without a network connection
	I want my saved scans uploaded when the device syncs
	So that no scan is lost and none is sent twice

	Runs the courier device's outbox against a real Couchbase Lite store in a
	temporary folder. The central system's answers are given by each scenario.

Background:
	Given the courier device has an empty local scan store
	And the courier scanned these parcels while offline:
		| scan     | tracking number | scan type |
		| pickup-1 | APX-20000001    | Pickup    |
		| pickup-2 | APX-20000002    | Pickup    |

Scenario: Offline scans are uploaded when the device syncs
	When the device syncs with the central system
	Then the central system receives scans "pickup-1, pickup-2" in that order
	And scan "pickup-1" is marked "Synced"
	And scan "pickup-2" is marked "Synced"
	And no scan is waiting to be sent

Scenario: A scan the central system already holds is accepted and not sent again
	Given the central system already holds scan "pickup-1"
	When the device syncs with the central system
	Then the central system receives scans "pickup-1, pickup-2" in that order
	And scan "pickup-1" is marked "Synced"
	And scan "pickup-2" is marked "Synced"
	And the next sync sends nothing

Scenario: An invalid scan is rejected and not sent again
	Given the central system refuses scan "pickup-2" because "The scan ID is already recorded for another shipment."
	When the device syncs with the central system
	Then scan "pickup-1" is marked "Synced"
	And scan "pickup-2" is marked "Failed"
	And the sync reports scan "pickup-2" failed because "The scan ID is already recorded for another shipment."
	And the next sync sends nothing
