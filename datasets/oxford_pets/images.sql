-- Oxford-IIIT Pets recipe. Reads the VGG-hosted images.tar.gz of 7,349
-- pet portraits (37 cat + dog breeds, ~200 images each). Filenames carry
-- the breed: 'images/Abyssinian_42.jpg' → 'Abyssinian',
-- 'images/basset_hound_17.jpg' → 'basset_hound'. Cat breeds start with a
-- capital letter, dog breeds lowercase — preserved verbatim here so a
-- caller can derive cat vs dog with a simple character check.
SELECT
    path,
    image_decode(bytes) AS img,
    regexp_replace(path, '^images/([A-Za-z_]+)_\d+\.[A-Za-z]+$', '\1') AS breed,
    size AS file_bytes,
    modified
FROM open_archive($archive)
WHERE get_filename_ext(path) IN ('jpg', 'jpeg', 'JPG', 'JPEG')
