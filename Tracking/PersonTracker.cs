using OpenCvSharp;
using OpenCvSharp.Tracking;

namespace ComeBackHome.Tracking
{
    public enum TrackerType
    {
        CSRT,
        KCF,
        MIL,   // ✅ MOSSE 대신 넣자(대체용)
    }

    public class PersonTracker
    {
        private Tracker? _tracker;
        private TrackerType _type;

        public bool IsActive => _tracker != null;

        public PersonTracker(TrackerType type)
        {
            _type = type;
            _tracker = CreateTracker(type);
        }

        private Tracker CreateTracker(TrackerType type)
        {
            return type switch
            {
                TrackerType.CSRT => TrackerCSRT.Create(),
                TrackerType.KCF => TrackerKCF.Create(),
                TrackerType.MIL => TrackerMIL.Create(),
                _ => TrackerCSRT.Create()
            };
        }

        // ✅ Rect2d -> Rect 변환 (OpenCvSharp Tracker가 Rect를 기대하는 버전 대응)
        private static Rect ToRect(Rect2d r)
        {
            int x = (int)Math.Round(r.X);
            int y = (int)Math.Round(r.Y);
            int w = (int)Math.Round(r.Width);
            int h = (int)Math.Round(r.Height);

            if (w < 1) w = 1;
            if (h < 1) h = 1;

            return new Rect(x, y, w, h);
        }

        private static Rect2d ToRect2d(Rect r) => new Rect2d(r.X, r.Y, r.Width, r.Height);

        /// <summary>
        /// YOLO bbox로 트래커 초기화
        /// </summary>
        public void Init(Mat frame, Rect2d bbox)
        {
            _tracker?.Dispose();
            _tracker = CreateTracker(_type);

            Rect rect = ToRect(bbox);
            _tracker.Init(frame, rect); // ✅ Rect로 전달
        }

        /// <summary>
        /// 다음 프레임에서 bbox 업데이트
        /// </summary>
        public bool Update(Mat frame, out Rect2d bbox)
        {
            bbox = new Rect2d();
            if (_tracker == null) return false;

            Rect rect = new Rect();              // ✅ Rect로 받고
            bool ok = _tracker.Update(frame, ref rect); // ✅ ref로 전달

            if (!ok) return false;

            bbox = ToRect2d(rect);               // ✅ 밖으로는 Rect2d로 내보내기
            return true;
        }

        public void Reset()
        {
            _tracker?.Dispose();
            _tracker = null;
        }
    }
}
