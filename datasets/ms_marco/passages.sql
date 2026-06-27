-- MS MARCO passages collection. The upstream `collection.tsv` is a two-column
-- tab-delimited file with no header row: `passage_id <TAB> text`. Pass
-- header => FALSE so open_csv_typed skips the autodetector and synthesises
-- `col_0 Int32, col_1 String`; the recipe just renames those columns into
-- the names downstream queries actually want.
SELECT
    col_0 AS passage_id,
    col_1 AS text
FROM open_csv_typed($artifact, header := FALSE)
