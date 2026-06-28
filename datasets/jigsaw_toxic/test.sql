-- Jigsaw Toxic Comments — test recipe. Joins the held-out comment text
-- ($comments => test.csv.gz, two cols: id + comment_text) with the
-- post-competition gold labels ($labels => test_labels.csv.gz, seven cols:
-- id + the six toxicity flags) on the shared id. The WHERE clause drops
-- the rows Kaggle excluded from private-leaderboard scoring: those rows
-- carry -1 in every label column instead of 0/1, so `l.toxic >= 0` keeps
-- only the ~63,978 scored rows out of the ~153,164 raw test rows.
SELECT
    t.id AS id,
    t.comment_text AS comment_text,
    (CASE WHEN l.toxic > 0 THEN true ELSE false END) AS toxic,
    (CASE WHEN l.severe_toxic > 0 THEN true ELSE false END) AS severe_toxic,
    (CASE WHEN l.obscene > 0 THEN true ELSE false END) AS obscene,
    (CASE WHEN l.threat > 0 THEN true ELSE false END) AS threat,
    (CASE WHEN l.insult > 0 THEN true ELSE false END) AS insult,
    (CASE WHEN l.identity_hate > 0 THEN true ELSE false END) AS identity_hate
FROM open_csv_typed($comments) AS t
JOIN open_csv_typed($labels) AS l ON t.id = l.id
WHERE l.toxic >= 0
