# MS MARCO License Terms

The MS MARCO datasets were released by Microsoft in 2016 (initial release) and
have been updated through several follow-on competitions and BEIR-style
re-releases. They are distributed under a non-standard set of "MS MARCO" terms
authored by Microsoft Research — not under a Creative Commons or open-source
license — granting a non-exclusive, royalty-free right to use the dataset for
non-commercial research purposes, with attribution.

## What you can do

- Use the dataset for research, experimentation, and benchmarking.
- Train and evaluate models on it (passage retrievers, rerankers, generative
  QA, embedding models).
- Publish numbers against the standard MS MARCO splits (dev / eval / TREC
  Deep Learning subsets) without separate clearance.
- Redistribute the dataset in unmodified form for research use, with
  attribution back to Microsoft.

## What you should still do

- **Attribute Microsoft** as the source whenever the dataset is used,
  redistributed, or referenced — including paper citations, blog posts, and
  downstream dataset cards.
- **Treat commercial deployment as a separate question.** The release terms
  are explicitly research-oriented. Productionising a model that was trained
  on MS MARCO (or evaluating it as part of a paid product) is not addressed
  by the dataset license and should be reviewed independently.
- **Do not redistribute the held-out evaluation labels.** The TREC Deep
  Learning evaluation qrels are released asynchronously and treating them
  as part of the public corpus undermines the benchmark.
- **Respect Bing's terms.** The passages are extracted from real Bing web
  search results; redistributing the source URLs or attempting to re-crawl
  the linked pages remains subject to Bing's and the linked sites'
  individual terms.

## Background

- Original release: Bajaj, Nguyen, et al. — *MS MARCO: A Human Generated
  Machine Reading Comprehension Dataset* (2016).
- Project site:
  [microsoft.github.io/msmarco/](https://microsoft.github.io/msmarco/)
- Passage retrieval task overview:
  [microsoft.github.io/msmarco/Datasets#passage-ranking-dataset](https://microsoft.github.io/msmarco/Datasets#passage-ranking-dataset)