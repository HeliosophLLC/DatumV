# Oxford-IIIT Pets

7,349 photographs of cats and dogs across 37 breeds (~200 images per
breed), released in 2012 by Parkhi, Vedaldi, Zisserman, and Jawahar
from Oxford's VGG group. The reach-for small fine-grained
classification benchmark.

Strikes a useful middle ground in the benchmark ladder: harder than
CIFAR-10 (the 37 breeds are visually close, especially within
species), small enough to fit a training loop on a laptop
(~800 MB on disk), and the upstream is hosted directly by Oxford VGG
with a permanent URL.

## Output columns

The single `oxford_pets_images` variant produces an `images` table of
7,349 rows:

```
path:        String      -- entry path inside the source tar.gz (e.g. 'images/Abyssinian_42.jpg')
img:         Image       -- decoded JPEG, sidecar-backed
breed:       String      -- breed name extracted from the filename
file_bytes:  Int64       -- compressed JPEG payload size
modified:    Timestamp   -- tar entry mtime
```

The `breed` column is the canonical filename token (`'Abyssinian'`,
`'basset_hound'`, …), not a numeric class ID. Filename convention is
load-bearing: **cat breeds start with an uppercase letter, dog breeds
are all-lowercase**, so deriving species from breed is a single-char
check.

## Label vocabulary

12 cat breeds + 25 dog breeds = 37 classes total. A representative
slice:

```
Cats (12, capitalised):
  Abyssinian, Bengal, Birman, Bombay, British_Shorthair, Egyptian_Mau,
  Maine_Coon, Persian, Ragdoll, Russian_Blue, Siamese, Sphynx

Dogs (25, lowercase):
  american_bulldog, american_pit_bull_terrier, basset_hound, beagle,
  boxer, chihuahua, english_cocker_spaniel, english_setter,
  german_shorthaired, great_pyrenees, havanese, japanese_chin,
  keeshond, leonberger, miniature_pinscher, newfoundland, pomeranian,
  pug, saint_bernard, samoyed, scottish_terrier, shiba_inu,
  staffordshire_bull_terrier, wheaten_terrier, yorkshire_terrier
```

## Example SQL

Class distribution (should be ~200 per breed; small variance):

```sql
SELECT breed, COUNT(*) AS n
FROM datasets.oxford_pets_images
GROUP BY breed
ORDER BY breed;
```

Cat-vs-dog derivation using the capitalisation convention:

```sql
SELECT
  CASE WHEN SUBSTRING(breed, 1, 1) = UPPER(SUBSTRING(breed, 1, 1)) THEN 'cat' ELSE 'dog' END AS species,
  COUNT(*) AS n
FROM datasets.oxford_pets_images
GROUP BY species;
-- Expected: cat ≈ 2,400 (12 × ~200), dog ≈ 4,950 (25 × ~200)
```

Run a classifier and check per-breed accuracy:

```sql
WITH preds AS (
  SELECT breed AS truth, models.your_pet_classifier(img) AS pred
  FROM datasets.oxford_pets_images
)
SELECT truth,
       AVG(CASE WHEN truth = pred THEN 1.0 ELSE 0.0 END) AS accuracy,
       COUNT(*) AS n
FROM preds
GROUP BY truth
ORDER BY accuracy ASC
LIMIT 10;
```

Sample 12 random images from one breed for visual inspection:

```sql
SELECT path, img
FROM datasets.oxford_pets_images
WHERE breed = 'shiba_inu'
ORDER BY RANDOM()
LIMIT 12;
```

## Tips

- **Pose, lighting, scale vary widely** — these are casual photos,
  not studio portraits. Heads, half-bodies, and full-body shots are
  all represented per breed. Crop / resize policies that assume a
  centred face will leak signal.
- **Sibling annotations exist but aren't ingested** — the upstream
  `annotations.tar.gz` ships bounding boxes (head ROI) and trimap
  segmentation masks. This recipe is image-only; extend it to a
  multi-archive job if you want them.
- **Cat-vs-dog is solved; breed classification is the actual task**
  — getting the species right is essentially trivial; the
  fine-grained breed split is where models actually differentiate.
  Strong recipes report ~95 % breed accuracy.
- **`basset_hound`, `english_cocker_spaniel`, `wheaten_terrier`** —
  some breed names are multi-word. The filename tokenisation
  preserves underscores; don't strip them when joining to external
  metadata.

## License & attribution

CC-BY-SA-4.0. Commercial use OK with attribution; the ShareAlike
clause applies to redistributed or modified copies of the dataset
itself (models trained on it are generally not considered derivative
works).

- Paper: [Cats and Dogs](https://www.robots.ox.ac.uk/~vgg/publications/2012/parkhi12a/parkhi12a.pdf)
- Site: [Oxford VGG: Pet Dataset](https://www.robots.ox.ac.uk/~vgg/data/pets/)
