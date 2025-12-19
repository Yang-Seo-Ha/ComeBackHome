using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ComeBackHome
{
    public class LocalConfig
    {
        public string DatasetRoot { get; set; } = "";
        public string DefaultSequence { get; set; } = "";
        public int Fps { get; set; } = 30;
    }

    public partial class MainWindow : Window
    {
        private LocalConfig _cfg = new LocalConfig();

        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private List<string> _frames = new List<string>();
        private int _frameIndex = 0;
        private bool _isPlaying = false;

        public MainWindow()
        {
            InitializeComponent();

            TxtCctvStatus.Text = "Booting...";
            TxtHeaderTime.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            // 채널 선택 바뀌면 자동 로드 (먼저 걸어두는 게 더 안전)
            CmbChannel.SelectionChanged += (_, __) => PrepareFramesFromSelectedChannel();

            LoadLocalConfig();
            ScanChannelsToCombo();

            int fps = Math.Max(1, _cfg.Fps);
            _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
            _timer.Tick += (_, __) => ShowNextFrame();

        }

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
                    .Where(x => x.Name.StartsWith("H-", StringComparison.OrdinalIgnoreCase)
                             || x.Name.Contains("_UC-", StringComparison.OrdinalIgnoreCase))
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
                    if (hit != null)
                        CmbChannel.SelectedValue = hit.Full;
                    else
                        CmbChannel.SelectedIndex = 0;
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

        private void PrepareFramesFromSelectedChannel()
        {
            _timer.Stop();
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
                _timer.Stop();
                _isPlaying = false;
                TxtCctvStatus.Text = "Paused";
            }
            else
            {
                _timer.Start();
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

            string path = _frames[idx];

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();

            ImgCctv.Source = bmp;

            // 오버레이는 지금은 비워둠 (다음 단계: json 라벨 박스 그리기)
            OverlayCanvas.Children.Clear();

            TxtCctvStatus.Text = $"{Path.GetFileName(path)}  ({idx + 1}/{_frames.Count})";
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
                File.Copy(src, dlg.FileName, overwrite: true);
                MessageBox.Show("저장 완료!");
            }
        }

        private void BtnAck_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Ack 처리(예시). 나중에 경고 로그 상태 업데이트로 연결하면 됨.");
        }
    }
}
