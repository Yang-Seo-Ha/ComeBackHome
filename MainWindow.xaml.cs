using ComeBackHome.Tracking;
using Microsoft.Win32;
using Newtonsoft.Json;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ComeBackHome
{
    public class LocalConfig
    {
        public string DatasetRoot { get; set; } = "";
        public string DefaultSequence { get; set; } = "";
        public int Fps { get; set; } = 30;

        public string PythonExe { get; set; } = "";

        // ---- UC ----
        public string InferScript { get; set; } = @"scripts\infer_to_txt.py";
        public string ModelPath { get; set; } = @"assets\models\ladder_uc78_v1.pt";
        public string PredFolder { get; set; } = "pred_uc";
        public bool AutoInferEnabled { get; set; } = true;
        public int InferIntervalMs { get; set; } = 1000;
        public bool InferSingleFrameOnly { get; set; } = true;

        // ---- PERSON ----
        public string PersonInferScript { get; set; } = @"scripts\person_to_txt.py";
        public string PersonModelPath { get; set; } = @"assets\models\yolov8s.pt";
        public string PersonPredFolder { get; set; } = "pred_person";
        public bool AutoPersonEnabled { get; set; } = true;
        public int PersonIntervalMs { get; set; } = 1200;
        public bool PersonSingleFrameOnly { get; set; } = true;

        // ---- PPE ----
        public string PpeInferScript { get; set; } = @"scripts\infer_to_txt.py";
        public string PpeModelPath { get; set; } = @"assets\models\ppe_v2.pt";
        public string PpePredFolder { get; set; } = "pred_ppe";
        public bool AutoPpeEnabled { get; set; } = true;
        public int PpeIntervalMs { get; set; } = 1400;
        public bool PpeSingleFrameOnly { get; set; } = true;

        // ---- HIGH ----
        public string HighInferScript { get; set; } = @"scripts\infer_to_txt.py";
        public string HighModelPath { get; set; } = @"assets\models\high_v1.pt";
        public string HighPredFolder { get; set; } = "pred_high";
        public bool AutoHighEnabled { get; set; } = true;
        public int HighIntervalMs { get; set; } = 1000;
        public bool HighSingleFrameOnly { get; set; } = true;

        // ---- common infer params ----
        public int ImgSz { get; set; } = 640;
        public double Conf { get; set; } = 0.25;
        public double Iou { get; set; } = 0.45;
        public string Device { get; set; } = "cpu";

        // ---- tracking ----
        public bool TrackingEnabled { get; set; } = true;
        public int TrackingReinitEveryNFrames { get; set; } = 15;
        public int TrackingFailResetAfterNFrames { get; set; } = 10;
    }

    // ✅ OpenCvSharp.Window 충돌 방지
    public partial class MainWindow : System.Windows.Window
    {
        private LocalConfig _cfg = new LocalConfig();

        private readonly DispatcherTimer _playTimer = new DispatcherTimer();
        private readonly DispatcherTimer _ucTimer = new DispatcherTimer();
        private readonly DispatcherTimer _personTimer = new DispatcherTimer();
        private readonly DispatcherTimer _ppeTimer = new DispatcherTimer();
        private readonly DispatcherTimer _highTimer = new DispatcherTimer();
        private DispatcherTimer? _clockTimer;

        private PersonTracker _personTracker = new PersonTracker(TrackerType.CSRT);

        private List<string> _frames = new List<string>();
        private int _frameIndex = 0;
        private bool _isPlaying = false;

        private int _imgW = 0;
        private int _imgH = 0;

        private bool _ucRunning = false;
        private bool _personRunning = false;
        private bool _ppeRunning = false;
        private bool _highRunning = false;

        private string _projectRoot = "";

        // tracking state
        private Rect2d _lastTrackBox = default;
        private bool _hasLastTrackBox = false;
        private int _framesSinceYolo = 0;
        private int _framesSinceTrackerOk = 0;

        private const int EXPECTED_PEOPLE = 2;
        private int _warnToday = 0;

        // =========================
        // 리포트(CSV)용 세션/로그
        // =========================
        private DateTime? _workStartAt = null;   // 작업 시작 시간(모니터링 시작)
        private DateTime? _workEndAt = null;     // 작업 종료 시간(모니터링 정지)

        private int _maxPeopleStable = 0;        // 작업 중 최대 인원(peopleStable 기준)
        private int _maxHelmetCount = 0;         // 작업 중 최대 헬멧 탐지 수
        private int _maxHarnessCount = 0;        // 작업 중 최대 하네스 탐지 수

        private int _totalAbnormalEvents = 0;    // 비정상 이벤트 누적(리포트용)

        // 이벤트 로그: "언제 / 무엇 / 상세" 저장
        private readonly List<ReportEvent> _events = new List<ReportEvent>();

        private class ReportEvent
        {
            public DateTime Time { get; set; }
            public string Type { get; set; } = "";   // ex) PeopleMismatch, HelmetMissing, HarnessMissing, UnsafeInstall
            public string Detail { get; set; } = ""; // ex) "people=1 expected=2"
        }

        // =========================
        // 안정화(hold)용 상태
        // =========================
        private static readonly TimeSpan HOLD_PEOPLE = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan HOLD_PPE = TimeSpan.FromSeconds(6);   // 헬멧/하네스 미탐지 유지시간(원하면 5~7초로 조절)
        private static readonly TimeSpan HOLD_UNSAFE = TimeSpan.FromSeconds(5); // 미설치물 홀드(5초)

        private DateTime _lastSeenUnsafe = DateTime.MinValue;
        private string _lastUnsafeText = "-";

        // people
        private DateTime _lastSeenPeople = DateTime.MinValue;
        private int _lastPeopleCount = 0;

        // helmet
        private DateTime _lastSeenHelmetOk = DateTime.MinValue;

        // harness
        private DateTime _lastSeenHarnessOk = DateTime.MinValue;

        // ✅ A/B 폴더(좌측 선택으로 지정)
        private string? _seqPathA = null;
        private string? _seqPathB = null;

        // ✅ 현재 선택된 시퀀스(기존 CmbChannel.SelectedValue 역할)
        private string? _currentSeqPath = null;

        private readonly Dictionary<int, string> _ucClassMap = new Dictionary<int, string>
        {
            { 0, "UC-07" },
            { 1, "UC-08" },
        };

        private readonly Dictionary<int, string> _ppeClassMap = new Dictionary<int, string>
        {
            { 0, "Helmet" },
            { 1, "Safety_Vest" },
            { 2, "Safety_goggles" },
            { 3, "Safety_shoes" },
            { 4, "No_helmet" },
            { 5, "No_Vest" },
            { 6, "No_goggles" },
            { 7, "No_SafetyShoes" },
            { 8, "Person" },
            { 10, "Safety_Harness" },
        };

        private readonly Dictionary<int, string> _highClassMap = new Dictionary<int, string>
        {
            { 0, "SO-07" },
            { 1, "SO-05" },
            { 2, "UC-09" },
            { 3, "UC-10" },
            { 4, "UC-11" },
            { 5, "UC-12" },
            { 6, "WO-08" },
            { 7, "WO-01" },
        };

        private readonly HashSet<string> _unsafeInstallCodes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SO-07","SO-05",
                "UC-09","UC-10","UC-11","UC-12",
                "UC-07","UC-08"
            };

        private readonly Dictionary<string, string> _unsafeDisplayName =
             new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SO-07"] = "SO-07 안전난간 미설치",
        ["SO-05"] = "SO-05 과상승방지봉 미설치",

        ["UC-09"] = "UC-09 고임목 미설치",
        ["UC-10"] = "UC-10 과상승방지봉 미설치",
        ["UC-11"] = "UC-11 렌탈차량 통행로 미확보",
        ["UC-12"] = "UC-12 전도방지대 미설치",

        ["UC-07"] = "UC-07 적재물 위에 사다리 설치",
        ["UC-08"] = "UC-08 전도방지대 미설치",

        ["WO-08"] = "WO-08 고소작업대"
    };
        private readonly Dictionary<string, Brush> _clsBrush =
            new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase)
            {
                ["UC-07"] = Brushes.DeepSkyBlue,
                ["UC-08"] = Brushes.Lime,
                ["person"] = Brushes.Gold,
                ["person_track"] = Brushes.Aqua,

                ["Helmet"] = Brushes.Orange,
                ["Safety_Vest"] = Brushes.MediumPurple,
                ["Safety_goggles"] = Brushes.LightSkyBlue,
                ["Safety_shoes"] = Brushes.LightGreen,
                ["Safety_Harness"] = Brushes.HotPink,

                ["No_helmet"] = Brushes.Red,
                ["No_Vest"] = Brushes.Red,
                ["No_goggles"] = Brushes.Red,
                ["No_SafetyShoes"] = Brushes.Red,

                ["SO-07"] = Brushes.OrangeRed,
                ["SO-05"] = Brushes.OrangeRed,
                ["UC-09"] = Brushes.OrangeRed,
                ["UC-10"] = Brushes.OrangeRed,
                ["UC-11"] = Brushes.OrangeRed,
                ["UC-12"] = Brushes.OrangeRed,

                ["WO-08"] = Brushes.Cyan,
                ["WO-01"] = Brushes.Gold,
            };

        public class Detection
        {
            public string Cls { get; set; } = "unknown";
            public double Conf { get; set; } = 1.0;
            public double X1 { get; set; } = 0;
            public double Y1 { get; set; } = 0;
            public double X2 { get; set; } = 0;
            public double Y2 { get; set; } = 0;
        }

        public MainWindow()
        {
            InitializeComponent();

            TxtCctvStatus.Text = "Booting...";
            TxtHeaderTime.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            TxtSelectedWork.Text = "-";
            TxtWarnToday.Text = "0건";
            TxtWarnLevel.Text = "LIVE";
            TxtLastDetectTime.Text = "마지막 감지: -";

            TxtPeopleSummary.Text = $"0 / {EXPECTED_PEOPLE}";
            TxtHelmetSummary.Text = "(미착용)";
            TxtHarnessSummary.Text = "(미착용)";
            TxtUcSummary.Text = "-";
            TxtRiskSummary.Text = "정상";
            TxtRiskDetail.Text = "(비정상 0건)";
            TxtAlertLog.Text = "(로그 없음)";
            TxtBottomLog.Text = "(로그 없음)";

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (_, __) => TxtHeaderTime.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            _clockTimer.Start();

            _projectRoot = FindProjectRoot(AppDomain.CurrentDomain.BaseDirectory);

            LoadLocalConfig();

            // ✅ 트래킹 체크박스 -> cfg 반영
            if (ChkTracking != null)
            {
                ChkTracking.Checked += (_, __) => _cfg.TrackingEnabled = true;
                ChkTracking.Unchecked += (_, __) => _cfg.TrackingEnabled = false;
                ChkTracking.IsChecked = _cfg.TrackingEnabled;
            }

            if (CmbTrackerType != null && CmbTrackerType.Items.Count > 0)
                CmbTrackerType.SelectedIndex = 0;

            int fps = Math.Max(1, _cfg.Fps);
            _playTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
            _playTimer.Tick += (_, __) => ShowNextFrame();

            _ucTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(200, _cfg.InferIntervalMs));
            _ucTimer.Tick += async (_, __) =>
            {
                if (!_cfg.AutoInferEnabled) return;
                await RunUcInferForCurrentFrameAsync();
            };

            _personTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(300, _cfg.PersonIntervalMs));
            _personTimer.Tick += async (_, __) =>
            {
                if (!_cfg.AutoPersonEnabled) return;
                await RunPersonInferForCurrentFrameAsync();
            };

            _ppeTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(300, _cfg.PpeIntervalMs));
            _ppeTimer.Tick += async (_, __) =>
            {
                if (!_cfg.AutoPpeEnabled) return;
                await RunPpeInferForCurrentFrameAsync();
            };

            _highTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(300, _cfg.HighIntervalMs));
            _highTimer.Tick += async (_, __) =>
            {
                if (!_cfg.AutoHighEnabled) return;
                await RunHighInferForCurrentFrameAsync();
            };

            if (_cfg.AutoInferEnabled) _ucTimer.Start();
            if (_cfg.AutoPersonEnabled) _personTimer.Start();
            if (_cfg.AutoPpeEnabled) _ppeTimer.Start();
            if (_cfg.AutoHighEnabled) _highTimer.Start();

            Closed += (_, __) =>
            {
                try { _personTracker.Reset(); } catch { }
                try { _playTimer.Stop(); } catch { }
                try { _ucTimer.Stop(); } catch { }
                try { _personTimer.Stop(); } catch { }
                try { _ppeTimer.Stop(); } catch { }
                try { _highTimer.Stop(); } catch { }
                try { _clockTimer?.Stop(); } catch { }
            };

            // ✅ 선택 전에는 재생할 프레임이 없을 수 있음
            TxtCctvStatus.Text = "좌측에서 A/B 구역을 선택해 시퀀스 폴더를 지정하세요.";
        }

        // =========================
        // ✅ Left: A/B 폴더 선택(채널 콤보 제거)
        // =========================
        private void BtnSelectA_Click(object sender, RoutedEventArgs e)
        {
            var picked = PickSequenceFolder(_cfg.DatasetRoot);
            if (string.IsNullOrWhiteSpace(picked)) return;

            _seqPathA = picked;
            _currentSeqPath = _seqPathA;

            TxtSelectedWork.Text = "A구역 · 고소작업대 작업";
            PrepareFramesFromPath(_currentSeqPath);
        }

        private void BtnSelectB_Click(object sender, RoutedEventArgs e)
        {
            var picked = PickSequenceFolder(_cfg.DatasetRoot);
            if (string.IsNullOrWhiteSpace(picked)) return;

            _seqPathB = picked;
            _currentSeqPath = _seqPathB;

            TxtSelectedWork.Text = "B구역 · 사다리 작업";
            PrepareFramesFromPath(_currentSeqPath);
        }

        // ✅ WinForms 제거: 이미지 1개 선택 → 그 폴더를 시퀀스로 사용
        private string? PickSequenceFolder(string initialRoot)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "시퀀스 폴더에서 이미지 1장을 선택하세요",
                    Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp",
                    CheckFileExists = true,
                    Multiselect = false,
                    InitialDirectory = Directory.Exists(initialRoot)
                        ? initialRoot
                        : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                };

                if (dlg.ShowDialog() == true)
                {
                    return Path.GetDirectoryName(dlg.FileName);
                }
                return null;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"폴더 선택 실패: {ex.Message}");
                return null;
            }
        }

        // ✅ 콤보박스 대신, 경로로 프레임 로드
        private void PrepareFramesFromPath(string? seqPath)
        {
            _playTimer.Stop();

            // hold 상태 리셋(채널 전환 시)
            _lastSeenPeople = DateTime.MinValue;
            _lastPeopleCount = 0;
            _lastSeenHelmetOk = DateTime.MinValue;
            _lastSeenHarnessOk = DateTime.MinValue;
            _lastSeenUnsafe = DateTime.MinValue;
            _lastUnsafeText = "-";

            _isPlaying = false;
            _frameIndex = 0;

            if (_cfg.TrackingEnabled)
            {
                try { _personTracker.Reset(); } catch { }
                _hasLastTrackBox = false;
                _lastTrackBox = default;
                _framesSinceYolo = 0;
                _framesSinceTrackerOk = 0;
            }

            if (string.IsNullOrWhiteSpace(seqPath) || !Directory.Exists(seqPath))
            {
                TxtCctvStatus.Text = "No sequence selected.";
                return;
            }

            _frames = Directory.GetFiles(seqPath)
                .Where(p => p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p)
                .ToList();

            if (_frames.Count == 0)
            {
                TxtCctvStatus.Text = $"No frames found:\n{seqPath}";
                ImgCctv.Source = null;
                OverlayCanvas.Children.Clear();
                return;
            }

            TxtCctvStatus.Text = $"Channel: {Path.GetFileName(seqPath)} / Frames: {_frames.Count}";
            ShowFrame(0);
        }

        // ✅ 콤보박스 변경 시 트래커 교체
        private void CmbTrackerType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbTrackerType == null) return;

            TrackerType t = TrackerType.CSRT;
            if (CmbTrackerType.SelectedIndex == 1) t = TrackerType.KCF;
            else if (CmbTrackerType.SelectedIndex == 2) t = TrackerType.MIL;
            else t = TrackerType.CSRT;

            SwitchTracker(t, note: "UI Combo");
        }

        private void SwitchTracker(TrackerType t, string note = "")
        {
            try { _personTracker.Reset(); } catch { }
            _personTracker = new PersonTracker(t);

            _hasLastTrackBox = false;
            _lastTrackBox = default;
            _framesSinceYolo = 0;
            _framesSinceTrackerOk = 0;

            TxtCctvStatus.Text = string.IsNullOrWhiteSpace(note)
                ? $"Tracker switched: {t}"
                : $"Tracker switched: {t} ({note})";
        }

        private void LoadLocalConfig()
        {
            try
            {
                string cfgPath1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "configs", "local.user.json");
                string cfgPath2 = Path.Combine(_projectRoot, "configs", "local.user.json");
                string cfgPath = File.Exists(cfgPath1) ? cfgPath1 : cfgPath2;

                if (!File.Exists(cfgPath))
                {
                    TxtCctvStatus.Text = $"❌ Config missing\n{cfgPath1}\nOR\n{cfgPath2}";
                    return;
                }

                string json = File.ReadAllText(cfgPath, Encoding.UTF8);
                var loaded = JsonConvert.DeserializeObject<LocalConfig>(json);
                if (loaded != null) _cfg = loaded;

                if (string.IsNullOrWhiteSpace(_cfg.DatasetRoot))
                {
                    TxtCctvStatus.Text = $"❌ DatasetRoot empty\nConfig:\n{cfgPath}";
                    return;
                }
                if (!Directory.Exists(_cfg.DatasetRoot))
                {
                    TxtCctvStatus.Text = $"❌ Invalid DatasetRoot\n{_cfg.DatasetRoot}";
                    return;
                }

                if (string.IsNullOrWhiteSpace(_cfg.PredFolder)) _cfg.PredFolder = "pred_uc";
                if (string.IsNullOrWhiteSpace(_cfg.PersonPredFolder)) _cfg.PersonPredFolder = "pred_person";
                if (string.IsNullOrWhiteSpace(_cfg.PpePredFolder)) _cfg.PpePredFolder = "pred_ppe";
                if (string.IsNullOrWhiteSpace(_cfg.HighPredFolder)) _cfg.HighPredFolder = "pred_high";

                TxtCctvStatus.Text =
                    $"✅ Config OK\nRoot:\n{_cfg.DatasetRoot}\nFPS: {_cfg.Fps}";
            }
            catch (Exception ex)
            {
                TxtCctvStatus.Text = $"❌ Config load failed: {ex.Message}";
            }
        }

        private void BtnPlayStop_Click(object sender, RoutedEventArgs e)
        {
            if (_frames.Count == 0)
            {
                TxtCctvStatus.Text = "좌측에서 A/B 구역을 선택해 시퀀스를 지정하세요.";
                return;
            }

            if (_isPlaying)
            {
                _playTimer.Stop();
                _isPlaying = false;

                // ✅ [여기 추가] 재생 정지 시 '작업 종료 시간' 기록 + 리포트 버튼 활성
                _workEndAt = DateTime.Now;
                BtnReport.IsEnabled = true;

                TxtCctvStatus.Text = "Paused";
            }
            else
            {
                int fps = Math.Max(1, _cfg.Fps);
                _playTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
                _playTimer.Start();
                _isPlaying = true;

                // ✅ [여기 추가] 재생 시작 시 '작업 시작 시간' 기록 + 리포트 초기화 (처음 1회만)
                if (_workStartAt == null)
                {
                    _workStartAt = DateTime.Now;
                    _workEndAt = null;

                    _events.Clear();
                    _totalAbnormalEvents = 0;

                    _maxPeopleStable = 0;
                    _maxHelmetCount = 0;
                    _maxHarnessCount = 0;

                    // 진행 중엔 리포트 저장 막기(원하면 안 막아도 됨)
                    BtnReport.IsEnabled = false;
                }

                TxtCctvStatus.Text = "Playing...";
            }

        }

        private void ShowNextFrame()
        {
            if (_frames.Count == 0) return;
            _frameIndex++;
            if (_frameIndex >= _frames.Count) _frameIndex = 0;
            ShowFrame(_frameIndex);
        }

        private void ShowFrame(int idx)
        {
            if (idx < 0 || idx >= _frames.Count) return;

            string imgPath = _frames[idx];

            BitmapImage bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(imgPath);
            bmp.EndInit();
            bmp.Freeze();

            ImgCctv.Source = bmp;
            _imgW = bmp.PixelWidth;
            _imgH = bmp.PixelHeight;

            string? seqPath = _currentSeqPath;

            // UC
            string predUcTxt = GetPredTxtPath(seqPath, imgPath, _cfg.PredFolder);
            List<Detection> ucDets = File.Exists(predUcTxt)
                ? LoadYoloLabelsFromTxt(predUcTxt, _imgW, _imgH, "uc")
                : LoadYoloLabelsForImageSameFolder(imgPath, _imgW, _imgH, "uc");

            // PERSON
            string predPersonTxt = GetPredTxtPath(seqPath, imgPath, _cfg.PersonPredFolder);
            List<Detection> personDets = File.Exists(predPersonTxt)
                ? LoadYoloLabelsFromTxt(predPersonTxt, _imgW, _imgH, "person")
                : new List<Detection>();

            // PPE
            string predPpeTxt = GetPredTxtPath(seqPath, imgPath, _cfg.PpePredFolder);
            List<Detection> ppeDets = File.Exists(predPpeTxt)
                ? LoadYoloLabelsFromTxt(predPpeTxt, _imgW, _imgH, "ppe")
                : new List<Detection>();

            // HIGH
            string predHighTxt = GetPredTxtPath(seqPath, imgPath, _cfg.HighPredFolder);
            List<Detection> highDets = File.Exists(predHighTxt)
                ? LoadYoloLabelsFromTxt(predHighTxt, _imgW, _imgH, "high")
                : new List<Detection>();

            // tracking
            Detection? trackDet = null;
            if (_cfg.TrackingEnabled)
            {
                using Mat frame = SafeImRead(imgPath);
                trackDet = UpdateTracking(idx, frame, personDets);
            }

            var all = new List<Detection>();
            all.AddRange(ucDets);
            all.AddRange(personDets);
            all.AddRange(ppeDets);
            all.AddRange(highDets);
            if (trackDet != null) all.Add(trackDet);

            DrawDetections(all);
            UpdateRightSummary(personDets, ppeDets, ucDets, highDets);

            TxtLastDetectTime.Text = $"마지막 감지: {DateTime.Now:HH:mm:ss}";

            string trkNote = _cfg.TrackingEnabled
                ? $"TRK:{(_personTracker.IsActive ? "on" : "off")} yoloGap:{_framesSinceYolo} fail:{_framesSinceTrackerOk}"
                : "TRK:off";

            TxtCctvStatus.Text =
                $"{Path.GetFileName(imgPath)} ({idx + 1}/{_frames.Count}) " +
                $"UC:{ucDets.Count} PERSON:{personDets.Count} PPE:{ppeDets.Count} HIGH:{highDets.Count} {trkNote}";
        }

        // ✅ 누적 경고 과다 증가 방지용(정상→비정상 진입 시 1회만 카운트)
        private bool _abnormalActive = false;
        private DateTime _abnormalSince = DateTime.MinValue;
        // =========================
        // ✅ 유형별 경고 쿨다운
        // =========================
        private readonly Dictionary<string, TimeSpan> _warnCooldown =
            new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase)
            {
                ["PeopleMismatch"] = TimeSpan.FromSeconds(10),
                ["HelmetMissing"] = TimeSpan.FromSeconds(20),
                ["HarnessMissing"] = TimeSpan.FromSeconds(20),
                ["UnsafeInstall"] = TimeSpan.FromSeconds(30),
            };

        private readonly Dictionary<string, DateTime> _lastWarnAtByType =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private bool CanCountWarn(string type, DateTime now)
        {
            if (!_warnCooldown.TryGetValue(type, out var cd))
                cd = TimeSpan.FromSeconds(10); // 기본값

            if (!_lastWarnAtByType.TryGetValue(type, out var last))
            {
                _lastWarnAtByType[type] = now;
                return true;
            }

            if (now - last >= cd)
            {
                _lastWarnAtByType[type] = now;
                return true;
            }

            return false;
        }

        private void UpdateRightSummary(List<Detection> personDets, List<Detection> ppeDets, List<Detection> ucDets, List<Detection> highDets)
        {
            var now = DateTime.Now;

            // 1) people raw
            int peopleRaw = personDets.Count;

            // 2) people hold(3초 유지)
            if (peopleRaw > 0)
            {
                _lastSeenPeople = now;
                _lastPeopleCount = peopleRaw;
            }

            int peopleStable = (now - _lastSeenPeople <= HOLD_PEOPLE) ? _lastPeopleCount : 0;

            // helmet/harness count
            int helmetCount = ppeDets.Count(d => d.Cls.Equals("Helmet", StringComparison.OrdinalIgnoreCase));
            int harnessCount = ppeDets.Count(d => d.Cls.Equals("Safety_Harness", StringComparison.OrdinalIgnoreCase));

            // 작업 중 통계 업데이트
            if (_workStartAt != null && _workEndAt == null)
            {
                if (peopleStable > _maxPeopleStable) _maxPeopleStable = peopleStable;
                if (helmetCount > _maxHelmetCount) _maxHelmetCount = helmetCount;
                if (harnessCount > _maxHarnessCount) _maxHarnessCount = harnessCount;
            }

            TxtPeopleSummary.Text = $"{peopleStable} / {EXPECTED_PEOPLE}";

            // 3) Helmet 판정
            if (peopleStable == 0)
            {
                TxtHelmetSummary.Text = "-";
            }
            else
            {
                if (helmetCount >= peopleStable) _lastSeenHelmetOk = now;
                bool helmetOk = (now - _lastSeenHelmetOk <= HOLD_PPE);
                TxtHelmetSummary.Text = helmetOk ? "(착용)" : "(미착용)";
            }

            // 4) Harness 판정
            if (peopleStable == 0)
            {
                TxtHarnessSummary.Text = "-";
            }
            else
            {
                if (harnessCount >= peopleStable) _lastSeenHarnessOk = now;
                bool harnessOk = (now - _lastSeenHarnessOk <= HOLD_PPE);
                TxtHarnessSummary.Text = harnessOk ? "(착용)" : "(미착용)";
            }

            // 5) Unsafe install
            var unsafeHits = ucDets.Concat(highDets)
                .Select(d => d.Cls)
                .Where(cls => _unsafeInstallCodes.Contains(cls))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToList();

            if (unsafeHits.Count > 0)
            {
                _lastSeenUnsafe = now;
                _lastUnsafeText = string.Join(", ",
                    unsafeHits.Select(code =>
                        _unsafeDisplayName.TryGetValue(code, out var name) ? name : code)
                );
            }

            bool unsafeActive = (now - _lastSeenUnsafe <= HOLD_UNSAFE);
            TxtUcSummary.Text = unsafeActive ? _lastUnsafeText : "-";

            // =========================
            // ✅ 비정상 유형 계산
            // =========================
            bool peopleAbn = (peopleStable < EXPECTED_PEOPLE); // 2명 미만만 비정상, 2명 이상은 정상
            bool helmetAbn = (peopleStable > 0 && TxtHelmetSummary.Text.Contains("미착용"));
            bool harnessAbn = (peopleStable > 0 && TxtHarnessSummary.Text.Contains("미착용"));
            bool unsafeAbn = (TxtUcSummary.Text != "-");

            int abnormalCount = 0;
            if (peopleAbn) abnormalCount++;
            if (helmetAbn) abnormalCount++;
            if (harnessAbn) abnormalCount++;
            if (unsafeAbn) abnormalCount++;

            // 위험도 UI
            if (abnormalCount == 0)
            {
                TxtRiskSummary.Text = "정상";
                TxtRiskDetail.Text = "(비정상 0건)";
            }
            else if (abnormalCount == 1)
            {
                TxtRiskSummary.Text = "주의";
                TxtRiskDetail.Text = $"(비정상 {abnormalCount}건)";
            }
            else
            {
                TxtRiskSummary.Text = "위험";
                TxtRiskDetail.Text = $"(비정상 {abnormalCount}건)";
            }

            // =========================
            // ✅ (핵심) 누적 경고는 "유형별 쿨다운" 통과 시만 +1
            // =========================
            int added = 0;

            if (peopleAbn && CanCountWarn("PeopleMismatch", now)) added++;
            if (helmetAbn && CanCountWarn("HelmetMissing", now)) added++;
            if (harnessAbn && CanCountWarn("HarnessMissing", now)) added++;
            if (unsafeAbn && CanCountWarn("UnsafeInstall", now)) added++;

            if (added > 0)
            {
                _warnToday += added;
                TxtWarnToday.Text = $"{_warnToday}건";

                string msg = $"{now:HH:mm} 비정상 감지 (+{added})";
                TxtAlertLog.Text = msg;
                TxtBottomLog.Text = msg;
            }

            // =========================
            // ✅ 이벤트 로그(리포트용)는 기존처럼 기록 (1초 디바운스 유지)
            // =========================
            if (abnormalCount > 0 && _workStartAt != null && _workEndAt == null)
            {
                if (peopleAbn)
                    AddEventOncePerSecond(now, "PeopleMismatch", $"peopleStable={peopleStable}, minExpected={EXPECTED_PEOPLE}");


                if (helmetAbn)
                    AddEventOncePerSecond(now, "HelmetMissing", $"peopleStable={peopleStable}, helmetCount={helmetCount}");

                if (harnessAbn)
                    AddEventOncePerSecond(now, "HarnessMissing", $"peopleStable={peopleStable}, harnessCount={harnessCount}");

                if (unsafeAbn)
                    AddEventOncePerSecond(now, "UnsafeInstall", TxtUcSummary.Text);
            }
        }



        private DateTime _lastEventWrittenAt = DateTime.MinValue;
        private string _lastEventKey = "";

        private void AddEventOncePerSecond(DateTime now, string type, string detail)
        {
            // 같은 type/detail이 너무 자주 쌓이지 않게 1초 디바운스
            string key = $"{type}|{detail}";
            if (key == _lastEventKey && (now - _lastEventWrittenAt).TotalSeconds < 1.0)
                return;

            _events.Add(new ReportEvent
            {
                Time = now,
                Type = type,
                Detail = detail
            });

            _totalAbnormalEvents++;
            _lastEventKey = key;
            _lastEventWrittenAt = now;
        }

        private Detection? UpdateTracking(int idx, Mat frame, List<Detection> personDets)
        {
            if (frame.Empty()) return null;

            bool yoloHasPerson = personDets.Count > 0;

            if (yoloHasPerson)
            {
                _framesSinceYolo = 0;
                var best = PickBestPerson(personDets);

                bool needInit =
                    !_personTracker.IsActive
                    || !_hasLastTrackBox
                    || (_cfg.TrackingReinitEveryNFrames > 0 && (idx % _cfg.TrackingReinitEveryNFrames == 0));

                if (needInit)
                {
                    Rect2d yoloBox = DetToRect2d(best);
                    _personTracker.Init(frame, yoloBox);

                    if (_personTracker.IsActive)
                    {
                        _lastTrackBox = yoloBox;
                        _hasLastTrackBox = true;
                        _framesSinceTrackerOk = 0;
                    }
                }
                else
                {
                    Rect2d trackBox;
                    bool ok = _personTracker.Update(frame, out trackBox);

                    if (ok)
                    {
                        _lastTrackBox = trackBox;
                        _hasLastTrackBox = true;
                        _framesSinceTrackerOk = 0;
                    }
                    else
                    {
                        _framesSinceTrackerOk++;
                    }
                }
            }
            else
            {
                _framesSinceYolo++;

                if (_personTracker.IsActive && _hasLastTrackBox)
                {
                    Rect2d trackBox;
                    bool ok = _personTracker.Update(frame, out trackBox);

                    if (ok)
                    {
                        _lastTrackBox = trackBox;
                        _hasLastTrackBox = true;
                        _framesSinceTrackerOk = 0;
                    }
                    else
                    {
                        _framesSinceTrackerOk++;
                    }
                }
                else
                {
                    _framesSinceTrackerOk++;
                }
            }

            if (_cfg.TrackingFailResetAfterNFrames > 0 && _framesSinceTrackerOk >= _cfg.TrackingFailResetAfterNFrames)
            {
                _personTracker.Reset();
                _hasLastTrackBox = false;
                _lastTrackBox = default;
                _framesSinceTrackerOk = 0;
                return null;
            }

            if (_hasLastTrackBox && _personTracker.IsActive)
            {
                return new Detection
                {
                    Cls = "person_track",
                    Conf = 1.0,
                    X1 = Clamp(_lastTrackBox.X, 0, _imgW - 1),
                    Y1 = Clamp(_lastTrackBox.Y, 0, _imgH - 1),
                    X2 = Clamp(_lastTrackBox.X + _lastTrackBox.Width, 0, _imgW - 1),
                    Y2 = Clamp(_lastTrackBox.Y + _lastTrackBox.Height, 0, _imgH - 1),
                };
            }

            return null;
        }

        private static Mat SafeImRead(string imgPath)
        {
            try { return Cv2.ImRead(imgPath, ImreadModes.Color); }
            catch { return new Mat(); }
        }

        private static Detection PickBestPerson(List<Detection> personDets)
        {
            Detection best = personDets[0];
            double bestA = Area(best);
            for (int i = 1; i < personDets.Count; i++)
            {
                double a = Area(personDets[i]);
                if (a > bestA)
                {
                    bestA = a;
                    best = personDets[i];
                }
            }
            return best;
        }

        private static double Area(Detection d)
        {
            double w = Math.Max(0, d.X2 - d.X1);
            double h = Math.Max(0, d.Y2 - d.Y1);
            return w * h;
        }

        private static Rect2d DetToRect2d(Detection d)
        {
            double x = d.X1;
            double y = d.Y1;
            double w = Math.Max(1, d.X2 - d.X1);
            double h = Math.Max(1, d.Y2 - d.Y1);
            return new Rect2d(x, y, w, h);
        }

        private static double Clamp(double v, double lo, double hi)
            => v < lo ? lo : (v > hi ? hi : v);

        private List<Detection> LoadYoloLabelsForImageSameFolder(string imgPath, int imgW, int imgH, string labelKind)
        {
            try
            {
                string txtPath = Path.ChangeExtension(imgPath, ".txt");
                if (!File.Exists(txtPath)) return new List<Detection>();
                return LoadYoloLabelsFromTxt(txtPath, imgW, imgH, labelKind);
            }
            catch { return new List<Detection>(); }
        }

        private List<Detection> LoadYoloLabelsFromTxt(string txtPath, int imgW, int imgH, string labelKind)
        {
            var dets = new List<Detection>();
            try
            {
                foreach (var line in File.ReadAllLines(txtPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var sp = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (sp.Length < 5) continue;

                    if (!int.TryParse(sp[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int clsId))
                        continue;

                    if (!double.TryParse(sp[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double cx)) continue;
                    if (!double.TryParse(sp[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double cy)) continue;
                    if (!double.TryParse(sp[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double w)) continue;
                    if (!double.TryParse(sp[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double h)) continue;

                    double conf = 1.0;
                    if (sp.Length >= 6 && double.TryParse(sp[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double c))
                        conf = c;

                    string clsName = "unknown";

                    if (labelKind.Equals("person", StringComparison.OrdinalIgnoreCase))
                    {
                        clsName = "person";
                    }
                    else if (labelKind.Equals("uc", StringComparison.OrdinalIgnoreCase))
                    {
                        clsName = _ucClassMap.TryGetValue(clsId, out var name) ? name : $"UC_{clsId}";
                    }
                    else if (labelKind.Equals("ppe", StringComparison.OrdinalIgnoreCase))
                    {
                        clsName = _ppeClassMap.TryGetValue(clsId, out var name) ? name : $"PPE_{clsId}";
                        if (clsName.Equals("Person", StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    else if (labelKind.Equals("high", StringComparison.OrdinalIgnoreCase))
                    {
                        clsName = _highClassMap.TryGetValue(clsId, out var name) ? name : $"HIGH_{clsId}";
                    }

                    double px = cx * imgW;
                    double py = cy * imgH;
                    double pw = w * imgW;
                    double ph = h * imgH;

                    double x1 = px - pw / 2.0;
                    double y1 = py - ph / 2.0;
                    double x2 = px + pw / 2.0;
                    double y2 = py + ph / 2.0;

                    x1 = Math.Max(0, Math.Min(imgW - 1, x1));
                    y1 = Math.Max(0, Math.Min(imgH - 1, y1));
                    x2 = Math.Max(0, Math.Min(imgW - 1, x2));
                    y2 = Math.Max(0, Math.Min(imgH - 1, y2));

                    if (x2 <= x1 || y2 <= y1) continue;

                    dets.Add(new Detection { Cls = clsName, Conf = conf, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 });
                }
            }
            catch { }
            return dets;
        }

        private void DrawDetections(List<Detection> dets)
        {
            if (_imgW <= 0 || _imgH <= 0) return;

            double hostW = ImgCctv.ActualWidth;
            double hostH = ImgCctv.ActualHeight;
            if (hostW <= 0 || hostH <= 0) return;

            double scale = Math.Min(hostW / _imgW, hostH / _imgH);
            double drawnW = _imgW * scale;
            double drawnH = _imgH * scale;

            double offX = (hostW - drawnW) / 2.0;
            double offY = (hostH - drawnH) / 2.0;

            OverlayCanvas.Children.Clear();

            foreach (var d in dets)
            {
                Brush stroke = _clsBrush.TryGetValue(d.Cls, out var b) ? b : Brushes.Yellow;

                double x1 = offX + d.X1 * scale;
                double y1 = offY + d.Y1 * scale;
                double w = (d.X2 - d.X1) * scale;
                double h = (d.Y2 - d.Y1) * scale;

                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = w,
                    Height = h,
                    Stroke = stroke,
                    StrokeThickness = d.Cls.Equals("person_track", StringComparison.OrdinalIgnoreCase) ? 3 : 2,
                    Fill = Brushes.Transparent
                };

                Canvas.SetLeft(rect, x1);
                Canvas.SetTop(rect, y1);
                OverlayCanvas.Children.Add(rect);

                var labelBg = new System.Windows.Shapes.Rectangle
                {
                    Width = Math.Max(110, d.Cls.Length * 9 + 60),
                    Height = 22,
                    Fill = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
                    StrokeThickness = 0
                };
                Canvas.SetLeft(labelBg, x1);
                Canvas.SetTop(labelBg, Math.Max(0, y1 - 22));
                OverlayCanvas.Children.Add(labelBg);

                var tb = new TextBlock
                {
                    Text = $"{d.Cls} {d.Conf:0.00}",
                    Foreground = stroke,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(6, 2, 6, 2)
                };
                Canvas.SetLeft(tb, x1);
                Canvas.SetTop(tb, Math.Max(0, y1 - 22));
                OverlayCanvas.Children.Add(tb);
            }
        }

        private void BtnSnapshot_Click(object sender, RoutedEventArgs e)
        {
            if (_frames.Count == 0)
            {
                MessageBox.Show("현재 프레임이 없습니다.");
                return;
            }

            string src = _frames[_frameIndex];
            var dlg = new SaveFileDialog
            {
                Title = "스냅샷 저장",
                Filter = "JPG Image (*.jpg)|*.jpg|PNG Image (*.png)|*.png",
                FileName = Path.GetFileName(src)
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    File.Copy(src, dlg.FileName, overwrite: true);
                    MessageBox.Show("저장 완료!");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"저장 실패: {ex.Message}");
                }
            }
        }

        private string BuildReportCsv()
        {
            var sb = new StringBuilder();

            // ===== Summary =====
            sb.AppendLine("SECTION,KEY,VALUE");
            sb.AppendLine($"SUMMARY,WorkStart,{_workStartAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"SUMMARY,WorkEnd,{_workEndAt:yyyy-MM-dd HH:mm:ss}");

            // 현재 모니터링 문구(구역)
            sb.AppendLine($"SUMMARY,WorkZone,{EscapeCsv(TxtSelectedWork.Text)}");

            // 기준/홀드시간 같이 남기면 “왜 이렇게 판정했는지” 설명 가능
            sb.AppendLine($"SUMMARY,ExpectedPeople,{EXPECTED_PEOPLE}");
            sb.AppendLine($"SUMMARY,HoldPeopleSec,{HOLD_PEOPLE.TotalSeconds}");
            sb.AppendLine($"SUMMARY,HoldPpeSec,{HOLD_PPE.TotalSeconds}");
            sb.AppendLine($"SUMMARY,HoldUnsafeSec,{HOLD_UNSAFE.TotalSeconds}");

            // 통계
            sb.AppendLine($"SUMMARY,MaxPeopleStable,{_maxPeopleStable}");
            sb.AppendLine($"SUMMARY,MaxHelmetDetected,{_maxHelmetCount}");
            sb.AppendLine($"SUMMARY,MaxHarnessDetected,{_maxHarnessCount}");
            sb.AppendLine($"SUMMARY,TotalAbnormalEvents,{_totalAbnormalEvents}");

            sb.AppendLine(); // 빈 줄

            // ===== Events =====
            sb.AppendLine("EVENT_TIME,TYPE,DETAIL");
            foreach (var ev in _events)
            {
                sb.AppendLine($"{ev.Time:yyyy-MM-dd HH:mm:ss},{EscapeCsv(ev.Type)},{EscapeCsv(ev.Detail)}");
            }

            return sb.ToString();
        }

        private static string EscapeCsv(string? s)
        {
            s ??= "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
            {
                s = s.Replace("\"", "\"\"");
                return $"\"{s}\"";
            }
            return s;
        }


        private void BtnReport_Click(object sender, RoutedEventArgs e)
        {
            if (_workStartAt == null)
            {
                MessageBox.Show("작업(모니터링)을 시작한 기록이 없습니다.");
                return;
            }

            // 종료시간이 없으면 지금을 종료시간으로 임시 지정(원하면 막아도 됨)
            if (_workEndAt == null) _workEndAt = DateTime.Now;

            var dlg = new SaveFileDialog
            {
                Title = "안전관리 리포트(CSV) 저장",
                Filter = "CSV (*.csv)|*.csv",
                FileName = $"safety_report_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (dlg.ShowDialog() != true) return;

            try
            {
                string csv = BuildReportCsv();
                File.WriteAllText(dlg.FileName, csv, Encoding.UTF8);
                MessageBox.Show("리포트 저장 완료!");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"리포트 저장 실패: {ex.Message}");
            }
        }


        private void BtnAck_Click(object sender, RoutedEventArgs e)
        {
            TxtAlertLog.Text = "(로그 없음)";
            TxtBottomLog.Text = "(로그 없음)";
        }

        // ✅ 버튼은 없어졌어도 이 함수가 있어도 컴파일 문제 없음(원하면 삭제 가능)
        private async void BtnInfer_Click(object sender, RoutedEventArgs e)
        {
            await RunUcInferForCurrentFrameAsync(force: true);
            await RunPersonInferForCurrentFrameAsync(force: true);
            await RunPpeInferForCurrentFrameAsync(force: true);
            await RunHighInferForCurrentFrameAsync(force: true);
        }


        // =========================
        // INFER (seqPath만 _currentSeqPath로)
        // =========================
        private async Task RunUcInferForCurrentFrameAsync(bool force = false)
        {
            if (_ucRunning && !force) return;
            if (_frames.Count == 0) return;

            string? seqPath = _currentSeqPath;
            if (string.IsNullOrWhiteSpace(seqPath) || !Directory.Exists(seqPath)) return;

            string curImg = _frames[_frameIndex];

            string pyExe = _cfg.PythonExe?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(pyExe) || !File.Exists(pyExe))
            {
                TxtCctvStatus.Text = "PythonExe invalid";
                return;
            }

            string scriptPath = ResolveToFullPathSmart(_cfg.InferScript);
            string modelPath = ResolveToFullPathSmart(_cfg.ModelPath);

            if (!File.Exists(scriptPath) || !File.Exists(modelPath))
            {
                TxtCctvStatus.Text = $"UC InferScript/Model missing\nscript:{scriptPath}\nmodel:{modelPath}";
                return;
            }

            string predDir = Path.Combine(seqPath, _cfg.PredFolder ?? "pred_uc");
            Directory.CreateDirectory(predDir);

            string srcArg = _cfg.InferSingleFrameOnly ? curImg : seqPath;

            string args =
                $"\"{scriptPath}\" --src \"{srcArg}\" --weights \"{modelPath}\" --out \"{predDir}\" " +
                $"--imgsz {_cfg.ImgSz} --conf {_cfg.Conf.ToString(CultureInfo.InvariantCulture)} --iou {_cfg.Iou.ToString(CultureInfo.InvariantCulture)} " +
                $"--device \"{_cfg.Device}\"";

            if (_cfg.InferSingleFrameOnly) args += " --single";

            _ucRunning = true;
            try
            {
                var (code, stdout, stderr) = await Task.Run(() => RunProcess(pyExe, args, _projectRoot));
                if (code != 0)
                {
                    MessageBox.Show($"UC Infer 실패 (exit={code})\n\nSTDERR:\n{stderr}\n\nSTDOUT:\n{stdout}");
                    TxtCctvStatus.Text = "UC infer failed";
                    return;
                }
                ShowFrame(_frameIndex);
            }
            finally { _ucRunning = false; }
        }

        private async Task RunPersonInferForCurrentFrameAsync(bool force = false)
        {
            if (_personRunning && !force) return;
            if (_frames.Count == 0) return;

            string? seqPath = _currentSeqPath;
            if (string.IsNullOrWhiteSpace(seqPath) || !Directory.Exists(seqPath)) return;

            string curImg = _frames[_frameIndex];

            string pyExe = _cfg.PythonExe?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(pyExe) || !File.Exists(pyExe))
            {
                TxtCctvStatus.Text = "PythonExe invalid";
                return;
            }

            string scriptPath = ResolveToFullPathSmart(_cfg.PersonInferScript);
            string modelPath = ResolveToFullPathSmart(_cfg.PersonModelPath);

            if (!File.Exists(scriptPath) || !File.Exists(modelPath))
            {
                TxtCctvStatus.Text = $"PERSON InferScript/Model missing\nscript:{scriptPath}\nmodel:{modelPath}";
                return;
            }

            string predDir = Path.Combine(seqPath, _cfg.PersonPredFolder ?? "pred_person");
            Directory.CreateDirectory(predDir);

            string srcArg = _cfg.PersonSingleFrameOnly ? curImg : seqPath;

            string args =
                $"\"{scriptPath}\" --src \"{srcArg}\" --weights \"{modelPath}\" --out \"{predDir}\" " +
                $"--imgsz {_cfg.ImgSz} --conf {_cfg.Conf.ToString(CultureInfo.InvariantCulture)} --iou {_cfg.Iou.ToString(CultureInfo.InvariantCulture)} " +
                $"--device \"{_cfg.Device}\"";

            if (_cfg.PersonSingleFrameOnly) args += " --single";

            _personRunning = true;
            try
            {
                var (code, stdout, stderr) = await Task.Run(() => RunProcess(pyExe, args, _projectRoot));
                if (code != 0)
                {
                    TxtCctvStatus.Text = $"PERSON infer failed (exit={code})";
                    MessageBox.Show($"PERSON Infer 실패 (exit={code})\n\nSTDERR:\n{stderr}\n\nSTDOUT:\n{stdout}");
                    return;
                }
                ShowFrame(_frameIndex);
            }
            finally { _personRunning = false; }
        }

        private async Task RunPpeInferForCurrentFrameAsync(bool force = false)
        {
            if (_ppeRunning && !force) return;
            if (_frames.Count == 0) return;

            string? seqPath = _currentSeqPath;
            if (string.IsNullOrWhiteSpace(seqPath) || !Directory.Exists(seqPath)) return;

            string curImg = _frames[_frameIndex];

            string pyExe = _cfg.PythonExe?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(pyExe) || !File.Exists(pyExe))
            {
                TxtCctvStatus.Text = "PythonExe invalid";
                return;
            }

            string scriptPath = ResolveToFullPathSmart(_cfg.PpeInferScript);
            string modelPath = ResolveToFullPathSmart(_cfg.PpeModelPath);

            if (!File.Exists(scriptPath) || !File.Exists(modelPath))
            {
                TxtCctvStatus.Text = $"PPE InferScript/Model missing\nscript:{scriptPath}\nmodel:{modelPath}";
                return;
            }

            string predDir = Path.Combine(seqPath, _cfg.PpePredFolder ?? "pred_ppe");
            Directory.CreateDirectory(predDir);

            string srcArg = _cfg.PpeSingleFrameOnly ? curImg : seqPath;

            string args =
                $"\"{scriptPath}\" --src \"{srcArg}\" --weights \"{modelPath}\" --out \"{predDir}\" " +
                $"--imgsz {_cfg.ImgSz} --conf {_cfg.Conf.ToString(CultureInfo.InvariantCulture)} --iou {_cfg.Iou.ToString(CultureInfo.InvariantCulture)} " +
                $"--device \"{_cfg.Device}\"";

            if (_cfg.PpeSingleFrameOnly) args += " --single";

            _ppeRunning = true;
            try
            {
                var (code, stdout, stderr) = await Task.Run(() => RunProcess(pyExe, args, _projectRoot));
                if (code != 0)
                {
                    TxtCctvStatus.Text = $"PPE infer failed (exit={code})";
                    MessageBox.Show($"PPE Infer 실패 (exit={code})\n\nSTDERR:\n{stderr}\n\nSTDOUT:\n{stdout}");
                    return;
                }
                ShowFrame(_frameIndex);
            }
            finally { _ppeRunning = false; }
        }

        private async Task RunHighInferForCurrentFrameAsync(bool force = false)
        {
            if (_highRunning && !force) return;
            if (_frames.Count == 0) return;

            string? seqPath = _currentSeqPath;
            if (string.IsNullOrWhiteSpace(seqPath) || !Directory.Exists(seqPath)) return;

            string curImg = _frames[_frameIndex];

            string pyExe = _cfg.PythonExe?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(pyExe) || !File.Exists(pyExe))
            {
                TxtCctvStatus.Text = "PythonExe invalid";
                return;
            }

            string scriptPath = ResolveToFullPathSmart(_cfg.HighInferScript);
            string modelPath = ResolveToFullPathSmart(_cfg.HighModelPath);

            if (!File.Exists(scriptPath) || !File.Exists(modelPath))
            {
                TxtCctvStatus.Text = $"HIGH InferScript/Model missing\nscript:{scriptPath}\nmodel:{modelPath}";
                return;
            }

            string predDir = Path.Combine(seqPath, _cfg.HighPredFolder ?? "pred_high");
            Directory.CreateDirectory(predDir);

            string srcArg = _cfg.HighSingleFrameOnly ? curImg : seqPath;

            string args =
                $"\"{scriptPath}\" --src \"{srcArg}\" --weights \"{modelPath}\" --out \"{predDir}\" " +
                $"--imgsz {_cfg.ImgSz} --conf {_cfg.Conf.ToString(CultureInfo.InvariantCulture)} --iou {_cfg.Iou.ToString(CultureInfo.InvariantCulture)} " +
                $"--device \"{_cfg.Device}\"";

            if (_cfg.HighSingleFrameOnly) args += " --single";

            _highRunning = true;
            try
            {
                var (code, stdout, stderr) = await Task.Run(() => RunProcess(pyExe, args, _projectRoot));
                if (code != 0)
                {
                    TxtCctvStatus.Text = $"HIGH infer failed (exit={code})";
                    MessageBox.Show($"HIGH Infer 실패 (exit={code})\n\nSTDERR:\n{stderr}\n\nSTDOUT:\n{stdout}");
                    return;
                }
                ShowFrame(_frameIndex);
            }
            finally { _highRunning = false; }
        }

        private (int exitCode, string stdout, string stderr) RunProcess(string exe, string args, string workDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var p = new Process();
            p.StartInfo = psi;
            p.Start();

            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();

            return (p.ExitCode, stdout, stderr);
        }

        private string GetPredTxtPath(string? seqPath, string imgPath, string predFolder)
        {
            if (string.IsNullOrWhiteSpace(seqPath)) return "";
            string fileName = Path.GetFileNameWithoutExtension(imgPath);
            string predDir = Path.Combine(seqPath, predFolder ?? "pred");
            return Path.Combine(predDir, fileName + ".txt");
        }

        private string ResolveToFullPathSmart(string pathOrRelative)
        {
            if (string.IsNullOrWhiteSpace(pathOrRelative)) return pathOrRelative;

            if (Path.IsPathRooted(pathOrRelative))
                return pathOrRelative;

            string p1 = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, pathOrRelative));
            if (File.Exists(p1) || Directory.Exists(p1)) return p1;

            string p2 = Path.GetFullPath(Path.Combine(_projectRoot, pathOrRelative));
            return p2;
        }

        private string FindProjectRoot(string startDir)
        {
            try
            {
                var dir = new DirectoryInfo(startDir);
                for (int i = 0; i < 12 && dir != null; i++)
                {
                    var csproj = dir.GetFiles("*.csproj").FirstOrDefault();
                    if (csproj != null) return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch { }
            return Directory.GetCurrentDirectory();
        }
    }
}
