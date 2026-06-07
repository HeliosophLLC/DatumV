# MedNIST

58,954 64×64 grayscale medical thumbnails labeled with one of six
imaging modalities (AbdomenCT, BreastMRI, ChestCT, CXR, Hand, HeadCT).
Compiled by Bradley J. Erickson (Mayo Clinic) from CT, MRI, and X-ray
slices drawn from the Cancer Imaging Archive (TCIA), the RSNA Bone
Age Challenge, and the NIH ChestX-ray14 release, then packaged by the
MONAI team as a small drop-in benchmark for medical image
classification.

The medical-imaging counterpart to MNIST: same shape (64×64 grayscale,
class-labelled, six-figure row count), same role (validate the image
pipeline end-to-end, smoke-test a classifier in under a minute), but
the class boundaries actually correspond to clinical modality rather
than penmanship. Strong CNNs converge to ~99 % test accuracy in a few
epochs — easy enough that a sub-1 % error rate is the right
expectation, hard enough to surface broken preprocessing.

## Label vocabulary

Six modality classes, roughly balanced (~10,000 thumbnails each;
BreastMRI is slightly smaller at 8,954):

```
AbdomenCT   — axial abdominal CT slices
BreastMRI   — axial breast MRI slices
ChestCT     — axial chest CT slices
CXR         — frontal chest X-ray (radiograph)
Hand        — frontal hand radiograph (bone-age corpus)
HeadCT      — axial head CT slices
```

The class label is the **folder name** verbatim
(`MedNIST/AbdomenCT/000123.jpeg` → `AbdomenCT`), not an integer index.

## Example SQL

Sanity-check that the install landed and look at a single row:

```sql
SELECT path, class, img
FROM datasets.mednist_classes
LIMIT 1;
```

Class distribution — should be ~10k per modality with BreastMRI
slightly under:

```sql
SELECT class, COUNT(*) AS n
FROM datasets.mednist_classes
GROUP BY class
ORDER BY n DESC;
```

Spot-check 12 random thumbnails from one class for visual review:

```sql
SELECT path, img
FROM datasets.mednist_classes
WHERE class = 'ChestCT'
ORDER BY RANDOM()
LIMIT 12;
```

Cross-modal retrieval — given a free-text description, surface the
MedNIST thumbnails that look most like it. Top hits should fall in the
modality class the query describes:

```sql
WITH query AS (
  SELECT models.biomedclip_text_embed('a frontal radiograph of a human hand') AS emb
),
slides AS (
  SELECT img
  FROM datasets.mednist_classes
  ORDER BY random()
  LIMIT 100
)
SELECT s.img, dot_product(models.biomedclip_image_embed(img), q.emb) AS similarity
FROM slides s, query q
ORDER BY similarity DESC
```

## Output schema

The single `mednist_classes` variant produces an `images` table of
58,954 rows:

```
path:        String      -- entry path inside the tar.gz (e.g. 'MedNIST/AbdomenCT/000123.jpeg')
img:         Image       -- decoded 64×64 grayscale JPEG, sidecar-backed
class:       String      -- modality class name extracted from the folder
file_bytes:  Int64       -- compressed JPEG payload size
modified:    Timestamp   -- tar entry mtime
```

## Tips

- **Grayscale, not RGB** — `image_decode` returns a single-channel
  `Image`. Models that expect three channels need to broadcast the
  channel dimension before inference; check your classifier's input
  signature.
- **No train / test split in the upstream tarball** — every published
  MedNIST recipe carves a split client-side (commonly 80 / 10 / 10 by
  filename hash). A `WHERE HASH(path) % 10 < 8` clause gets you a
  deterministic train cut without an extra ingest.
- **Not a diagnostic corpus** — modality classification is a sanity
  task, not a clinical one. Don't read accuracy on MedNIST as evidence
  about real radiology workflows.
- **Class names match the folder names** — case-sensitive
  (`'AbdomenCT'`, `'CXR'`, `'BreastMRI'`); comparison strings must
  match exactly.

## License & attribution

CC-BY-SA-4.0. Share-alike — derivative datasets and derived models
distributed downstream need to carry the same license. Commercial use
is permitted with attribution.

- Source release: [MONAI-extra-test-data 0.8.1](https://github.com/Project-MONAI/MONAI-extra-test-data/releases/tag/0.8.1)
- MONAI tutorials (canonical usage): [Project-MONAI/tutorials](https://github.com/Project-MONAI/tutorials)
- Source corpora: [TCIA](https://www.cancerimagingarchive.net/), [RSNA Bone Age Challenge](https://www.rsna.org/education/ai-resources-and-training/ai-image-challenge/RSNA-Pediatric-Bone-Age-Challenge-2017), [NIH ChestX-ray14](https://www.nih.gov/news-events/news-releases/nih-clinical-center-provides-one-largest-publicly-available-chest-x-ray-datasets-scientific-community)
