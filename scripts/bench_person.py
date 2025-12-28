# scripts/bench_person.py
# -*- coding: utf-8 -*-

import argparse, glob, os, time
from ultralytics import YOLO

def list_images(src: str):
    # src가 폴더면 폴더 안 jpg/png 전부
    if os.path.isdir(src):
        exts = ("*.jpg", "*.jpeg", "*.png")
        files = []
        for e in exts:
            files += glob.glob(os.path.join(src, e))
        return sorted(files)
    # src가 패턴이면 그대로 glob
    return sorted(glob.glob(src))

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True, help="folder or glob pattern")
    ap.add_argument("--weights", required=True)
    ap.add_argument("--device", default="cpu")
    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--warmup", type=int, default=5)
    ap.add_argument("--limit", type=int, default=100, help="max images to benchmark")
    args = ap.parse_args()

    imgs = list_images(args.src)
    if not imgs:
        raise SystemExit(f"No images found: {args.src}")

    imgs = imgs[:args.limit]

    model = YOLO(args.weights)

    # warmup
    w = min(args.warmup, len(imgs))
    for i in range(w):
        _ = model.predict(imgs[i], imgsz=args.imgsz, device=args.device, verbose=False)

    # timed
    t0 = time.perf_counter()
    for p in imgs[w:]:
        _ = model.predict(p, imgsz=args.imgsz, device=args.device, verbose=False)
    t1 = time.perf_counter()

    n = max(1, len(imgs) - w)
    avg_s = (t1 - t0) / n
    avg_ms = avg_s * 1000.0
    fps = 1.0 / avg_s

    print(f"[BENCH] weights={args.weights} device={args.device} imgs={len(imgs)} warmup={w} "
          f"avg_ms={avg_ms:.2f} fps={fps:.2f}")

if __name__ == "__main__":
    main()

