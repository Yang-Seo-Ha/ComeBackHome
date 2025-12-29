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

            TxtUcCount.Text = "0";
            TxtPersonCount.Text = "0";
            TxtPpeCount.Text = "0";
            TxtHighCount.Text = "0";
            TxtPeopleNow.Text = "0";
            TxtPeopleWarn.Text = "-";

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (_, __) => TxtHeaderTime.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            _clockTimer.Start();

            _projectRoot = FindProjectRoot(AppDomain.CurrentDomain.BaseDirectory);

            LoadLocalConfig();
            ScanChannelsToCombo();

            CmbChannel.SelectionChanged += (_, __) => PrepareFramesFromSelectedChannel();

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
                    $"✅ Config OK\nRoot:\n{_cfg.DatasetRoot}\nFPS: {_cfg.Fps}\nConfig:\n{cfgPath}\nProjectRoot:\n{_projectRoot}";
            }
            catch (Exception ex)
            {
                TxtCctvStatus.Text = $"❌ Config load failed: {ex.Message}";
            }
        }

        private void ScanChannelsToCombo()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_cfg.DatasetRoot) || !Directory.Exists(_cfg.DatasetRoot))
                    return;

                var seqDirs = Directory.GetDirectories(_cfg.DatasetRoot)
                    .Select(d => new { Name = Path.GetFileName(d) ?? d, Full = d })
                    .OrderBy(x => x.Name)
                    .ToList();

                CmbChannel.ItemsSource = seqDirs;
                CmbChannel.DisplayMemberPath = "Name";
                CmbChannel.SelectedValuePath = "Full";

                if (seqDirs.Count == 0)
                {
                    TxtCctvStatus.Text = $"No sequences found in:\n{_cfg.DatasetRoot}";
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_cfg.DefaultSequence))
                {
                    var hit = seqDirs.FirstOrDefault(x => x.Name.Equals(_cfg.DefaultSequence, StringComparison.OrdinalIgnoreCase));
                    CmbChannel.SelectedValue = hit != null ? hit.Full : seqDirs[0].Full;
                }
                else
                {
                    CmbChannel.SelectedValue = seqDirs[0].Full;
                }

                PrepareFramesFromSelectedChannel();
            }
            catch (Exception ex)
            {
                TxtCctvStatus.Text = $"Scan failed: {ex.Message}";
            }
        }

        private void PrepareFramesFromSelectedChannel()
        {
            _playTimer.Stop();
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

            string? seqPath = CmbChannel.SelectedValue as string;
            if (string.IsNullOrWhiteSpace(seqPath) || !Directory.Exists(seqPath))
            {
                TxtCctvStatus.Text = "No channel selected.";
                TxtSelectedChannel.Text = "-";
                return;
            }

            TxtSelectedChannel.Text = Path.GetFileName(seqPath);

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

        private void BtnPlayStop_Click(object sender, RoutedEventArgs e)
        {
            if (_frames.Count == 0)
                PrepareFramesFromSelectedChannel();
            if (_frames.Count == 0) return;

            if (_isPlaying)
            {
                _playTimer.Stop();
                _isPlaying = false;
                TxtCctvStatus.Text = "Paused";
            }
            else
            {
                int fps = Math.Max(1, _cfg.Fps);
                _playTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
                _playTimer.Start();
                _isPlaying = true;
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

            string? seqPath = CmbChannel.SelectedValue as string;
            OverlayCanvas.Children.Clear();

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

            // ✅ tracking
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

            // ✅ 우측 패널 갱신
            TxtUcCount.Text = ucDets.Count.ToString();
            TxtPersonCount.Text = personDets.Count.ToString();
            TxtPpeCount.Text = ppeDets.Count.ToString();
            TxtHighCount.Text = highDets.Count.ToString();

            TxtPeopleNow.Text = personDets.Count.ToString();
            TxtPeopleWarn.Text = (personDets.Count == 2) ? "정상" : "⚠ 인원 확인 필요";

            // 좌측 인원
            TxtPeopleLineA.Text = $"적정 2명 / 현재 {personDets.Count}명";
            TxtPeopleLineB.Text = $"적정 2명 / 현재 {personDets.Count}명";
            TxtPeopleBadgeA.Text = (personDets.Count == 2) ? "정상" : "인원확인";
            TxtPeopleBadgeB.Text = (personDets.Count == 2) ? "정상" : "인원확인";
            TxtLastDetectTime.Text = $"마지막 감지: {DateTime.Now:HH:mm:ss}";

            string trkNote = _cfg.TrackingEnabled
                ? $"TRK:{(_personTracker.IsActive ? "on" : "off")} yoloGap:{_framesSinceYolo} fail:{_framesSinceTrackerOk}"
                : "TRK:off";

            TxtCctvStatus.Text =
                $"{Path.GetFileName(imgPath)} ({idx + 1}/{_frames.Count})  " +
                $"UC:{ucDets.Count}  PERSON:{personDets.Count}  PPE:{ppeDets.Count}  HIGH:{highDets.Count}  {trkNote}";
        }

        // ✅ UpdateTracking: 파일 내 1개만 존재
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
            double viewW = ImgCctv.ActualWidth;
            double viewH = ImgCctv.ActualHeight;
            if (_imgW <= 0 || _imgH <= 0 || viewW <= 0 || viewH <= 0) return;

            double scale = Math.Min(viewW / _imgW, viewH / _imgH);
            double offX = (viewW - _imgW * scale) / 2.0;
            double offY = (viewH - _imgH * scale) / 2.0;

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

        private void BtnAck_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Ack 처리(예시).");
        }

        private async void BtnInfer_Click(object sender, RoutedEventArgs e)
        {
            await RunUcInferForCurrentFrameAsync(force: true);
            await RunPpeInferForCurrentFrameAsync(force: true);
            await RunHighInferForCurrentFrameAsync(force: true);
        }

        // =========================
        // ✅ BENCH: 300 frames -> CSV
        // =========================
        private async void BtnBench_Click(object sender, RoutedEventArgs e)
        {
            if (_frames.Count == 0)
            {
                MessageBox.Show("먼저 채널을 선택하고 프레임을 로드하세요.");
                return;
            }

            try
            {
                BtnBench.IsEnabled = false;
                TxtCctvStatus.Text = "Benchmark running...";

                string? seqPath = CmbChannel.SelectedValue as string;
                if (string.IsNullOrWhiteSpace(seqPath) || !Directory.Exists(seqPath))
                {
                    MessageBox.Show("채널 경로가 올바르지 않습니다.");
                    return;
                }

                // 현재 선택 트래커 타입
                TrackerType t = TrackerType.CSRT;
                if (CmbTrackerType.SelectedIndex == 1) t = TrackerType.KCF;
                else if (CmbTrackerType.SelectedIndex == 2) t = TrackerType.MIL;

                int N = 300;
                var result = await Task.Run(() => RunTrackingBenchmark(seqPath, t, N));

                string outDir = Path.Combine(_projectRoot, "benchmarks");
                Directory.CreateDirectory(outDir);

                string seqName = Path.GetFileName(seqPath);
                string csvPath = Path.Combine(outDir,
                    $"bench_{seqName}_{t}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

                File.WriteAllText(csvPath, result, Encoding.UTF8);

                TxtCctvStatus.Text = $"✅ Bench done: {t} / saved:\n{csvPath}";
                MessageBox.Show($"벤치 완료!\n\nTracker: {t}\nSaved:\n{csvPath}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"벤치 실패: {ex.Message}");
            }
            finally
            {
                BtnBench.IsEnabled = true;
            }
        }

        // CSV 문자열 생성
        private string RunTrackingBenchmark(string seqPath, TrackerType t, int maxFrames)
        {
            // 전략:
            // - pred_person 폴더의 YOLO 결과(txt)가 있으면 첫 bbox로 init
            // - 이후는 tracker.Update만 수행하면서
            //   성공률 / 평균 ms / fps 계산
            // - 만약 init할 bbox가 끝까지 없으면 "no_init"로 종료

            var frames = Directory.GetFiles(seqPath)
                .Where(p => p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p)
                .Take(Math.Max(1, maxFrames))
                .ToList();

            int total = frames.Count;

            // 측정값
            int initFrame = -1;
            int updateCount = 0;
            int okCount = 0;
            double sumMs = 0;

            // tracker instance (벤치용 독립)
            var tracker = new PersonTracker(t);
            bool trackerActive = false;

            // img size는 첫 프레임 기준으로 잡고, bbox decode에도 사용
            int imgW = 0, imgH = 0;

            for (int i = 0; i < total; i++)
            {
                string img = frames[i];
                using Mat m = SafeImRead(img);
                if (m.Empty()) continue;

                if (imgW == 0) { imgW = m.Width; imgH = m.Height; }

                // yolo person txt path
                string predTxt = GetPredTxtPath(seqPath, img, _cfg.PersonPredFolder);

                if (!trackerActive)
                {
                    // init은 YOLO person bbox가 있을 때만
                    if (File.Exists(predTxt))
                    {
                        var dets = LoadYoloLabelsFromTxt(predTxt, imgW, imgH, "person");
                        if (dets.Count > 0)
                        {
                            var best = PickBestPerson(dets);
                            tracker.Init(m, DetToRect2d(best));
                            trackerActive = tracker.IsActive;
                            initFrame = i;
                        }
                    }

                    continue;
                }

                // update timing
                var sw = Stopwatch.StartNew();
                Rect2d box;
                bool ok = tracker.Update(m, out box);
                sw.Stop();

                updateCount++;
                sumMs += sw.Elapsed.TotalMilliseconds;
                if (ok) okCount++;
            }

            // 결과 정리
            double avgMs = updateCount > 0 ? sumMs / updateCount : 0;
            double fps = avgMs > 0 ? 1000.0 / avgMs : 0;
            double okRate = updateCount > 0 ? (double)okCount / updateCount : 0;

            // CSV: header + summary row
            var sb = new StringBuilder();
            sb.AppendLine("seq,tracker,total_frames,init_frame,updates,ok,ok_rate,avg_update_ms,est_fps,person_pred_folder");
            sb.AppendLine(string.Join(",",
                Csv(seqPath),
                Csv(t.ToString()),
                total.ToString(CultureInfo.InvariantCulture),
                initFrame.ToString(CultureInfo.InvariantCulture),
                updateCount.ToString(CultureInfo.InvariantCulture),
                okCount.ToString(CultureInfo.InvariantCulture),
                okRate.ToString("0.000", CultureInfo.InvariantCulture),
                avgMs.ToString("0.000", CultureInfo.InvariantCulture),
                fps.ToString("0.0", CultureInfo.InvariantCulture),
                Csv(_cfg.PersonPredFolder ?? "pred_person")
            ));

            // 부가: init 못 했으면 원인 로그도 한 줄 더
            if (initFrame < 0)
            {
                sb.AppendLine();
                sb.AppendLine("note,no_init_bbox_found_in_pred_person_txt");
            }

            return sb.ToString();
        }

        private static string Csv(string s)
        {
            // 간단 CSV escape
            s ??= "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
            {
                s = s.Replace("\"", "\"\"");
                return $"\"{s}\"";
            }
            return s;
        }

        // =========================
        // INFER (너 기존 로직 유지)
        // =========================
        private async Task RunUcInferForCurrentFrameAsync(bool force = false)
        {
            if (_ucRunning && !force) return;
            if (_frames.Count == 0) return;

            string? seqPath = CmbChannel.SelectedValue as string;
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

            string? seqPath = CmbChannel.SelectedValue as string;
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

            string? seqPath = CmbChannel.SelectedValue as string;
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

            string? seqPath = CmbChannel.SelectedValue as string;
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
