---
title: String Functions
category: string
---

# String Functions

PostgreSQL-compatible string functions. All functions return NULL when any required argument is NULL.

## Length

### len

`len(val)` → Float32 | QU: 1

Length of string, collection, or array. Also works on Vector, UInt8Array, Matrix, Tensor, JsonValue, and Array.

```sql
SELECT len('hello') -- 5
```

### length

`length(str)` → Float32 | QU: 1

Number of characters. Alias for `len()`.

```sql
SELECT length('hello') -- 5
```

### char_length

`char_length(str)` → Float32 | QU: 1

SQL standard alias for `len()`.

```sql
SELECT char_length('hello') -- 5
```

### character_length

`character_length(str)` → Float32 | QU: 1

SQL standard alias for `len()`.

```sql
SELECT character_length('hello') -- 5
```

### octet_length

`octet_length(str)` → Float32 | QU: 1

Number of bytes in the string (UTF-8 encoded).

```sql
SELECT octet_length('hello') -- 5
SELECT octet_length('cafe\u0301') -- more than char count for multi-byte chars
```

### bit_length

`bit_length(str)` → Float32 | QU: 1

Number of bits in the string (8 x `octet_length`).

```sql
SELECT bit_length('hello') -- 40
```

## Case Conversion

### upper

`upper(str)` → String | QU: 1

Convert to uppercase (invariant).

```sql
SELECT upper('hello') -- 'HELLO'
```

### lower

`lower(str)` → String | QU: 1

Convert to lowercase (invariant).

```sql
SELECT lower('HELLO') -- 'hello'
```

### initcap

`initcap(str)` → String | QU: 1

Capitalize first letter of each word, lowercase the rest. Non-alphanumeric characters are word boundaries.

```sql
SELECT initcap('hello world') -- 'Hello World'
```

## Substring Extraction

### substring

`substring(str, start, [length])` → String | QU: 1

Extract substring from 1-based start position. Optional `length` limits the result.

```sql
SELECT substring('hello world', 7)    -- 'world'
SELECT substring('hello world', 1, 5) -- 'hello'
```

### substr

`substr(str, start, [length])` → String | QU: 1

Alias for `substring()`.

```sql
SELECT substr('hello world', 7) -- 'world'
```

### mid

`mid(str, start, length)` → String | QU: 1

Extract substring by 1-based position and length.

```sql
SELECT mid('hello world', 1, 5) -- 'hello'
```

### left

`left(str, n)` → String | QU: 1

First n characters. When n is negative, returns all but the last |n| characters.

```sql
SELECT left('hello', 3)  -- 'hel'
SELECT left('hello', -2) -- 'hel'
```

### right

`right(str, n)` → String | QU: 1

Last n characters. When n is negative, returns all but the first |n| characters.

```sql
SELECT right('hello', 3)  -- 'llo'
SELECT right('hello', -2) -- 'llo'
```

### overlay

`overlay(str, new, start, [count])` → String | QU: 1

Replace `count` characters at 1-based `start` with `new`. Count defaults to `length(new)`.

```sql
SELECT overlay('Txxxxas', 'hom', 2, 4) -- 'Thomas'
```

## Trimming

### trim

`trim(str, [chars])` → String | QU: 1

Remove characters from both sides. Default: whitespace.

```sql
SELECT trim('  hello  ')           -- 'hello'
SELECT trim('xyxtrimyyx', 'xyz')   -- 'trim'
```

### ltrim

`ltrim(str, [chars])` → String | QU: 1

Remove leading characters. Default: whitespace.

```sql
SELECT ltrim('zzzytest', 'xyz') -- 'test'
```

### rtrim

`rtrim(str, [chars])` → String | QU: 1

Remove trailing characters. Default: whitespace.

```sql
SELECT rtrim('testxxzx', 'xyz') -- 'test'
```

### btrim

`btrim(str, [chars])` → String | QU: 1

Trim characters from both sides. PostgreSQL alias for `trim()`.

```sql
SELECT btrim('xyxtrimyyx', 'xyz') -- 'trim'
```

## Padding

### lpad

`lpad(str, len, [fill])` → String | QU: 1

Pad on the left to target length with fill string (default space). Truncates from the right if already longer.

```sql
SELECT lpad('hi', 5)         -- '   hi'
SELECT lpad('hi', 5, 'xy')   -- 'xyxhi'
```

### rpad

`rpad(str, len, [fill])` → String | QU: 1

Pad on the right to target length with fill string (default space). Truncates if already longer.

```sql
SELECT rpad('hi', 5)         -- 'hi   '
SELECT rpad('hi', 5, 'xy')   -- 'hixyx'
```

