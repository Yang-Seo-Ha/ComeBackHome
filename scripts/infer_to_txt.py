# scripts/infer_to_txt.py
# - Ultralytics YOLO(.pt)로 이미지(1장 or 폴더)를 추론하고 YOLO txt로 저장
# - 저장 포맷: cls cx cy w h conf   (0~1 정규화)
# - src가 파일이면 1장만, 폴더면 폴더 내 이미지 전체(또는 --single이면 첫 장만)

import argparse
from pathlib import Path

def parse_args():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True, help="image file path OR folder path")
    ap.add_argument("--weights", required=True, help="path to .pt")
    ap.add_argument("--out", required=True, help="output folder to save txts")

    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--conf", type=float, default=0.25)
    ap.add_argument("--iou", type=float, default=0.45)
    ap.add_argument("--device", type=str, default="cpu")  # "cpu" or "0"
    ap.add_argument("--single", action="store_true", help="if src is folder, infer only 1 frame (first)")
    return ap.parse_args()

def clamp01(x: float) -> float:
    return max(0.0, min(1.0, x))

def img_list_from_src(src: Path, single: bool):
    exts = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}
    if src.is_file():
        return [src]
    if src.is_dir():
        imgs = sorted([p for p in src.iterdir() if p.suffix.lower() in exts])
        if single and imgs:
            return [imgs[0]]
        return imgs
    raise FileNotFoundError(f"--src not found: {src}")

def main():
    args = parse_args()

    src = Path(args.src)
    weights = Path(args.weights)
    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)

    if not weights.exists():
        raise FileNotFoundError(f"--weights not found: {weights}")

    images = img_list_from_src(src, args.single)
    if not images:
        print(f"[WARN] no images found in src: {src}")
        return

    # import inside: 에러 메시지 명확히
    try:
        from ultralytics import YOLO
    except Exception as e:
        raise RuntimeError(
            "ultralytics import failed. "
            "해당 파이썬 환경(아나콘다)에 ultralytics 설치됐는지 확인!\n"
            "pip install ultralytics"
        ) from e

    model = YOLO(str(weights))

    # 파일별 추론
    for img_path in images:
        results = model.predict(
            source=str(img_path),
            imgsz=args.imgsz,
            conf=args.conf,
            iou=args.iou,
            device=args.device,
            verbose=False
        )

        r = results[0]  # 1장

        # 원본 이미지 크기 (H,W)
        h, w = r.orig_shape

        txt_path = out_dir / (img_path.stem + ".txt")
        lines = []

        if r.boxes is not None and len(r.boxes) > 0:
            for b in r.boxes:
                cls_id = int(b.cls.item())
                conf = float(b.conf.item()) if b.conf is not None else 1.0

                x1, y1, x2, y2 = [float(v) for v in b.xyxy[0].tolist()]

                # 픽셀 -> 정규화 YOLO(center x,y,w,h)
                cx = ((x1 + x2) / 2.0) / w
                cy = ((y1 + y2) / 2.0) / h
                bw = (x2 - x1) / w
                bh = (y2 - y1) / h

                cx = clamp01(cx)
                cy = clamp01(cy)
                bw = clamp01(bw)
                bh = clamp01(bh)

                lines.append(f"{cls_id} {cx:.6f} {cy:.6f} {bw:.6f} {bh:.6f} {conf:.6f}")

        txt_path.write_text("\n".join(lines), encoding="utf-8")

    print(f"Done. saved txt to: {out_dir}")

if __name__ == "__main__":
    main()
