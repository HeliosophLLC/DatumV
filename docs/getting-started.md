---
title: Getting Started
---

## What Is DatumV?

DatumV is a SQL-based tool for preparing data for machine learning. If you've ever needed to load a CSV, join it with another dataset, clean up bad rows, normalize some columns, and export the result as Parquet — all without writing Python — DatumV does that in a single SQL query.

It supports standard SQL (SELECT, JOIN, GROUP BY, window functions) plus ML-specific extensions: vector operations, image transforms, type-safe tensors, sampling, cross-validation, and 200+ built-in functions.

## Installing

```bash
dotnet tool install --global DatumV.Cli
```

Verify it works:

```bash
datumv --version
```

## Your First Query

Say you have a CSV file called `iris.csv`:

```csv
sepal_length,sepal_width,petal_length,petal_width,species
5.1,3.5,1.4,0.2,setosa
7.0,3.2,4.7,1.4,versicolor
...
```

To peek at the data:

```bash
datumv explore "SELECT * FROM iris LIMIT 5" --source "iris=./iris.csv"
```

The `--source` flag tells DatumV where the data lives. The format is `name=path` — the name becomes the table name you use in SQL.

## The Three-Step Pipeline

Almost every DatumV workflow follows the same pattern: **load → transform → export**.

### Step 1: Load (FROM + --source)

```bash
# CSV (auto-detected from extension)
--source "orders=./orders.csv"

# Parquet
--source "features=./embeddings.parquet"

# ZIP of images (columns: file_name, file_bytes)
--source "images=./train2017.zip"

# JSON
--source "labels=./annotations.json"

# Multiple sources in one query
--source "orders=./orders.csv" --source "customers=./customers.csv"
```

The format is auto-detected from the file extension. For explicit control, prefix with the provider name: `csv:data=./file.txt`.

### Step 2: Transform (SELECT, WHERE, JOIN, functions)

This is where you do the work — filter bad rows, join tables, compute features, normalize values:

```sql
SELECT
  customer_id,
  total_spent / order_count AS avg_order_value,
  CASE WHEN churn_score > 0.7 THEN 'high_risk' ELSE 'normal' END AS risk_tier
FROM customers
WHERE total_spent > 0
ORDER BY avg_order_value DESC
```

### Step 3: Export (INTO)

Write results to a file. The format is inferred from the extension:

```bash
datumv query "SELECT * FROM customers INTO 'output.parquet'" --source "customers=./customers.csv"
```

Supported output formats:

| Extension | Format | Best for |
|-----------|--------|----------|
| `.csv` | CSV | Spreadsheets, simple tools |
| `.parquet` | Parquet | ML frameworks, columnar analytics |
| `.h5` / `.hdf5` | HDF5 | NumPy, TensorFlow, PyTorch |

For large exports, shard the output into manageable chunks:

```sql
SELECT * FROM data INTO 'output.parquet' SHARD ON sample_count 50000
-- Creates: output_shard_00000.parquet, output_shard_00001.parquet, ...
```

## Interactive Mode (Shell)

For exploration, use the interactive shell:

```bash
datumv shell --source "orders=./orders.csv" --source "products=./products.csv"
```

Inside the shell you can run queries, inspect tables, and iterate without restarting:

```
datum> .tables
orders    (5 columns, csv)
products  (4 columns, csv)

datum> SELECT * FROM orders LIMIT 3
order_id  customer_id  product_id  quantity  price
1         42           101         2         29.99
2         42           203         1         9.99
3         17           101         5         29.99

datum> .schema orders
Column        Type      Nullable
order_id      Float32   YES
customer_id   Float32   YES
product_id    Float32   YES
quantity      Float32   YES
price         Float32   YES
```

## A Real Example: Preparing Customer Features

Let's walk through a realistic scenario. You have two CSV files — `orders.csv` and `customers.csv` — and you want to build a feature table for a churn prediction model.

### 1. Explore the data

```bash
datumv shell --source "orders=./orders.csv" --source "customers=./customers.csv"
```

```sql
-- What does the orders table look like?
SELECT * FROM orders LIMIT 5

-- How many orders per customer?
SELECT customer_id, COUNT(*) AS order_count
FROM orders
GROUP BY customer_id
ORDER BY order_count DESC
LIMIT 10
```

### 2. Build features with a JOIN

