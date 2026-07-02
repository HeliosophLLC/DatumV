#!/usr/bin/env python3
"""Build the Heliosoph/CORD mirror: per-split image zips + reshaped JSONL.

CORD (naver-clova-ix/cord-v1, CC-BY-4.0) ships as HuggingFace parquet with the
receipt as an Image feature (a {bytes, path} struct) plus a `ground_truth` JSON
string. DatumV's parquet reader doesn't decode struct columns, so we reshape
into the same image-bag + JSONL shape the DocBank recipe already consumes:

    cord-<split>-images.zip   flat zip of receipt images (entry name == image_file)
    cord-<split>.jsonl.gz     one JSON object per receipt:
                                { image_file, width, height, ground_truth }

We read the parquet directly with pyarrow. CORD's embedded images are
full-resolution PNG photos (~2 MB each), so we re-encode to JPEG q90 —
visually lossless for OCR and ~6x smaller. `ground_truth` is CORD's structured
parse (gt_parse: menu lines, subtotals, totals, ...), carried through as a
nested object.

Deps: pyarrow + huggingface_hub + Pillow.

Usage:
    python scripts/export-cord.py [--outdir DIR] [--repo naver-clova-ix/cord-v1]
"""
import argparse
import gzip
import io
import json
import os
import zipfile

import pyarrow.parquet as pq
from huggingface_hub import HfApi, hf_hub_download
from PIL import Image

SPLITS = ("train", "validation", "test")


def find_parquet(api, repo):
    """Return (revision, [parquet paths]). Prefer the main branch; fall back
    to the auto-converted parquet branch."""
    for rev in (None, "refs/convert/parquet"):
        files = api.list_repo_files(repo, repo_type="dataset", revision=rev)
        parquet = [f for f in files if f.endswith(".parquet")]
        if parquet:
            return rev, parquet
    raise SystemExit(f"No parquet files found in {repo}")


def build_split(repo, rev, paths, split, outdir):
    shards = [p for p in paths if split in p.lower()]
    if not shards:
        print(f"{split}: no parquet shard found, skipping")
        return 0

    images_zip = os.path.join(outdir, f"cord-{split}-images.zip")
    jsonl_gz = os.path.join(outdir, f"cord-{split}.jsonl.gz")
    n = 0
    with zipfile.ZipFile(images_zip, "w", zipfile.ZIP_DEFLATED) as zf, \
            gzip.open(jsonl_gz, "wt", encoding="utf-8") as jf:
        for shard in shards:
            local = hf_hub_download(repo, shard, repo_type="dataset", revision=rev)
            table = pq.read_table(local)
            images = table.column("image").to_pylist()
            gts = table.column("ground_truth").to_pylist()
            for image, gt_str in zip(images, gts):
                # CORD ships full-resolution PNG photos (~2 MB each). Re-encode
                # to JPEG q90 — lossless-enough for OCR, ~6x smaller mirror.
                im = Image.open(io.BytesIO(image["bytes"]))
                if im.mode != "RGB":
                    im = im.convert("RGB")
                name = f"cord-{split}-{n:04d}.jpg"
                buf = io.BytesIO()
                im.save(buf, format="JPEG", quality=90, optimize=True)
                zf.writestr(name, buf.getvalue())

                jf.write(json.dumps({
                    "image_file": name,
                    "width": im.width,
                    "height": im.height,
                    "ground_truth": json.loads(gt_str),
                }, ensure_ascii=False) + "\n")
                n += 1

    img_mb = os.path.getsize(images_zip) / 1e6
    ann_mb = os.path.getsize(jsonl_gz) / 1e6
    print(f"{split}: {n} receipts -> "
          f"{os.path.basename(images_zip)} ({img_mb:.1f} MB) + "
          f"{os.path.basename(jsonl_gz)} ({ann_mb:.1f} MB)")
    return n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--outdir", default=os.path.join(
        os.environ.get("TEMP", "/tmp"), "cord-mirror"))
    ap.add_argument("--repo", default="naver-clova-ix/cord-v1")
    args = ap.parse_args()

    os.makedirs(args.outdir, exist_ok=True)
    api = HfApi()
    rev, paths = find_parquet(api, args.repo)
    print(f"Found {len(paths)} parquet file(s) in {args.repo} "
          f"(revision: {rev or 'main'})")

    for split in SPLITS:
        build_split(args.repo, rev, paths, split, args.outdir)

    print(f"\nStaged in: {args.outdir}")
    print("Next: hf upload Heliosoph/CORD <outdir> . --repo-type dataset")


if __name__ == "__main__":
    main()