## Search

### position

`position(str, sub)` → Float32 | QU: 1

1-based index of first occurrence, or 0 if not found.

```sql
SELECT position('hello world', 'world') -- 7
```

### strpos

`strpos(str, sub)` → Float32 | QU: 1

Same as `position()`.

```sql
SELECT strpos('hello world', 'world') -- 7
```

### contains

`contains(str, sub)` → Boolean | QU: 1

Returns whether str contains sub (ordinal).

```sql
SELECT contains('hello world', 'world') -- true
```

### starts_with

`starts_with(str, prefix)` → Boolean | QU: 1

Returns whether str starts with prefix (ordinal).

```sql
SELECT starts_with('hello world', 'hello') -- true
```

### ends_with

`ends_with(str, suffix)` → Boolean | QU: 1

Returns whether str ends with suffix (ordinal).

```sql
SELECT ends_with('hello world', 'world') -- true
```

## Replacement

### replace

`replace(str, old, new)` → String | QU: 1

Replace all occurrences of old with new (ordinal).

```sql
SELECT replace('hello world', 'world', 'there') -- 'hello there'
```

### translate

`translate(str, from, to)` → String | QU: 1

Character-by-character substitution. Characters in `from` without a `to` counterpart are deleted.

```sql
SELECT translate('12345', '143', 'ax') -- 'a2x5'
```

### regexp_replace

`regexp_replace(str, pattern, replacement, [flags])` → String | QU: 1

Replace regex matches. Default replaces all; pass `'i'` for case-insensitive, `'g'` for global, `'gi'` for both. Without `'g'`, replaces first match only.

```sql
SELECT regexp_replace('Hello World', '[aeiou]', '*', 'gi') -- 'H*ll* W*rld'
```

## Concatenation

### concat

`concat(a, b, ...)` → String | QU: 1

Concatenate two or more strings. Null args treated as empty.

```sql
SELECT concat('hello', ' ', 'world') -- 'hello world'
```

### concat_ws

`concat_ws(sep, s1, s2, ...)` → String | QU: 1

Concatenate with separator, skipping nulls.

```sql
SELECT concat_ws(', ', 'a', 'b', 'c') -- 'a, b, c'
```

### repeat

`repeat(str, count)` → String | QU: 1

Repeat string count times.

```sql
SELECT repeat('ha', 3) -- 'hahaha'
```

## Splitting

### split_part

`split_part(str, delim, n)` → String | QU: 1

Split on delimiter, return the n-th field (1-based). Empty string if out of range. Negative n counts from end.

```sql
SELECT split_part('a.b.c', '.', 2) -- 'b'
SELECT split_part('a.b.c', '.', -1) -- 'c'
```

## Character Codes

### ascii

`ascii(str)` → Float32 | QU: 1

Unicode code point of first character. Returns 0 for empty string.

```sql
SELECT ascii('A') -- 65
```

### chr

`chr(code)` → String | QU: 1

Character from Unicode code point.

```sql
SELECT chr(65) -- 'A'
```

## Regular Expressions

Flags: `'i'` for case-insensitive. `regexp_replace` also supports `'g'` for global.

### regexp_extract

`regexp_extract(str, pattern, [group])` → String | QU: 1

Extract first regex match. Optional 1-based group index returns a capture group. NULL if no match.

```sql
SELECT regexp_extract('abc123', '\d+')        -- '123'
SELECT regexp_extract('abc123', '(\d+)', 1)   -- '123'
```

### regexp_count

`regexp_count(str, pattern, [start], [flags])` → Float32 | QU: 1

Number of times pattern matches. Optional 1-based start.

```sql
SELECT regexp_count('abc123def456', '\d+') -- 2
```

### regexp_like

`regexp_like(str, pattern, [flags])` → Boolean | QU: 1

Returns whether pattern matches anywhere in the string.

```sql
SELECT regexp_like('Hello World', 'world$', 'i') -- true
```

### regexp_match

`regexp_match(str, pattern, [flags])` → Array | QU: 1

Captured substrings from the first match as an Array. NULL if no match.

```sql
SELECT regexp_match('2024-03-26', '(\d{4})-(\d{2})-(\d{2})') -- ['2024','03','26']
```

### regexp_substr

`regexp_substr(str, pattern, [start], [N], [flags], [subexpr])` → String | QU: 1

The N'th match (default 1). `subexpr` selects a capture group. NULL if no match.

```sql
SELECT regexp_substr('abc123def456', '\d+', 1, 2) -- '456'
```

### regexp_instr

`regexp_instr(str, pattern, [start], [N], [endoption], [flags], [subexpr])` → Float32 | QU: 1

