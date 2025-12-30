using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using OpenCvSharp;           // NuGet: OpenCvSharp4.Windows 필수
using OpenCvSharp.WpfExtensions;
using Point = OpenCvSharp.Point;
using Rect = OpenCvSharp.Rect; // NuGet: OpenCvSharp4.WpfExtensions 필수

namespace ComeBackHome
{
    // C++ DLL과 통신하는 결과 구조체
    [StructLayout(LayoutKind.Sequential)]
    public struct DetectionResult
    {
        public int Label;  // 0: 사람
        public int IsSafe; // 1: 헬멧 착용(안전), 0: 미착용(위험)
        public int X, Y, W, H; // 사람 전체 바운딩 박스 좌표
    }

    // 하이브리드 분석 과정을 보여주는 2분할 디버그 창
    public class DebugWindow : System.Windows.Window
    {
        // 왼쪽: 색상 추출 결과(B/W), 오른쪽: 머리 영역 확대(Color)
        public Image ImgMask = new Image { Stretch = Stretch.Uniform };
        public Image ImgRoi = new Image { Stretch = Stretch.Uniform };

        public DebugWindow()
        {
            this.Title = "SSD + ROI 하이브리드 분석 모니터";
            this.Width = 850; this.Height = 450;
            this.Background = new SolidColorBrush(Color.FromRgb(11, 16, 32));

            Grid g = new Grid { Margin = new Thickness(10) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());

            // C++의 전통적 CV 기법(색상 필터) 시각화
            AddSec(g, ImgMask, 0, "1. ROI 기반 색상 추출 (HSV Mask)");
            // SSD가 찾은 영역 기반의 ROI 시각화
            AddSec(g, ImgRoi, 1, "2. 머리 영역 확대 (ROI Zoom)");

            this.Content = g;
        }

        private void AddSec(Grid g, Image i, int c, string h)
        {
            GroupBox gb = new GroupBox { Header = h, Margin = new Thickness(5), Foreground = Brushes.White, FontWeight = FontWeights.Bold };
            gb.Content = i; Grid.SetColumn(gb, c); g.Children.Add(gb);
        }
    }

    public partial class OpenCvWindow : System.Windows.Window
    {
        private const string DllName = "opencv1.dll";
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern void InitDetector(int mode);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int ProcessSafety(string imagePath, int mode, [Out] DetectionResult[] results);

        private CancellationTokenSource _cts;
        private volatile bool _isPaused = false;
        private DebugWindow _debugWin;

        public OpenCvWindow()
        {
            InitializeComponent();
            this.Closed += (s, e) => { _cts?.Cancel(); _debugWin?.Close(); };
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            int mode = (btn.Name == "BtnSelectA") ? 1 : 2;
            OpenFileDialog dlg = new OpenFileDialog { Filter = "영상 파일|*.mp4;*.avi;*.jpg;*.png" };
            if (dlg.ShowDialog() == true)
            {
                if (_debugWin == null || !_debugWin.IsLoaded) { _debugWin = new DebugWindow { Owner = this }; _debugWin.Show(); }
                TxtSelectedWork.Text = mode == 1 ? "A구역 (고소작업대)" : "B구역 (사다리 작업)";
                InitDetector(mode); _isPaused = false;
                _cts = new CancellationTokenSource();
                await Task.Run(() => StartDetectionLoop(dlg.FileName, mode, _cts.Token));
            }
        }

        private void BtnPlayStop_Click(object sender, RoutedEventArgs e)
        {
            _isPaused = !_isPaused;
            BtnPlayStop.Content = _isPaused ? "▶ 재생" : "⏸ 정지";
        }

        private void StartDetectionLoop(string path, int mode, CancellationToken token)
        {
            using var cap = new VideoCapture(path);
            using var frame = new Mat();
            DetectionResult[] results = new DetectionResult[50];

            // 디버그 시각화용 매트릭스
            using var hsv = new Mat();
            using var mask = new Mat();

            while (!token.IsCancellationRequested)
            {
                if (_isPaused) { Thread.Sleep(100); continue; }
                if (!cap.Read(frame) || frame.Empty()) break;

                Cv2.ImWrite("temp.jpg", frame);
                // C++ DLL 호출 (SSD 기반 사람 탐지 및 헬멧 검증 결과 수신)
                int count = ProcessSafety("temp.jpg", mode, results);

                Mat headRoiImg = new Mat();
                Mat headMaskImg = new Mat();

                // ✅ 메인 화면: SSD가 탐지한 '사람 전체 영역' 표시
                for (int i = 0; i < count; i++)
                {
                    Scalar color = (results[i].IsSafe == 1) ? Scalar.Green : Scalar.Red;
                    Cv2.Rectangle(frame, new OpenCvSharp.Rect(results[i].X, results[i].Y, results[i].W, results[i].H), color, 4);
                    string label = results[i].IsSafe == 1 ? "SAFE" : "WARNING";
                    Cv2.PutText(frame, label, new Point(results[i].X, results[i].Y - 15), HersheyFonts.HersheySimplex, 1.0, color, 2);
                }

                // ✅ 디버그 화면: ROI 추출 및 전통적 CV 기법 시각화
                if (count > 0)
                {
                    var res = results[0];
                    // 1. ROI 계산: 사람 박스의 상단 25%를 머리 영역으로 설정
                    Rect roiRect = new Rect(res.X, res.Y, res.W, (int)(res.H * 0.25));
                    roiRect = roiRect.Intersect(new Rect(0, 0, frame.Cols, frame.Rows)); // 화면 밖 영역 잘라냄

                    if (roiRect.Width > 5 && roiRect.Height > 5)
                    {
                        // 2. 오른쪽 창: 추출된 머리 영역(ROI) 원본 이미지
                        headRoiImg = frame[roiRect].Clone();

                        // 3. 왼쪽 창: ROI 내부의 HSV 색상 마스킹 결과 (전통적 CV 기법)
                        // C++ 코드의 노란색/흰색 탐지 로직을 시각적으로 표현
                        Cv2.CvtColor(headRoiImg, hsv, ColorConversionCodes.BGR2HSV);
                        // 예시: 노란색 계열 범위 마스킹 (C++ 코드 기반)
                        Cv2.InRange(hsv, new Scalar(10, 60, 80), new Scalar(40, 255, 255), mask);
                        headMaskImg = mask.Clone();
                    }
                }

                // UI 업데이트
                Dispatcher.Invoke(() => {
                    ImgCvDisplay.Source = frame.ToWriteableBitmap();
                    // 디버그 창의 두 이미지 업데이트
                    if (_debugWin != null && _debugWin.IsLoaded && !headRoiImg.Empty())
                    {
                        _debugWin.ImgMask.Source = headMaskImg.ToWriteableBitmap();
                        _debugWin.ImgRoi.Source = headRoiImg.ToWriteableBitmap();
                    }
                    UpdateStats(results, count);
                });
                Thread.Sleep(30);
            }
        }

        private void UpdateStats(DetectionResult[] results, int count)
        {
            int p = 0, s = 0, w = 0;
            for (int i = 0; i < count; i++) { p++; if (results[i].IsSafe == 1) s++; else w++; }
            TxtPersonCount.Text = $"{p}명"; TxtSafeCount.Text = s.ToString(); TxtWarnCount.Text = w.ToString();
            if (w > 0) TxtWarnToday.Text = $"{w}건";
        }
    }
}