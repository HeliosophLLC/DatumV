-- CIFAR-10 test recipe. Single 10,000-record batch (test_batch.bin)
-- pulled out of the upstream tar.gz and fed into open_cifar10. With one
-- source batch the TVF's local idx is already global, so we surface it
-- unchanged.
WITH labels AS (
    SELECT
        CAST(row_number() OVER () - 1 AS UInt8) AS label_id,
        f.fields[1] AS label_name
    FROM open_archive($artifact, 'cifar-10-batches-bin/batches.meta.txt') tt
    JOIN read_csv(tt.bytes) f
    WHERE f.fields[1] <> ''
)
SELECT
    row_number() OVER () - 1 AS idx,
    c.image AS img,
    c.label AS label,
    labels.label_name AS label_name
FROM open_archive($artifact, 'cifar-10-batches-bin/test_batch.bin') AS a
CROSS JOIN open_cifar10(a.bytes) AS c
JOIN labels ON labels.label_id = c.label