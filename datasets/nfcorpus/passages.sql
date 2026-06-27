-- NFCorpus passages collection. The mirror's `corpus.jsonl` is the BEIR
-- re-encoding verbatim — one JSON object per line with keys `_id`, `title`,
-- `text`, and `metadata`. open_jsonl scans the file at plan time, infers the
-- per-column schema (all String for NFCorpus — ids like "MED-10" are not
-- numeric), and streams one row per line at execute time. The recipe
-- renames `_id` to `passage_id` and concatenates title + body into a single
-- `text` column (the convention used by BEIR / MTEB embedder evaluations);
-- `metadata` is dropped — it's just the source NutritionFacts.org URL and
-- nothing downstream embeds it.
SELECT
    "_id"                   AS passage_id,
    title || '. ' || "text" AS text
FROM open_jsonl($artifact)
