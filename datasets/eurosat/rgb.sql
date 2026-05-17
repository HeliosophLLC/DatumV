-- EuroSAT RGB recipe. Reads the Zenodo-hosted ZIP of 27,000 Sentinel-2
-- RGB tiles, organised one folder per land-use class. The class label is
-- extracted from the path stem: 'EuroSAT_RGB/Forest/Forest_1234.jpg'
-- → 'Forest'.
SELECT
    path,
    image_decode(bytes) AS img,
    regexp_replace(path, '^[^/]+/([^/]+)/[^/]+$', '\1') AS class,
    size AS file_bytes,
    modified
FROM open_archive($archive)
WHERE get_filename_ext(path) IN ('jpg', 'jpeg', 'JPG', 'JPEG')