1-based position of N'th match. `endoption=1` returns end+1. Returns 0 if no match.

```sql
SELECT regexp_instr('ABCDEF', 'c(.)(..)', 1, 1, 0, 'i') -- 3
```

## Formatting

### format

`format(fmt, ...)` → String | QU: 1

sprintf-style formatting. `%s` string, `%I` SQL identifier, `%L` SQL literal, `%%` literal `%`. Positional: `%1$s`.

```sql
SELECT format('Hello %s, %1$s', 'World')  -- 'Hello World, World'
SELECT format('INSERT INTO %I VALUES (%L)', 'my_table', 'O''Reilly')
```

## Array Splitting

### string_to_array

`string_to_array(str, delim, [null_str])` → Array | QU: 1

Split by delimiter into an Array. NULL delimiter splits into characters. Fields matching `null_str` become NULL.

```sql
SELECT string_to_array('xx~~yy~~zz', '~~', 'yy') -- ['xx', NULL, 'zz']
```

### regexp_split_to_array

`regexp_split_to_array(str, pattern, [flags])` → Array | QU: 1

Split by regex into an Array.

```sql
SELECT regexp_split_to_array('hello world', '\s+') -- ['hello', 'world']
```

## Base Conversion

### to_hex

`to_hex(n)` → String | QU: 1

Hexadecimal string representation.

```sql
SELECT to_hex(255) -- 'ff'
```

### to_bin

`to_bin(n)` → String | QU: 1

Binary string representation.

```sql
SELECT to_bin(10) -- '1010'
```

### to_oct

`to_oct(n)` → String | QU: 1

Octal string representation.

```sql
SELECT to_oct(8) -- '10'
```

## Unicode

### normalize

`normalize(str, [form])` → String | QU: 1

Unicode normalization. Form: `NFC` (default), `NFD`, `NFKC`, `NFKD`.

```sql
SELECT normalize('cafe\u0301', 'NFC') -- 'caf\u00e9'
```

### to_ascii

`to_ascii(str)` → String | QU: 1

Transliterate to ASCII by removing diacritical marks.

```sql
SELECT to_ascii('caf\u00e9') -- 'cafe'
```

### unistr

`unistr(str)` → String | QU: 1

Evaluate Unicode escapes: `\XXXX`, `\+XXXXXX`, `\uXXXX`, `\UXXXXXXXX`. `\\` for literal backslash.

```sql
SELECT unistr('d\0061t\+000061') -- 'data'
```

### casefold

`casefold(str)` → String | QU: 1

Unicode case folding for case-insensitive comparison.

```sql
SELECT casefold('Stra\u00dfe') -- 'strasse'
```

## SQL Quoting

### quote_ident

`quote_ident(str)` → String | QU: 1

Quote as a SQL identifier (double-quoted) when necessary.

```sql
SELECT quote_ident('my table') -- '"my table"'
```

### quote_literal

`quote_literal(val)` → String | QU: 1

Quote as a SQL string literal (single-quoted). NULL on NULL input.

```sql
SELECT quote_literal('O''Reilly') -- '''O''''Reilly'''
```

### quote_nullable

`quote_nullable(val)` → String | QU: 1

Like `quote_literal`, but returns the string `'NULL'` for NULL input.

```sql
SELECT quote_nullable(NULL) -- 'NULL'
```

### parse_ident

`parse_ident(str, [strict])` → Array | QU: 1

Split a qualified identifier into an Array, removing quotes and folding to lowercase.

```sql
SELECT parse_ident('"Schema"."Table"') -- ['schema', 'table']
```

## Other

### reverse

`reverse(str)` → String | QU: 1

Reverse character order.

```sql
SELECT reverse('hello') -- 'olleh'
```

### word_count

`word_count(str)` → Float32 | QU: 1

Count whitespace-separated words.

```sql
SELECT word_count('hello world foo') -- 3
```

### get_filename

`get_filename(path)` → String | QU: 1

Return file name with extension from path.

```sql
SELECT get_filename('/data/images/photo.jpg') -- 'photo.jpg'
```

### get_file_extension

`get_file_extension(path)` → String | QU: 1

Return extension (with dot) from path.

```sql
SELECT get_file_extension('/data/images/photo.jpg') -- '.jpg'
```

### get_path

`get_path(path)` → String | QU: 1

Return directory portion of path.

```sql
SELECT get_path('/data/images/photo.jpg') -- '/data/images'
```

## See Also

- [Array Functions](array.md) -- array construction, manipulation, and search
- [JSON Functions](json.md) -- JSON path access and array inspection
- [Utility & Type Conversion Functions](utility.md) -- type checks, casting, and conditionals
