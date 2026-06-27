# Quora Question Pairs Release Terms

The Quora Question Pairs dataset was released by Quora in January 2017 alongside
the Kaggle competition of the same name. It was not formally placed under a
Creative Commons or other standard open-source license; instead, Quora granted a
non-exclusive, royalty-free license to use the dataset for research and
educational purposes, with attribution.

## What you can do

- Use the dataset for research, experimentation, and benchmarking.
- Train and evaluate models on it (sentence embedders, duplicate-question
  detectors, paraphrase classifiers).
- Redistribute the dataset itself in unmodified form for research use, with
  attribution back to Quora.

## What you should still do

- **Attribute Quora** as the source of the dataset whenever it is used,
  redistributed, or referenced in published work — including paper citations,
  blog posts, and downstream dataset cards.
- **Treat commercial deployment as a separate question.** The 2017 release
  predates Quora's current Terms of Service and was framed around research /
  Kaggle participation; if a commercial product depends on this corpus, confirm
  rights independently rather than relying on the original blog-post language.
- **Do not redistribute user-identifying metadata.** The release strips Quora
  user ids; downstream republications should not attempt to re-attach them.

## Background

- Original release announcement:
  [First Quora Dataset Release: Question Pairs](https://quoradata.quora.com/First-Quora-Dataset-Release-Question-Pairs)
  (Iyer, Dandekar, Csernai — January 2017).
- Kaggle competition:
  [Quora Question Pairs](https://www.kaggle.com/competitions/quora-question-pairs).
- Schema: 404,290 rows of `(id, qid1, qid2, question1, question2, is_duplicate)`
  where `is_duplicate` is a 0/1 human-labelled flag.

## SPDX identifier

No formal SPDX identifier is registered. Catalogued here as `quora-qp-terms`.
