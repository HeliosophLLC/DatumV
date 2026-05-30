-- MedNIST recipe. Reads the MONAI-hosted MedNIST.tar.gz of ~58,954
-- 64×64 grayscale medical thumbnails, organised one folder per
-- modality class: 'MedNIST/AbdomenCT/000000.jpeg' → 'AbdomenCT'.
SELECT
    path,
    image_decode(bytes) AS img,
    regexp_replace(path, '^[^/]+/([^/]+)/[^/]+$', '\1') AS class,
    size AS file_bytes,
    modified
FROM open_archive($archive)
WHERE get_filename_ext(path) IN ('jpg', 'jpeg', 'JPG', 'JPEG')
