-- Jigsaw Toxic Comments — train recipe. The upstream `train.csv.gz` is an
-- eight-column CSV with header (id, comment_text, toxic, severe_toxic,
-- obscene, threat, insult, identity_hate). open_csv_typed reads the header
-- row and types the id + comment_text as String and the six label columns
-- as Int32 0/1 flags, so the table lands with the exact shape downstream
-- multi-label classifiers want — no rename / no CAST required.
SELECT * FROM open_csv_typed($artifact)