```sql
SELECT
  c.customer_id,
  c.signup_date,
  c.region,
  COUNT(o.order_id) AS order_count,
  SUM(o.price * o.quantity) AS total_spent,
  AVG(o.price) AS avg_price,
  MAX(o.order_date) AS last_order_date,
  date_diff('day', MAX(o.order_date), CURRENT_DATE) AS days_since_last_order
FROM customers AS c
LEFT JOIN orders AS o ON c.customer_id = o.customer_id
GROUP BY c.customer_id, c.signup_date, c.region
```

### 3. Add derived features and quality checks

```sql
SELECT DEFINE {
  LET tenure = date_diff('day', signup_date, CURRENT_DATE);
  LET avg_order = total_spent / NULLIF(order_count, 0);
  ASSERT order_count >= 0 MESSAGE 'negative order count' ON FAIL SKIP;
}
  customer_id,
  tenure AS tenure_days,
  order_count,
  total_spent,
  avg_order AS avg_order_value,
  days_since_last_order,
  CASE
    WHEN days_since_last_order > 90 THEN 'churned'
    WHEN days_since_last_order > 30 THEN 'at_risk'
    ELSE 'active'
  END AS status
FROM (
  SELECT
    c.customer_id,
    c.signup_date,
    COUNT(o.order_id) AS order_count,
    COALESCE(SUM(o.price * o.quantity), 0) AS total_spent,
    date_diff('day', MAX(o.order_date), CURRENT_DATE) AS days_since_last_order
  FROM customers AS c
  LEFT JOIN orders AS o ON c.customer_id = o.customer_id
  GROUP BY c.customer_id, c.signup_date
) AS base
```

### 4. Export

```bash
datumv query "
  SELECT ...  -- (the query above)
  INTO 'customer_features.parquet'
" --source "orders=./orders.csv" --source "customers=./customers.csv"
```

Your Parquet file is ready for pandas, scikit-learn, or any ML framework.

## A Real Example: Preparing an Image Dataset

You have a ZIP of images and a JSON annotations file. You want to resize all images to 224x224, pair them with their labels, and export as HDF5 for PyTorch.

```bash
datumv query "
  SELECT
    resize(img.file_bytes, 224, 224) AS image,
    cap.label
  FROM images AS img
  INNER JOIN annotations AS cap ON get_filename(img.file_name) = cap.filename
  WHERE cap.label IS NOT NULL
  INTO 'training_data.h5'
" \
  --source "images=./train2017.zip" \
  --source "json:annotations=./labels.json"
```

That's it — no Python script, no intermediate files, no memory issues. DatumV streams through the ZIP, joins with annotations, resizes each image, and writes directly to HDF5.

## Sampling and Cross-Validation

### Quick sample for exploration

```sql
-- Random 1% sample
SELECT * FROM training_data TABLESAMPLE BERNOULLI(1)
```

### Balance an imbalanced dataset

```sql
-- Exactly 1000 rows per class
SELECT * FROM training_data
TABLESAMPLE BALANCED(1000) ON label REPEATABLE(42)
INTO 'balanced_train.parquet'
```

### Set up k-fold cross-validation

```sql
-- Tag each row with a fold number (0-4), then export
SELECT *, fold
FROM training_data
CROSS VALIDATE(k = 5, seed = 42) ON id AS fold
INTO 'training_with_folds.parquet'
```

## What's Next?

Now that you have the basics, explore these topics based on what you need:

| I want to... | Read |
|--------------|------|
| Filter and clean data | [WHERE](sql/filtering.md) |
| Join multiple tables | [JOIN](sql/joins.md) |
| Aggregate and group | [GROUP BY](sql/group-by.md) |
| Rank and window | [Window Functions](sql/window-functions.md), [QUALIFY](sql/qualify.md) |
| Compute reusable values | [LET Bindings](sql/let-bindings.md) |
| Validate data quality | [ASSERT](sql/assert.md) |
| Reshape wide/long | [PIVOT / UNPIVOT](sql/pivot-unpivot.md) |
| Sample and balance | [TABLESAMPLE](sql/tablesample.md) |
| Cross-validate | [CROSS VALIDATE](sql/cross-validate.md) |
| Export results | [INTO](sql/into.md) |
| Browse all functions | [Functions Reference](functions/string.md) |
| Understand the type system | [Type System](sql/type-system.md) |
| Inspect query performance | [EXPLAIN](sql/explain.md) |
