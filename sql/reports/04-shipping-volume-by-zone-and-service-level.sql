-- Shipment volume by destination zone and service level. Matches
-- ShippingVolumeReportService, which counts every stored shipment. A
-- combination with no shipments returns no row.
SELECT
    z.ZoneCode,
    z.Name AS ZoneName,
    s.ServiceLevel,
    COUNT(*) AS ShipmentCount,
    SUM(s.WeightKg) AS TotalWeightKg
FROM dbo.Shipments AS s
INNER JOIN dbo.ShippingZones AS z
    ON z.ZoneCode = s.ZoneCode
GROUP BY z.ZoneCode, z.Name, s.ServiceLevel
ORDER BY z.ZoneCode, s.ServiceLevel;
