-- TweetEval sentiment subtask. The upstream HF parquet ships two columns:
-- `text` (the tweet body, String) and `label` (Int64, 0=negative /
-- 1=neutral / 2=positive). open_parquet peeks the schema at plan time so
-- both columns land with their real types and downstream queries can
-- filter on `label` without a CAST. The recipe is shared across the
-- three split variants — each variant feeds a different parquet artifact
-- but the schema and transform are identical.
SELECT
  text,
  CASE
    WHEN label = 0 THEN 'negative'
    WHEN label = 1 THEN 'neutral'
    WHEN label = 2 THEN 'positive'
  END AS sentiment
FROM open_parquet($artifact)
