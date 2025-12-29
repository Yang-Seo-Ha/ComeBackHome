# scripts/person_to_txt.py
# - COCO 사전학습 YOLO로 person만 검출 -> YOLO txt 저장
# - 포맷: cls cx cy w h conf  (여기서 cls는 0=person 고정)

import argparse
from pathlib import Path
import time  # ✅ 추가

def parse_args():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True, help="image file path OR folder path")
    ap.add_argument("--weights", required=True, help="path to coco .pt (ex: yolov8s.pt)")
    ap.add_argument("--out", required=True, help="output folder to save txts")

    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--conf", type=float, default=0.25)
    ap.add_argument("--iou", type=float, default=0.45)
    ap.add_argument("--device", type=str, default="cpu")  # "cpu" or "0"
    ap.add_argument("--single", action="store_true")
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

    from ultralytics import YOLO
    model = YOLO(str(weights))

    PERSON_CLASS_ID_COCO = 0  # COCO: person=0

    # ✅ 통계용 변수
    infer_ms_list = []
    total_ms_list = []

    for img_path in images:
        t0_total = time.perf_counter()

        # ✅ (선택) 워밍업: 첫 프레임은 느릴 수 있음
        # -> 통계 낼 때 1번째 값은 제외하는 걸 추천(아래에서 처리 가능)

        t0 = time.perf_counter()
        results = model.predict(
            source=str(img_path),
            imgsz=args.imgsz,
            conf=args.conf,
            iou=args.iou,
            device=args.device,
            verbose=False
        )
        t1 = time.perf_counter()

        infer_ms = (t1 - t0) * 1000.0
        infer_ms_list.append(infer_ms)

        r = results[0]
        h, w = r.orig_shape

        txt_path = out_dir / (img_path.stem + ".txt")
        lines = []

        if r.boxes is not None and len(r.boxes) > 0:
            for b in r.boxes:
                cls_id = int(b.cls.item())
                if cls_id != PERSON_CLASS_ID_COCO:
                    continue  # ✅ person만

                conf = float(b.conf.item()) if b.conf is not None else 1.0
                x1, y1, x2, y2 = [float(v) for v in b.xyxy[0].tolist()]

                cx = ((x1 + x2) / 2.0) / w
                cy = ((y1 + y2) / 2.0) / h
                bw = (x2 - x1) / w
                bh = (y2 - y1) / h

                cx, cy, bw, bh = clamp01(cx), clamp01(cy), clamp01(bw), clamp01(bh)

                lines.append(f"0 {cx:.6f} {cy:.6f} {bw:.6f} {bh:.6f} {conf:.6f}")

        txt_path.write_text("\n".join(lines), encoding="utf-8")

        t1_total = time.perf_counter()
        total_ms = (t1_total - t0_total) * 1000.0
        total_ms_list.append(total_ms)

        # ✅ 프레임별 로그(너무 시끄러우면 주석처리)
        print(f"[TIME] {img_path.name}  infer={infer_ms:.1f}ms  total={total_ms:.1f}ms")

    # ✅ 요약 출력 (워밍업 1장 제외 버전)
    if len(infer_ms_list) >= 2:
        infer_eval = infer_ms_list[1:]
        total_eval = total_ms_list[1:]
        print(f"\n[SUMMARY] (warmup 제외, N={len(infer_eval)})")
        print(f"  infer  avg={sum(infer_eval)/len(infer_eval):.1f}ms  min={min(infer_eval):.1f}  max={max(infer_eval):.1f}")
        print(f"  total  avg={sum(total_eval)/len(total_eval):.1f}ms  min={min(total_eval):.1f}  max={max(total_eval):.1f}")
    else:
        print(f"\n[SUMMARY] N={len(infer_ms_list)} (샘플 1장이라 warmup 제외 불가)")
        if infer_ms_list:
            print(f"  infer={infer_ms_list[0]:.1f}ms  total={total_ms_list[0]:.1f}ms")

    print(f"\nDone. saved person txt to: {out_dir}")

if __name__ == "__main__":
    main()
