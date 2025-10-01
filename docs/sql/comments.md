---
title: Comments
---

Line comments start with `--` and continue to the end of the line. Block comments are enclosed in `/* ... */`. Both styles are stripped during tokenization and may appear anywhere whitespace is allowed.

```sql
-- This is a line comment
SELECT
    col1,           -- inline comment
    /* col2, */     -- block comment can disable code
    col3
FROM my_table
```

Block comments do not nest.

## See Also

- [SELECT](select.md)
- [FROM](from.md)
- [WHERE](filtering.md)
