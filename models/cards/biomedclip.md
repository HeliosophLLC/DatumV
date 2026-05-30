# BiomedCLIP

CLIP for biomedical figures, from Microsoft Research. ViT-B/16 vision
tower + PubMedBERT text tower, projected into a shared 512-d embedding
space. Trained on **PMC-15M** — 15 million image-caption pairs scraped
from PubMed Central papers (radiology, histology, microscopy, charts,
gross pathology, illustrations).

The biomedical counterpart to OpenAI CLIP. Same idea — embed images and
text into the same vector space so cosine similarity is meaningful
across modalities — but trained on a domain where general-purpose CLIP
struggles (a CT slice and a stained slide are nothing like ImageNet).

Two SQL-visible models ship together per variant:
`biomedclip_image_embed` for the vision side, `biomedclip_text_embed`
for the text side. Both emit a 512-d L2-normalised `Float32[]` vector
in the same space, so dot product equals cosine similarity.

## When to use which variant

| Variant | Disk    | Best for                                                       |
| ------- | ------- | -------------------------------------------------------------- |
| **fp32**| ~770 MB | **Default**. Reference numerics, retrieval-quality benchmarks. |
| fp16    | ~390 MB | Half the disk + load memory. 1.5-2× speedup on GPUs with native fp16. CPU runtimes that upcast internally see no speedup but still save memory. |

Wire-boundary tensors stay `Float32` in both variants — switching
between them is a one-line change to the `USING` path in the SQL model
body, no input-tensor changes needed.

## Example SQL

Embed an image:

```sql
SELECT models.biomedclip_image_embed(image_decode('/path/to/chest_xray.png')) AS embedding;
```

Embed a caption:

```sql
SELECT models.biomedclip_text_embed('a chest X-ray showing pneumonia') AS embedding;
```

Zero-shot diagnosis over a dataset of X-rays — rank each image against
a fixed list of candidate descriptions, return the top match per image:

```sql
WITH labels AS (
  SELECT 'pneumonia'       AS dx, models.biomedclip_text_embed('a chest X-ray showing pneumonia')      AS emb
  UNION ALL SELECT 'pneumothorax', models.biomedclip_text_embed('a chest X-ray showing pneumothorax')
  UNION ALL SELECT 'effusion',     models.biomedclip_text_embed('a chest X-ray showing pleural effusion')
  UNION ALL SELECT 'healthy',      models.biomedclip_text_embed('a chest X-ray of a healthy patient')
),
images AS (
  SELECT path, models.biomedclip_image_embed(img) AS emb
  FROM datasets.mednist_classes
  LIMIT 100
)
SELECT
  i.path,
  l.dx,
  dot_product(i.emb, l.emb) AS score
FROM images i
CROSS JOIN labels l
QUALIFY rank() OVER (PARTITION BY i.path ORDER BY dot_product(i.emb, l.emb) DESC) = 1
ORDER BY i.path;
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

## Output shape

Both models return `Float32[]` — a length-512 L2-normalised vector.
Because every embedding lies on the unit sphere, `dot_product` and
`cosine_similarity` produce identical scores; `dot_product` is the
faster of the two.

## Tips

- **Preprocessing inherits OpenAI CLIP unchanged** — the image side
  uses CLIP's mean / std, not ImageNet's. The model body handles
  this internally via `image_to_tensor_chw`.
- **Text context is 256 tokens.** The PubMedBERT tokenizer is
  WordPiece (biomedical vocab — `mitochondria` is one token,
  not three). For typical zero-shot labels and short captions you
  won't get close to the limit; long abstracts need truncation.
- **Embed once, compare many.** The expensive call is the model
  invocation; storing embeddings as a `Float32[]` column and
  comparing in SQL is dramatically cheaper than re-embedding per
  query.
- **Not a diagnostic tool.** Zero-shot scores reflect caption-vs-image
  similarity in the training distribution. Clinical decisions need
  validated, regulated systems. The upstream model card calls this
  out explicitly.

## License & attribution

MIT. Original model by Microsoft Research; ONNX export and re-host on
HuggingFace under `Heliosoph/biomedclip-vit-base-patch16-224-onnx`.

- Paper: [BiomedCLIP: a multimodal biomedical foundation model pretrained from fifteen million scientific image-text pairs](https://arxiv.org/abs/2303.00915)
- Upstream: [microsoft/BiomedCLIP-PubMedBERT_256-vit_base_patch16_224](https://huggingface.co/microsoft/BiomedCLIP-PubMedBERT_256-vit_base_patch16_224)
