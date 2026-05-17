# EuroSAT

27,000 64×64 RGB satellite tiles from the European Space Agency's
Sentinel-2 satellites, sampled across 34 European countries and
labelled with one of ten land-use / land-cover classes. Released in
2017 by Helber et al. — the reach-for small benchmark for
remote-sensing image classification.

Plays a similar role to MNIST in the satellite-imagery space: large
enough to be non-trivial, small enough to iterate on a laptop, and
clean enough that a strong CNN converges in hours rather than days.
Reported accuracies sit around 98–99 % for state-of-the-art models,
but the more interesting use is as a transfer-learning testbed for
ImageNet-pretrained features adapted to overhead imagery.

## Output columns

The single `eurosat_rgb` variant produces an `images` table of 27,000
rows:

```
path:        String      -- entry path inside the source ZIP (e.g. 'EuroSAT_RGB/Forest/Forest_1234.jpg')
img:         Image       -- decoded 64×64 RGB JPEG, sidecar-backed
class:       String      -- land-use class name extracted from the folder
file_bytes:  Int64       -- compressed JPEG payload size
modified:    Timestamp   -- ZIP entry mtime (usually all the same)
```

The class label is the **folder name** (`Forest`, `Highway`, `River`,
…), not an integer index. The original numeric label mapping in the
upstream paper isn't load-bearing — the folder name is the natural
identifier and queries read more clearly with it.

## Label vocabulary

Ten roughly balanced classes (~2,000–3,000 samples each):

```
AnnualCrop             Industrial
Forest                 Pasture
HerbaceousVegetation   PermanentCrop
Highway                Residential
                       River
                       SeaLake
```

## Example SQL

Class distribution — the imbalance, if any, is mild but worth
checking:

```sql
SELECT class, COUNT(*) AS n
FROM datasets.eurosat_rgb
GROUP BY class
ORDER BY n DESC;
```

Run a classifier and look at per-class accuracy:

```sql
WITH preds AS (
  SELECT class AS truth, models.your_landcover_classifier(img) AS pred
  FROM datasets.eurosat_rgb
)
SELECT truth,
       AVG(CASE WHEN truth = pred THEN 1.0 ELSE 0.0 END) AS accuracy,
       COUNT(*) AS n
FROM preds
GROUP BY truth
ORDER BY accuracy ASC;
```

Spot-check 12 random tiles from one class for visual review:

```sql
SELECT path, img
FROM datasets.eurosat_rgb
WHERE class = 'Forest'
ORDER BY RANDOM()
LIMIT 12;
```

## Tips

- **Tiles, not scenes** — each 64×64 patch covers roughly 640 m on a
  side and is sampled from larger Sentinel-2 raster tiles. Don't
  expect global geographic context; each row is a single biome-or-
  texture sample.
- **No georeferencing in this variant** — the original Sentinel-2
  acquisitions carry lat/lon, but the RGB-only release strips them.
  Use the upstream multispectral release (different download) if you
  need coordinates.
- **JPEG, not true Sentinel-2 pixels** — the upstream archive
  rasterises the multispectral 13-band data to 3-channel JPEG.
  Useful for ImageNet-pretrained transfer; not appropriate for any
  pixel-accurate spectral analysis. Reach for the multispectral
  release when band ratios matter (NDVI, NDWI, etc.).
- **Class names match the upstream folder names** — they're
  capitalised and CamelCased; comparison strings should match
  exactly (`'Forest'`, not `'forest'`).

## License & attribution

MIT-licensed by the authors. Sentinel-2 source imagery is provided by
the European Space Agency under the Copernicus open-data terms.
Commercial use OK with attribution.

- Paper: [EuroSAT: A Novel Dataset and Deep Learning Benchmark for Land Use and Land Cover Classification](https://arxiv.org/abs/1709.00029)
- Repo: [phelber/EuroSAT](https://github.com/phelber/EuroSAT)
- Sentinel-2 source: [ESA Copernicus](https://sentinel.esa.int/web/sentinel/missions/sentinel-2)
