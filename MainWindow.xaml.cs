// MainWindow.xaml.cs (FULL)
// - Play / UC infer / Person infer / PPE infer timers separated
// - Python run is Task.Run async (avoid UI freeze)
// - Load pred txt first, fallback to original label txt (UC only)
// - Resolve paths from bin + project root
// - Fix: --single flag logic (was inverted)

using Microsoft.Win32;
using Newtonsoft.Json;
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

        // ---- PPE (NEW) ----
        // 너 JSON에 이미 PpeModelPath는 넣어놨으니, Script/Folder도 키를 추가하는걸 추천
        public string PpeInferScript { get; set; } = @"scripts\infer_to_txt.py";
        public string PpeModelPath { get; set; } = @"assets\models\ppe_v1.pt";
        public string PpePredFolder { get; set; } = "pred_ppe";

        public bool AutoPpeEnabled { get; set; } = true;
        public int PpeIntervalMs { get; set; } = 1400;
        public bool PpeSingleFrameOnly { get; set; } = true;

        // ---- common infer params ----
        public int ImgSz { get; set; } = 640;
        public double Conf { get; set; } = 0.25;
        public double Iou { get; set; } = 0.45;
        public string Device { get; set; } = "cpu";
    }

    public partial class MainWindow : Window
    {
        private LocalConfig _cfg = new LocalConfig();

        private readonly DispatcherTimer _playTimer = new DispatcherTimer();
        private readonly DispatcherTimer _ucTimer = new DispatcherTimer();
        private readonly DispatcherTimer _personTimer = new DispatcherTimer();
        private readonly DispatcherTimer _ppeTimer = new DispatcherTimer();

        private List<string> _frames = new List<string>();
        private int _frameIndex = 0;
        private bool _isPlaying = false;

        private int _imgW = 0;
        private int _imgH = 0;

        private bool _ucRunning = false;
        private bool _personRunning = false;
        private bool _ppeRunning = false;

        private string _projectRoot = "";

        // UC 클래스맵 (너 데이터 기준)
        private readonly Dictionary<int, string> _ucClassMap = new Dictionary<int, string>
        {
            { 0, "UC-07" },
            { 1, "UC-08" },
        };

        // PPE 클래스맵 (아직 클래스명 확정 전이면 일단 PPE_0, PPE_1...)
        // 팀원한테 클래스 순서(0=helmet? 1=harness?) 받으면 여기만 바꾸면 됨
        private readonly Dictionary<int, string> _ppeClassMap = new Dictionary<int, string>
        {
            // { 0, "helmet" },
            // { 1, "harness" },
        };

        private readonly Dictionary<string, Brush> _clsBrush =
            new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase)
            {
                // UC
                ["UC-07"] = Brushes.DeepSkyBlue,
                ["UC-08"] = Brushes.Lime,

                // PERSON
                ["person"] = Brushes.Gold,

                // PPE (예시 색)
                ["helmet"] = Brushes.Orange,
                ["harness"] = Brushes.HotPink,
                ["vest"] = Brushes.MediumPurple,
            };

        public class Detection
        {
            public string Cls { get; set; } = "unknown";
            public double Conf { get; set; } = 1.0;
            public double X1 { get; set; }
            public double Y1 { get; set; }
            public double X2 { get; set; }
            public double Y2 { get; set; }
        }

        public MainWindow()
        {
            InitializeComponent();

            TxtCctvStatus.Text = "Booting...";
            TxtHeaderTime.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clock.Tick += (_, __) => TxtHeaderTime.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            clock.Start();

            CmbChannel.SelectionChanged += (_, __) => PrepareFramesFromSelectedChannel();

            _projectRoot = FindProjectRoot(AppDomain.CurrentDomain.BaseDirectory);

            LoadLocalConfig();
            ScanChannelsToCombo();

            // play
            int fps = Math.Max(1, _cfg.Fps);
            _playTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
            _playTimer.Tick += (_, __) => ShowNextFrame();

            // UC
            _ucTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(200, _cfg.InferIntervalMs));
            _ucTimer.Tick += async (_, __) =>
            {
                if (!_cfg.AutoInferEnabled) return;
                await RunUcInferForCurrentFrameAsync();
            };

            // PERSON
            _personTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(300, _cfg.PersonIntervalMs));
            _personTimer.Tick += async (_, __) =>
            {
                if (!_cfg.AutoPersonEnabled) return;
                await RunPersonInferForCurrentFrameAsync();
            };

            // PPE
            _ppeTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(300, _cfg.PpeIntervalMs));
            _ppeTimer.Tick += async (_, __) =>
            {
                if (!_cfg.AutoPpeEnabled) return;
                await RunPpeInferForCurrentFrameAsync();
            };

            if (_cfg.AutoInferEnabled) _ucTimer.Start();
            if (_cfg.AutoPersonEnabled) _personTimer.Start();
            if (_cfg.AutoPpeEnabled) _ppeTimer.Start();
        }

        // -------------------------
        // (A) Config
        // -------------------------
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

                var loaded = JsonConvert.DeserializeObject<LocalConfig>(File.ReadAllText(cfgPath));
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

                // 기본값 방어
                if (string.IsNullOrWhiteSpace(_cfg.PredFolder)) _cfg.PredFolder = "pred_uc";
                if (string.IsNullOrWhiteSpace(_cfg.PersonPredFolder)) _cfg.PersonPredFolder = "pred_person";
                if (string.IsNullOrWhiteSpace(_cfg.PpePredFolder)) _cfg.PpePredFolder = "pred_ppe";

                TxtCctvStatus.Text = $"✅ Config OK\nRoot:\n{_cfg.DatasetRoot}\nFPS: {_cfg.Fps}\nProjectRoot:\n{_projectRoot}";
            }
            catch (Exception ex)
            {
                TxtCctvStatus.Text = $"❌ Config load failed: {ex}";
            }
        }

        // -------------------------
        // (B) Channel scan
        // -------------------------
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

        // -------------------------
        // (C) Frame handling
        // -------------------------
        private void PrepareFramesFromSelectedChannel()
        {
            _playTimer.Stop();
            _isPlaying = false;
            _frameIndex = 0;

            string? seqPath = CmbChannel.SelectedValue as string;
            if (string.IsNullOrWhiteSpace(seqPath) || !Directory.Exists(seqPath))
            {
                TxtCctvStatus.Text = "No channel selected.";
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

            var bmp = new BitmapImage();
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

            // ---- UC: pred_uc 우선, 없으면 원본 라벨(.txt) fallback ----
            string predUcTxt = GetPredTxtPath(seqPath, imgPath, _cfg.PredFolder);
            List<Detection> ucDets = File.Exists(predUcTxt)
                ? LoadYoloLabelsFromTxt(predUcTxt, _imgW, _imgH, labelKind: "uc")
                : LoadYoloLabelsForImageSameFolder(imgPath, _imgW, _imgH, labelKind: "uc");

            // ---- PERSON: pred_person만 (없으면 none) ----
            string predPersonTxt = GetPredTxtPath(seqPath, imgPath, _cfg.PersonPredFolder);
            List<Detection> personDets = File.Exists(predPersonTxt)
                ? LoadYoloLabelsFromTxt(predPersonTxt, _imgW, _imgH, labelKind: "person")
                : new List<Detection>();

            // ---- PPE: pred_ppe만 (없으면 none) ----
            string predPpeTxt = GetPredTxtPath(seqPath, imgPath, _cfg.PpePredFolder);
            List<Detection> ppeDets = File.Exists(predPpeTxt)
                ? LoadYoloLabelsFromTxt(predPpeTxt, _imgW, _imgH, labelKind: "ppe")
                : new List<Detection>();

            // draw all
            var all = new List<Detection>();
            all.AddRange(ucDets);
            all.AddRange(personDets);
            all.AddRange(ppeDets);
            DrawDetections(all);

            string noteUc = File.Exists(predUcTxt) ? "pred_uc" : "label";
            string noteP = File.Exists(predPersonTxt) ? "pred_person" : "none";
            string notePpe = File.Exists(predPpeTxt) ? "pred_ppe" : "none";

            TxtCctvStatus.Text =
                $"{Path.GetFileName(imgPath)} ({idx + 1}/{_frames.Count})  " +
                $"UC:{ucDets.Count} [{noteUc}]  " +
                $"PERSON:{personDets.Count} [{noteP}]  " +
                $"PPE:{ppeDets.Count} [{notePpe}]";
        }

        // -------------------------
        // (D) Label loading
        // -------------------------
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

                    bool okCx = double.TryParse(sp[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double cx);
                    bool okCy = double.TryParse(sp[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double cy);
                    bool okW = double.TryParse(sp[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double w);
                    bool okH = double.TryParse(sp[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double h);
                    if (!(okCx && okCy && okW && okH)) continue;

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
                    }

                    // normalized -> pixel
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

        // -------------------------
        // (E) Drawing
        // -------------------------
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
                    StrokeThickness = d.Cls.Equals("person", StringComparison.OrdinalIgnoreCase) ? 2 : 2,
                    Fill = Brushes.Transparent
                };

                Canvas.SetLeft(rect, x1);
                Canvas.SetTop(rect, y1);
                OverlayCanvas.Children.Add(rect);

                var labelBg = new System.Windows.Shapes.Rectangle
                {
                    Width = Math.Max(90, d.Cls.Length * 9 + 52),
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

        // -------------------------
        // (F) Snapshot / Ack
        // -------------------------
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
                File.Copy(src, dlg.FileName, overwrite: true);
                MessageBox.Show("저장 완료!");
            }
        }

        private void BtnAck_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Ack 처리(예시).");
        }

        // -------------------------
        // (G) Manual Infer Button (UC + PPE 같이)
        // -------------------------
        private async void BtnInfer_Click(object sender, RoutedEventArgs e)
        {
            await RunUcInferForCurrentFrameAsync(force: true);
            await RunPpeInferForCurrentFrameAsync(force: true);
        }

        // -------------------------
        // (H) UC Inference
        // -------------------------
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

            // ✅ FIX: single일 때 --single
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

        // -------------------------
        // (I) PERSON Inference
        // -------------------------
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

            // ✅ FIX
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

        // -------------------------
        // (J) PPE Inference (NEW)
        // -------------------------
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

        // -------------------------
        // Helpers
        // -------------------------
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
                for (int i = 0; i < 10 && dir != null; i++)
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
