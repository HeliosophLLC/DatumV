# Twitter RoBERTa Sentiment

A RoBERTa-base classifier from Cardiff NLP, fine-tuned on the TweetEval
sentiment subtask (itself the SemEval-2017 Task 4 tweet-sentiment
corpus). Picks one of three labels — `negative`, `neutral`, `positive` —
for any short English text. Trained on tweets, but generalizes
reasonably well to other short-form English: product reviews, news
comments, support tickets. ~500 MB on disk, CPU-viable for one-off
classification, GPU recommended for bulk passes over millions of rows.

One SQL-visible model: `twitter_roberta_sentiment`. Takes a `String` and
returns a `ScoredLabel` — a struct of `{label: String, score: Float32}`
where `label` is the picked class and `score` is its softmax probability
(0-1). Internally: BPE-tokenize, run the RoBERTa encoder, softmax over
three logits, argmax.

## Example SQL

Classify a single sentence:

```sql
SELECT models.twitter_roberta_sentiment('honestly the new release just made my day') AS result;
-- result = {label: 'positive', score: 0.97}
```

Score the TweetEval sentiment test split and measure accuracy against
the gold labels:

```sql
WITH predictions AS (
    SELECT
        text,
        sentiment AS gold,
        models.twitter_roberta_sentiment(text) AS predicted
    FROM datasets.tweeteval_sentiment_test
    LIMIT 100 -- Remove for all tweets
)
SELECT
    gold,
    predicted,
    count(*) AS n
FROM predictions
GROUP BY gold, predicted
ORDER BY gold, predicted;
```

That's the full 3×3 confusion matrix over 12,284 held-out tweets.

The leaderboard target on this split is ~73% accuracy / ~72 macro-F1 —
anything materially below means the pipeline (encoding, normalization,
or label mapping) is off.

## Output shape

`ScoredLabel` — `{label: String, score: Float32}`. The label is one of
exactly three strings: `'negative'`, `'neutral'`, `'positive'`. The
score is the softmax probability of the picked class (the other two
classes' probabilities are discarded). Per-class probabilities can be
recovered by calling the underlying softmax pipeline directly if
needed for calibration.

## Tips

- **Short text is the design point.** Tweets are ≤280 characters; the
  model is sized and trained around that. Sentences and short paragraphs
  work fine; multi-paragraph documents need to be split first or the
  signal averages out.
- **Neutral is a real class, not a fallback.** A common mistake is to
  treat low-confidence positives/negatives as neutral — the model
  already emits `neutral` for genuinely neutral text. Trust its
  three-way decision.
- **Pre-redaction matters for tweet-domain data.** Cardiff NLP's
  training data replaces URLs with `http` and user mentions with
  `@user`. Doing the same on your input (when it's tweet-like)
  squeezes a percentage point or two of accuracy back; the TweetEval
  test split already ships pre-redacted, which is why direct evaluation
  works without any preprocessing.
- **English only.** For multilingual sentiment reach for
  `cardiffnlp/twitter-xlm-roberta-base-sentiment` upstream.
- **Context is 128 tokens** (BPE). Tweets always fit; long reviews get
  truncated silently.

## License & attribution

MIT. Original model by Cardiff NLP (Cardiff University). ONNX export by
Joshua Lochner (Xenova).

- Paper: [TweetEval: Unified Benchmark and Comparative Evaluation for Tweet Classification](https://arxiv.org/abs/2010.12421)
- Upstream: [cardiffnlp/twitter-roberta-base-sentiment-latest](https://huggingface.co/cardiffnlp/twitter-roberta-base-sentiment-latest)
- ONNX export: [Xenova/twitter-roberta-base-sentiment-latest](https://huggingface.co/Xenova/twitter-roberta-base-sentiment-latest)
