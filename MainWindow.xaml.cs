// MainWindow.xaml.cs (FULL)
// - 재생(프레임 표시) 타이머와 추론 타이머를 분리
// - 추론은 비동기로 python 실행(UI Freeze 방지)
// - pred txt 우선 로드, 없으면 원본 txt 로드

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
        public string InferScript { get; set; } = "scripts\\infer_to_txt.py";
        public string ModelPath { get; set; } = "assets\\models\\best.pt";
        public string PredFolder { get; set; } = "pred";

        // ✅ 추론 분리 옵션
        public bool AutoInferEnabled { get; set; } = false;
        public int InferIntervalMs { get; set; } = 1000;
        public bool InferSingleFrameOnly { get; set; } = true;

        // ✅ infer params
        public int ImgSz { get; set; } = 640;
        public double Conf { get; set; } = 0.25;
        public double Iou { get; set; } = 0.45;
        public string Device { get; set; } = "cpu";
    }

    public partial class MainWindow : Window
    {
        private LocalConfig _cfg = new LocalConfig();

        private readonly DispatcherTimer _playTimer = new DispatcherTimer();
        private readonly DispatcherTimer _inferTimer = new DispatcherTimer();

        private List<string> _frames = new List<string>();
        private int _frameIndex = 0;
        private bool _isPlaying = false;

        private int _imgW = 0;
        private int _imgH = 0;

        // ✅ 추론 중복 실행 방지
        private bool _inferRunning = false;

        private readonly Dictionary<int, string> _classMap = new Dictionary<int, string>
        {
            { 0, "UC-07" },
            { 1, "UC-08" },
        };

        private readonly Dictionary<string, Brush> _clsBrush =
            new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase)
            {
                ["UC-07"] = Brushes.DeepSkyBlue,
                ["UC-08"] = Brushes.Lime,
                ["ladder_UC"] = Brushes.Lime,
                ["lift_UC"] = Brushes.Orange,
                ["scaffold_UC"] = Brushes.DeepSkyBlue,
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

            LoadLocalConfig();
            ScanChannelsToCombo();

            // ✅ 재생 타이머
            int fps = Math.Max(1, _cfg.Fps);
            _playTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
            _playTimer.Tick += (_, __) => ShowNextFrame();

            // ✅ 추론 타이머(주기 분리)
            int inferMs = Math.Max(200, _cfg.InferIntervalMs);
            _inferTimer.Interval = TimeSpan.FromMilliseconds(inferMs);
            _inferTimer.Tick += async (_, __) =>
            {
                if (!_cfg.AutoInferEnabled) return;
                await RunInferForCurrentFrameAsync(); // 단일 프레임 추론
            };

            if (_cfg.AutoInferEnabled)
                _inferTimer.Start();
        }

        // -------------------------
        // (A) Config
        // -------------------------
        private void LoadLocalConfig()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string cfgPath = Path.Combine(baseDir, "configs", "local.user.json");

                if (!File.Exists(cfgPath))
                {
                    TxtCctvStatus.Text = $"❌ Config missing\n{cfgPath}\n\nBaseDir:\n{baseDir}";
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
                    TxtCctvStatus.Text = $"❌ Invalid DatasetRoot\n{_cfg.DatasetRoot}\n\nConfig:\n{cfgPath}";
                    return;
                }

                TxtCctvStatus.Text = $"✅ Config OK\nRoot:\n{_cfg.DatasetRoot}\nFPS: {_cfg.Fps}";
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
                    .Select(d => new
                    {
                        Name = Path.GetFileName(d) ?? d,
                        Full = d
                    })
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
                    if (hit != null) CmbChannel.SelectedValue = hit.Full;
                    else CmbChannel.SelectedIndex = 0;
                }
                else
                {
                    CmbChannel.SelectedIndex = 0;
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

            // 이미지 로드
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(imgPath);
            bmp.EndInit();
            bmp.Freeze();

            ImgCctv.Source = bmp;

            _imgW = bmp.PixelWidth;
            _imgH = bmp.PixelHeight;

            OverlayCanvas.Children.Clear();

            // pred 우선
            string? seqPath = CmbChannel.SelectedValue as string;
            string predTxt = GetPredTxtPath(seqPath, imgPath);

            List<Detection> dets;
            if (!string.IsNullOrWhiteSpace(predTxt) && File.Exists(predTxt))
                dets = LoadYoloLabelsFromTxt(predTxt, _imgW, _imgH);
            else
                dets = LoadYoloLabelsForImageSameFolder(imgPath, _imgW, _imgH);

            DrawDetections(dets);

            string note = (File.Exists(predTxt) ? "pred" : "label");
            TxtCctvStatus.Text = $"{Path.GetFileName(imgPath)} ({idx + 1}/{_frames.Count})  det:{dets.Count}  [{note}]";
        }

        // -------------------------
        // (D) Label loading
        // -------------------------
        private List<Detection> LoadYoloLabelsForImageSameFolder(string imgPath, int imgW, int imgH)
        {
            var dets = new List<Detection>();
            try
            {
                string txtPath = Path.ChangeExtension(imgPath, ".txt");
                if (!File.Exists(txtPath)) return dets;
                return LoadYoloLabelsFromTxt(txtPath, imgW, imgH);
            }
            catch { return dets; }
        }

        private List<Detection> LoadYoloLabelsFromTxt(string txtPath, int imgW, int imgH)
        {
            var dets = new List<Detection>();

            try
            {
                foreach (var line in File.ReadAllLines(txtPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // YOLO: cls cx cy w h [conf]
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

                    string clsName = _classMap.TryGetValue(clsId, out var name) ? name : $"cls_{clsId}";

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

                    dets.Add(new Detection
                    {
                        Cls = clsName,
                        Conf = conf,
                        X1 = x1,
                        Y1 = y1,
                        X2 = x2,
                        Y2 = y2
                    });
                }
            }
            catch { }

            return dets;
        }

        // -------------------------
        // (E) Drawing (이미 해결했다 했지만, 유지)
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
                    StrokeThickness = 2,
                    Fill = Brushes.Transparent
                };

                Canvas.SetLeft(rect, x1);
                Canvas.SetTop(rect, y1);
                OverlayCanvas.Children.Add(rect);

                var labelBg = new System.Windows.Shapes.Rectangle
                {
                    Width = Math.Max(80, d.Cls.Length * 9 + 46),
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
        // (G) Manual Infer Button (원하면)
        // -------------------------
        private async void BtnInfer_Click(object sender, RoutedEventArgs e)
        {
            await RunInferForCurrentFrameAsync(force: true);
        }

        // -------------------------
        // (H) Inference (핵심)
        // -------------------------
        private async Task RunInferForCurrentFrameAsync(bool force = false)
        {
            if (_inferRunning && !force) return;
            if (_frames.Count == 0) return;

            string? seqPath = CmbChannel.SelectedValue as string;
            if (string.IsNullOrWhiteSpace(seqPath) || !Directory.Exists(seqPath)) return;

            string curImg = _frames[_frameIndex];

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // python.exe
            string pyExe = _cfg.PythonExe?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(pyExe) || !File.Exists(pyExe))
            {
                TxtCctvStatus.Text = "PythonExe invalid";
                return;
            }

            // script/model
            string scriptPath = ResolveToFullPath(baseDir, _cfg.InferScript);
            string modelPath = ResolveToFullPath(baseDir, _cfg.ModelPath);

            if (!File.Exists(scriptPath) || !File.Exists(modelPath))
            {
                TxtCctvStatus.Text = "InferScript/ModelPath missing";
                return;
            }

            // pred output
            string predDir = Path.Combine(seqPath, _cfg.PredFolder ?? "pred");
            Directory.CreateDirectory(predDir);

            // ✅ src 결정: 단일 프레임이면 파일 경로, 아니면 폴더 경로
            string srcArg = _cfg.InferSingleFrameOnly ? curImg : seqPath;

            // python args
            string args =
                $"\"{scriptPath}\" --src \"{srcArg}\" --weights \"{modelPath}\" --out \"{predDir}\" " +
                $"--imgsz {_cfg.ImgSz} --conf {_cfg.Conf.ToString(CultureInfo.InvariantCulture)} --iou {_cfg.Iou.ToString(CultureInfo.InvariantCulture)} " +
                $"--device \"{_cfg.Device}\"";

            // 폴더인데 single 돌리고 싶으면 --single 추가
            if (!_cfg.InferSingleFrameOnly) args += " --single";

            _inferRunning = true;
            TxtCctvStatus.Text = "Infer running...";

            try
            {
                var (code, stdout, stderr) = await Task.Run(() => RunProcess(pyExe, args, baseDir));

                if (code != 0)
                {
                    MessageBox.Show($"Infer 실패 (exit={code})\n\nSTDERR:\n{stderr}\n\nSTDOUT:\n{stdout}");
                    TxtCctvStatus.Text = "Infer failed";
                    return;
                }

                // ✅ 결과 반영: 현재 프레임 다시 그리기(이제 pred txt가 생겼을 것)
                ShowFrame(_frameIndex);
                TxtCctvStatus.Text = "Infer done ✅";
            }
            finally
            {
                _inferRunning = false;
            }
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
        private string ResolveToFullPath(string baseDir, string pathOrRelative)
        {
            if (string.IsNullOrWhiteSpace(pathOrRelative)) return pathOrRelative;
            if (Path.IsPathRooted(pathOrRelative)) return pathOrRelative;
            return Path.GetFullPath(Path.Combine(baseDir, pathOrRelative));
        }

        private string GetPredTxtPath(string? seqPath, string imgPath)
        {
            if (string.IsNullOrWhiteSpace(seqPath)) return "";

            string fileName = Path.GetFileNameWithoutExtension(imgPath);
            string predDir = Path.Combine(seqPath, _cfg.PredFolder ?? "pred");
            return Path.Combine(predDir, fileName + ".txt");
        }
    }
}
