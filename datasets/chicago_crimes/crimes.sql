-- Chicago Crimes recipe. Reads the CSV directly via open_csv_typed so
-- the schema lands fully typed (dates, lat/lng floats, ints) instead
-- of the Array<String> shape open_csv yields. The plan-time scan is a
-- single file pass and matches the ingester's authoritative type
-- inference.
SELECT * FROM open_csv_typed($artifact)
