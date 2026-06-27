-- Quora Question Pairs recipe. The canonical release is a tab-delimited
-- file (despite the upstream .tsv name landing as one row per pair) with
-- six columns: id, qid1, qid2, question1, question2, is_duplicate.
-- open_csv_typed auto-detects the tab delimiter from the header line and
-- assigns Int32 to the three numeric columns + String to the question
-- bodies, so embedders can read `question1` / `question2` directly and
-- `is_duplicate` flows through as a 0/1 integer suitable for filtering
-- without a CAST.
SELECT * FROM open_csv_typed($artifact)
