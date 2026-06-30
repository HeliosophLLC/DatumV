---
title: List Accumulators (List<T>)
---

A `List<T>` is a growable, in-place accumulator for building an array one
element at a time inside a [procedural body](procedural.md) or script. You
declare it, grow it with `APPEND`, and read it back as an ordinary
`Array<T>` — it freezes to its array automatically the moment you use it.

```sql
DECLARE squares List<Int32>;
DECLARE i INT32 = 1;
WHILE i <= 5 BEGIN
  APPEND i * i TO squares;
  SET i = i + 1
END
SELECT squares    -- [1, 4, 9, 16, 25]
```

## Why use it

The natural way to build an array in a loop is to keep reassigning it:

```sql
DECLARE acc Float32[] = [];
WHILE more BEGIN
  SET acc = array_append(acc, next_value)   -- rebuilds the whole array each step
END
```

Every `array_append` (or `array_concat`) produces a *new* array — arrays
are immutable values, so each step copies everything accumulated so far.
Building an array of N elements this way does work proportional to N², and
the intermediate copies pile up. For a handful of elements that's fine; for
a loop that accumulates hundreds or thousands of values it is the dominant
cost.

`List<T>` removes that cost. `APPEND` grows the list in place, so adding an
element is cheap regardless of how large the list has already grown.
Building the same N-element result is proportional to N, with no
intermediate copies. The list is a *builder*; the array is the result.

## Declaring a list

Declare a list with the `List<T>` type and **no initializer** — it starts
empty and is filled by `APPEND`:

```sql
DECLARE scores  List<Float32>;
DECLARE ids     List<Int64>;
DECLARE flags   List<Boolean>;
```

`T` is the element type, fixed at declaration. It must be a fixed-width
primitive — the numeric kinds (`Int8`…`Int64`, `UInt8`…`UInt64`,
`Float32`, `Float64`), `Boolean`, and the date/time kinds. Reference
element types (`String`, `Image`, `Struct`, …) are not supported.

A `List<T>` is **local to the body or script that declares it** and is not
a storable type — you cannot use it as a table column, a cast target, or a
function parameter. It exists only to build an array.

## APPEND

`APPEND value TO list` adds to the end of the list. The value can be a
single element or another array, which is concatenated:

```sql
DECLARE v List<Float32>;

APPEND 1.5 TO v;                                      -- → [1.5]
APPEND CAST(2.5 AS Float32) TO v;                     -- → [1.5, 2.5]
APPEND [3.0::Float32, 4.0::Float32] TO v;             -- append every element → [1.5, 2.5, 3.0, 4.0]
```

A single appended value is **coerced to the element type**, so you don't
need to cast literals: `APPEND 1 TO List<Int32>` works even though the
literal `1` is a narrower kind. A non-numeric value that has no conversion
to the element type (for example appending a string to a numeric list) is
an error.

When appending an **array**, its element type must already match the
list's element type — cast the array's elements first if they don't.

## RESERVE

If you know roughly how many elements you'll add, `RESERVE` pre-sizes the
list so it doesn't grow incrementally:

```sql
DECLARE buf List<Float32>;
RESERVE 4096 FOR buf;
-- ... append up to ~4096 elements without reallocating ...
```

`RESERVE` is a pure hint: it changes capacity, not length. The list is
still empty afterwards, and appending more than the reserved amount is
fine — it just grows again. Use it when the upper bound is known and the
list is large; otherwise it's unnecessary.

## Reading a list — it freezes to Array<T>

You never have to convert a list explicitly. Whenever a list is *read* — passed
to a function, returned, or projected by a `SELECT` — it freezes to a flat
`Array<T>` of its current contents:

```sql
DECLARE xs List<Int32>;
APPEND 10 TO xs; APPEND 20 TO xs; APPEND 30 TO xs;

SELECT cardinality(xs)      -- 3        (xs is read as Int32[])
SELECT array_sum(xs)        -- 60
SELECT xs                   -- [10, 20, 30]
```

Freezing copies the accumulated elements into an array once, at the point
of use. After that point the array behaves like any other `Array<T>`: it
can be stored, returned from a model, indexed, or passed to array
functions. The list itself remains usable — you can keep appending and read
it again later, producing a fresh array each time.

The typical shape is *accumulate in a loop, read once at the end*:

```sql
CREATE FUNCTION running_squares(n INT32) RETURNS INT32[]
BEGIN
  DECLARE out List<Int32>;
  DECLARE i INT32 = 1;
  WHILE i <= n BEGIN
    APPEND i * i TO out;
    SET i = i + 1
  END
  RETURN out               -- frozen to Int32[] on the way out
END
```

## Where lists can be used

`List<T>` works anywhere procedural statements do:

- inside a [`CREATE FUNCTION`](udf.md) body,
- inside a `CREATE MODEL` body (see [Defining Models](create-model.md)),
- and in a top-level [procedural script](procedural.md).

```sql
-- Top-level script: build a list, then use it.
DECLARE evens List<Int32>;
DECLARE k INT32 = 0;
WHILE k < 10 BEGIN
  APPEND k * 2 TO evens;
  SET k = k + 1
END
SELECT evens AS doubled, cardinality(evens) AS n
```

## Relationship to immutable arrays

A `List<T>` is the mutable counterpart of `Array<T>`. Anything you express
with a list can be written with immutable-array operations — the list form
is faster, not more expressive:

| List form | Immutable-array equivalent |
|---|---|
| `DECLARE acc List<Float32>` | `DECLARE acc Float32[] = []` |
| `APPEND x TO acc` | `SET acc = array_append(acc, x)` |
| `APPEND arr TO acc` | `SET acc = array_concat(acc, arr)` |
| `RESERVE n FOR acc` | *(no equivalent — capacity is a list-only hint)* |

Reach for `List<T>` when you are building an array element-by-element in a
loop. For small, fixed collections an array literal or a couple of
`array_append` calls is simpler and just as fast.

## See also

- [Procedural Statements](procedural.md) — `DECLARE`, `SET`, `WHILE`, `FOR`.
- [Type System](type-system.md) — `Array<T>` and the element kinds.
- [User-Defined Functions](udf.md) — procedural function bodies.
