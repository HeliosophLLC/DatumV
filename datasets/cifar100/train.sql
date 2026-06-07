WITH fine_labels AS (
    SELECT
        CAST(row_number() OVER () - 1 AS UInt8) AS label_id,
        f.fields[1] AS label_name
    FROM open_archive($artifact, 'cifar-100-binary/fine_label_names.txt') tt
    JOIN read_csv(tt.bytes) f
),
coarse_labels AS (
    SELECT
        CAST(row_number() OVER () - 1 AS UInt8) AS label_id,
        f.fields[1] AS label_name
    FROM open_archive($artifact, 'cifar-100-binary/coarse_label_names.txt') tt
    JOIN read_csv(tt.bytes) f
)
SELECT
    c.idx AS idx,
    c.image AS img,
    c.coarse_label AS coarse_label,
    c.fine_label AS fine_label,
    coarse_labels.label_name AS coarse_label_name,
    fine_labels.label_name AS fine_label_name
FROM open_archive($artifact, 'cifar-100-binary/train.bin') AS a
CROSS JOIN open_cifar100(a.bytes) AS c
JOIN coarse_labels ON coarse_labels.label_id = c.coarse_label
JOIN fine_labels   ON fine_labels.label_id   = c.fine_label