-- SEC EDGAR quarterly filings index (2021 Q1 – 2025 Q4).
--
-- Each master.idx is pipe-delimited with a 9-line preamble (5 description
-- lines + 4 blanks) before the "CIK|Company Name|..." header on line 10,
-- followed by an all-dashes separator on line 11. open_csv_typed gets:
--   skip_lines = 9  – drop the preamble block so the pipe-delimited
--                     header lands as the first line the scanner sees
--                     (auto-detects the pipe delimiter + names the
--                     columns from the file).
--   comment    = '-' – drop the all-dashes separator line that follows
--                     the header, plus any sentinel line a future SEC
--                     format change might add starting with '-'. Without
--                     this the dashes row collapses the CIK column to
--                     String (a non-numeric value kills every integer
--                     candidate during type inference).
-- Each block tags rows with a 'quarter' literal so the unified table is
-- groupable by era; the rest of the columns flow through with the kinds
-- the scanner picked (UInt32 CIK, String company / form / filename,
-- DATE filed).
SELECT '2021Q1' AS quarter, * FROM open_csv_typed($y2021q1, 9, '-')
UNION ALL
SELECT '2021Q2' AS quarter, * FROM open_csv_typed($y2021q2, 9, '-')
UNION ALL
SELECT '2021Q3' AS quarter, * FROM open_csv_typed($y2021q3, 9, '-')
UNION ALL
SELECT '2021Q4' AS quarter, * FROM open_csv_typed($y2021q4, 9, '-')
UNION ALL
SELECT '2022Q1' AS quarter, * FROM open_csv_typed($y2022q1, 9, '-')
UNION ALL
SELECT '2022Q2' AS quarter, * FROM open_csv_typed($y2022q2, 9, '-')
UNION ALL
SELECT '2022Q3' AS quarter, * FROM open_csv_typed($y2022q3, 9, '-')
UNION ALL
SELECT '2022Q4' AS quarter, * FROM open_csv_typed($y2022q4, 9, '-')
UNION ALL
SELECT '2023Q1' AS quarter, * FROM open_csv_typed($y2023q1, 9, '-')
UNION ALL
SELECT '2023Q2' AS quarter, * FROM open_csv_typed($y2023q2, 9, '-')
UNION ALL
SELECT '2023Q3' AS quarter, * FROM open_csv_typed($y2023q3, 9, '-')
UNION ALL
SELECT '2023Q4' AS quarter, * FROM open_csv_typed($y2023q4, 9, '-')
UNION ALL
SELECT '2024Q1' AS quarter, * FROM open_csv_typed($y2024q1, 9, '-')
UNION ALL
SELECT '2024Q2' AS quarter, * FROM open_csv_typed($y2024q2, 9, '-')
UNION ALL
SELECT '2024Q3' AS quarter, * FROM open_csv_typed($y2024q3, 9, '-')
UNION ALL
SELECT '2024Q4' AS quarter, * FROM open_csv_typed($y2024q4, 9, '-')
UNION ALL
SELECT '2025Q1' AS quarter, * FROM open_csv_typed($y2025q1, 9, '-')
UNION ALL
SELECT '2025Q2' AS quarter, * FROM open_csv_typed($y2025q2, 9, '-')
UNION ALL
SELECT '2025Q3' AS quarter, * FROM open_csv_typed($y2025q3, 9, '-')
UNION ALL
SELECT '2025Q4' AS quarter, * FROM open_csv_typed($y2025q4, 9, '-')
