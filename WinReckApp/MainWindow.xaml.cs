using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;

namespace WinReckApp
{
    public class FolderItem
    {
        public string FullPath { get; set; } = "";
        public string Name { get; set; } = "";
        public int AudioCount { get; set; }
    }

    public class RecordingItem
    {
        public string FilePath { get; set; } = "";
        public string Title { get; set; } = "";
        public string DurationStr { get; set; } = "0:00";
        public string DateStr { get; set; } = "";
        public string FileSizeStr { get; set; } = "";
        public TimeSpan Duration { get; set; }
        public float[] WaveformSamples { get; set; } = Array.Empty<float>();
    }

    public partial class MainWindow : Window
    {
        private bool _isExpanded = false;
        private string _recordingsFolder = "";
        private string _rootRecordingsFolder = "";
        private string _explorerFolder = "";
        private List<FolderItem> _subfolders = new List<FolderItem>();
        private string? _selectedSubfolderPath = null;
        private string _folderSearchQuery = "";
        private string _recordingSearchQuery = "";
        private List<RecordingItem> _recordings = new List<RecordingItem>();
        private RecordingItem? _selectedRecording = null;
        private string? _inlineEditingFilePath = null;

        // Batch / Multi-Select State
        private bool _isBatchSelectMode = false;
        private HashSet<string> _selectedFilePaths = new HashSet<string>();

        // Custom Modal State
        private Action? _modalConfirmAction = null;

        // Save Destination Picker State
        private bool _saveDirectlyToSelectedFolder = false;
        private string? _pendingSaveDestinationFolder = null;
        private byte[]? _pendingSaveData = null;

        // Native Recording Engine (Zero-Latency Pre-warmed)
        private WaveIn? _waveIn;
        private List<byte> _recordedBytes = new List<byte>();
        private DispatcherTimer _recTimer;
        private Stopwatch _recStopwatch = new Stopwatch();
        private bool _isRecording = false;
        private bool _isPaused = false;

        // Live Recording Waveform (Fixed Bars — No Twitching!)
        private readonly object _audioLock = new object();
        private List<float> _pendingLiveSamples = new List<float>();
        private List<float> _liveRecordedBars = new List<float>();

        // Live In-Memory Preview Playback
        private WaveOut? _previewWaveOut;
        private RawSourceWaveStream? _previewRawStream;
        private DispatcherTimer _previewTimer;
        private bool _isPreviewPlaying = false;
        private bool _isLiveScrubbing = false;

        // Library Playback Engine & Seekable Waveform
        private WaveOut? _libWaveOut;
        private AudioFileReader? _libAudioReader;
        private DispatcherTimer _libPlayTimer;
        private bool _isLibPlaying = false;
        private bool _isScrubbing = false;
        private TimeSpan _currentSeekPosition = TimeSpan.Zero;

        private readonly WaveFormat _studioFormat = new WaveFormat(48000, 16, 2);

        // Unified High-End Color Palette
        private static readonly Color CyanNeon       = Color.FromRgb(0, 229, 255);
        private static readonly Color PinkNeon       = Color.FromRgb(255, 42, 133);
        private static readonly Color SlateUnplayed  = Color.FromRgb(90, 100, 115);
        private static readonly Color RedRecording   = Color.FromRgb(230, 57, 70);

        public MainWindow()
        {
            InitializeComponent();

            _recordingsFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                "WinReck Recordings"
            );
            _rootRecordingsFolder = _recordingsFolder;
            _explorerFolder = _recordingsFolder;
            Directory.CreateDirectory(_recordingsFolder);

            // 50ms interval = 20 bars per second (smooth, rock-solid, fixed bars)
            _recTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
            _recTimer.Tick += RecTimer_Tick;

            _previewTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(25) };
            _previewTimer.Tick += PreviewTimer_Tick;

            _libPlayTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
            _libPlayTimer.Tick += LibPlayTimer_Tick;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            PopulateAudioDevices();
            InitPrewarmedMic();
            // NOTE: Do NOT call LoadRecordingsFromDisk() here — MainAppGrid is Collapsed,
            // so all canvas ActualWidth values are 0. We load after MorphSplashToMain().

            // Splash screen shows for 1.8s then morphs into the main window
            await System.Threading.Tasks.Task.Delay(1800);
            MorphSplashToMain();
        }

        private void MorphSplashToMain()
        {
            if (_isExpanded) return;
            _isExpanded = true;

            double targetWidth = 1120;
            double targetHeight = 640;

            this.ResizeMode = ResizeMode.CanResizeWithGrip;

            var ease = new QuarticEase { EasingMode = EasingMode.EaseInOut };

            double startLeft = this.Left;
            double startTop = this.Top;
            double targetLeft = Math.Max(20, startLeft - (targetWidth - this.Width) / 2);
            double targetTop = Math.Max(20, startTop - (targetHeight - this.Height) / 2);

            var animW = new DoubleAnimation(this.Width, targetWidth, TimeSpan.FromMilliseconds(650)) { EasingFunction = ease };
            var animH = new DoubleAnimation(this.Height, targetHeight, TimeSpan.FromMilliseconds(650)) { EasingFunction = ease };
            var animL = new DoubleAnimation(startLeft, targetLeft, TimeSpan.FromMilliseconds(650)) { EasingFunction = ease };
            var animT = new DoubleAnimation(startTop, targetTop, TimeSpan.FromMilliseconds(650)) { EasingFunction = ease };

            animW.Completed += (s, ev) =>
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    RedrawAllWaveforms();
                }));
            };

            this.BeginAnimation(Window.WidthProperty,  animW);
            this.BeginAnimation(Window.HeightProperty, animH);
            this.BeginAnimation(Window.LeftProperty,   animL);
            this.BeginAnimation(Window.TopProperty,    animT);

            var fadeSplash = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(400)) { BeginTime = TimeSpan.FromMilliseconds(150) };
            fadeSplash.Completed += (s, ev) => SplashGrid.Visibility = Visibility.Collapsed;

            var fadeInMain = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(500)) { BeginTime = TimeSpan.FromMilliseconds(320) };

            MainAppGrid.Visibility = Visibility.Visible;

            SplashGrid.BeginAnimation(UIElement.OpacityProperty, fadeSplash);
            MainAppGrid.BeginAnimation(UIElement.OpacityProperty, fadeInMain);

            // Initial load of recordings and empty states once the grid is attached
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                LoadRecordingsFromDisk(autoSelectFirst: false);
                DrawEmptyWaveform(LiveWaveformCanvas);
                DrawEmptyWaveform(PlayWaveformCanvas);
            }));
        }

        #region Device Enumeration & Pre-warming (Zero Cutoff)
        private void PopulateAudioDevices()
        {
            try
            {
                CmbMicrophone.Items.Clear();
                int micCount = WaveIn.DeviceCount;
                for (int i = 0; i < micCount; i++)
                    CmbMicrophone.Items.Add(WaveIn.GetCapabilities(i).ProductName);
                if (CmbMicrophone.Items.Count > 0) CmbMicrophone.SelectedIndex = 0;
                else CmbMicrophone.Items.Add("Default Microphone");

                CmbHeadphones.Items.Clear();
                int outCount = WaveOut.DeviceCount;
                for (int i = 0; i < outCount; i++)
                    CmbHeadphones.Items.Add(WaveOut.GetCapabilities(i).ProductName);
                if (CmbHeadphones.Items.Count > 0) CmbHeadphones.SelectedIndex = 0;
                else CmbHeadphones.Items.Add("Default Output");
            }
            catch
            {
                CmbMicrophone.Items.Add("Default Microphone"); CmbMicrophone.SelectedIndex = 0;
                CmbHeadphones.Items.Add("Default Output");     CmbHeadphones.SelectedIndex = 0;
            }
        }

        private void InitPrewarmedMic()
        {
            try
            {
                _waveIn?.StopRecording();
                _waveIn?.Dispose();

                int deviceNumber = CmbMicrophone.SelectedIndex >= 0 ? CmbMicrophone.SelectedIndex : 0;
                if (deviceNumber >= WaveIn.DeviceCount) deviceNumber = 0;

                _waveIn = new WaveIn
                {
                    DeviceNumber = deviceNumber,
                    WaveFormat = _studioFormat,
                    BufferMilliseconds = 20,
                    NumberOfBuffers = 3
                };

                _waveIn.DataAvailable += (s, a) =>
                {
                    if (_isRecording && !_isPaused && a.BytesRecorded > 0)
                    {
                        byte[] copy = new byte[a.BytesRecorded];
                        Buffer.BlockCopy(a.Buffer, 0, copy, 0, a.BytesRecorded);

                        lock (_recordedBytes)
                            _recordedBytes.AddRange(copy);

                        float maxSample = 0f;
                        double sumSq = 0;
                        int samples = 0;
                        for (int i = 0; i < a.BytesRecorded - 1; i += 2)
                        {
                            short sample = (short)((a.Buffer[i + 1] << 8) | a.Buffer[i]);
                            float val = Math.Abs(sample / 32768f);
                            if (val > maxSample) maxSample = val;
                            sumSq += val * val;
                            samples++;
                        }
                        float rms = samples > 0 ? (float)Math.Sqrt(sumSq / samples) : 0f;
                        float mixed = Math.Clamp(0.5f * maxSample + 0.5f * (rms * 2.2f), 0.02f, 1.0f);

                        lock (_audioLock)
                        {
                            _pendingLiveSamples.Add(mixed);
                        }
                    }
                };

                _waveIn.StartRecording();
            }
            catch { }
        }

        private void CmbMicrophone_SelectionChanged(object sender, SelectionChangedEventArgs e) => InitPrewarmedMic();

        private void CmbHeadphones_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLibPlaying && _selectedRecording != null)
            {
                var pos = _libAudioReader?.CurrentTime ?? TimeSpan.Zero;
                StopLibPlayback();
                StartLibPlayback(_selectedRecording, pos);
            }
        }
        #endregion

        #region Studio Recording Engine & Rock-Solid Waveform (No Twitching)
        private void BtnRecordAction_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRecording && !_isPaused)        StartRecording();
            else if (_isRecording && !_isPaused)    PauseRecording();
            else if (_isPaused)                     ResumeRecording();
        }

        private void StartRecording()
        {
            StopLibPlayback();
            StopLivePreview();

            InitPrewarmedMic();

            lock (_recordedBytes) _recordedBytes.Clear();
            lock (_audioLock)
            {
                _pendingLiveSamples.Clear();
                _liveRecordedBars.Clear();
            }

            _recStopwatch.Restart();
            _recTimer.Start();
            _isRecording = true;
            _isPaused = false;

            PausedActionControls.Visibility = Visibility.Collapsed;
            TxtRecStatus.Text = "Recording...";
            LiveRecIndicator.Fill = new SolidColorBrush(RedRecording);

            RecIconDot.Visibility = Visibility.Collapsed;
            PauseIconBars.Visibility = Visibility.Visible;
            ((Rectangle)PauseIconBars.Children[0]).Fill = new SolidColorBrush(RedRecording);
            ((Rectangle)PauseIconBars.Children[1]).Fill = new SolidColorBrush(RedRecording);
        }

        private void PauseRecording()
        {
            if (!_isRecording) return;
            _isPaused = true;
            _recStopwatch.Stop();
            _recTimer.Stop();

            TxtRecStatus.Text = "Paused";
            LiveRecIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 183, 3));

            PauseIconBars.Visibility = Visibility.Collapsed;
            RecIconDot.Visibility = Visibility.Visible;
            RecIconDot.Fill = new SolidColorBrush(RedRecording);

            PausedActionControls.Visibility = Visibility.Visible;

            // When paused: freeze into a stable whole-session scaled overview (no twitching)
            List<float> snapshot;
            lock (_audioLock) snapshot = new List<float>(_liveRecordedBars);
            DrawScaledSessionWaveform(LiveWaveformCanvas, snapshot, -1);
        }

        private void ResumeRecording()
        {
            StopLivePreview();
            _isPaused = false;
            _recStopwatch.Start();
            _recTimer.Start();

            PausedActionControls.Visibility = Visibility.Collapsed;
            TxtRecStatus.Text = "Recording...";
            LiveRecIndicator.Fill = new SolidColorBrush(RedRecording);

            RecIconDot.Visibility = Visibility.Collapsed;
            PauseIconBars.Visibility = Visibility.Visible;
        }

        private void BtnPreviewLive_Click(object sender, RoutedEventArgs e)
        {
            byte[] snapshot;
            lock (_recordedBytes) snapshot = _recordedBytes.ToArray();

            if (snapshot.Length == 0)
            {
                ShowAppModal("Preview", "No audio recorded yet.", () => { });
                return;
            }

            if (_isPreviewPlaying)
            {
                StopLivePreview();
                return;
            }

            try
            {
                StopLivePreview();
                var ms = new MemoryStream(snapshot);
                _previewRawStream = new RawSourceWaveStream(ms, _studioFormat);

                int outDev = CmbHeadphones.SelectedIndex >= 0 ? CmbHeadphones.SelectedIndex : 0;
                if (outDev >= WaveOut.DeviceCount) outDev = 0;

                _previewWaveOut = new WaveOut { DeviceNumber = outDev };
                _previewWaveOut.Init(_previewRawStream);
                _previewWaveOut.PlaybackStopped += (s, ev) => Dispatcher.Invoke(StopLivePreview);
                _previewWaveOut.Play();
                _isPreviewPlaying = true;
                _previewTimer.Start();
                BtnPreviewLive.Content = "Stop Preview";
            }
            catch (Exception ex)
            {
                ShowAppModal("Playback Error", ex.Message, null);
            }
        }

        private void PreviewTimer_Tick(object? sender, EventArgs e)
        {
            if (_previewRawStream == null || !_isPreviewPlaying) return;

            double progress = (double)_previewRawStream.Position / Math.Max(1, _previewRawStream.Length);
            var curr = _previewRawStream.CurrentTime;
            TxtRecTime.Text = $"{(int)curr.TotalMinutes}:{curr.Seconds:D2}";

            List<float> snapshot;
            lock (_audioLock) snapshot = new List<float>(_liveRecordedBars);
            DrawScaledSessionWaveform(LiveWaveformCanvas, snapshot, progress);
        }

        private void StopLivePreview()
        {
            _previewTimer.Stop();
            _isPreviewPlaying = false;
            BtnPreviewLive.Content = "Play Preview";

            _previewWaveOut?.Stop();
            _previewWaveOut?.Dispose();
            _previewWaveOut = null;

            _previewRawStream?.Dispose();
            _previewRawStream = null;

            // Restore recorded total time and full session overview
            var elapsed = _recStopwatch.Elapsed;
            TxtRecTime.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";

            List<float> snapshot;
            lock (_audioLock) snapshot = new List<float>(_liveRecordedBars);
            DrawScaledSessionWaveform(LiveWaveformCanvas, snapshot, -1);
        }

        private void BtnSaveRecording_Click(object sender, RoutedEventArgs e)
        {
            StopLivePreview();
            FinishRecordingSession(true);
        }

        private void BtnClearRecording_Click(object sender, RoutedEventArgs e)
        {
            ShowAppModal("Clear Recording?", "This will discard the current recording and reset the recorder.", () =>
            {
                ResetLiveRecording();
            });
        }

        private void ResetLiveRecording()
        {
            StopLivePreview();
            _recTimer.Stop();
            _recStopwatch.Reset();
            _isRecording = false;
            _isPaused = false;

            lock (_recordedBytes) _recordedBytes.Clear();
            lock (_audioLock)
            {
                _pendingLiveSamples.Clear();
                _liveRecordedBars.Clear();
            }

            // Flush driver buffer queue and pre-warm mic afresh
            InitPrewarmedMic();

            DrawEmptyWaveform(LiveWaveformCanvas);

            PausedActionControls.Visibility = Visibility.Collapsed;
            TxtRecStatus.Text = "Ready";
            LiveRecIndicator.Fill = new SolidColorBrush(Color.FromRgb(80, 80, 80));

            PauseIconBars.Visibility = Visibility.Collapsed;
            RecIconDot.Visibility = Visibility.Visible;
            RecIconDot.Fill = new SolidColorBrush(RedRecording);
            TxtRecTime.Text = "0:00";
        }

        private void FinishRecordingSession(bool save)
        {
            _recTimer.Stop();
            _recStopwatch.Stop();
            _isRecording = false;
            _isPaused = false;

            if (save)
            {
                byte[] data;
                lock (_recordedBytes) data = _recordedBytes.ToArray();

                if (data.Length > 0)
                {
                    // If user enabled the UI checkbox to save directly to the currently selected folder:
                    if (_saveDirectlyToSelectedFolder)
                    {
                        CommitSaveRecordingToFolder(_recordingsFolder, data);
                    }
                    else
                    {
                        // Ask where to save it from the main folder / available folders
                        ShowSaveDestinationModal(data);
                        return; // Modal will complete saving or cancel
                    }
                }
            }

            ResetLiveRecording();
        }

        private void CommitSaveRecordingToFolder(string targetFolder, byte[] data)
        {
            try
            {
                if (!Directory.Exists(targetFolder))
                    Directory.CreateDirectory(targetFolder);

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string finalPath = System.IO.Path.Combine(targetFolder, $"Recording_{timestamp}.wav");

                using (var writer = new WaveFileWriter(finalPath, _studioFormat))
                    writer.Write(data, 0, data.Length);

                _recordingsFolder = targetFolder;
                _selectedSubfolderPath = targetFolder;

                LoadRecordingsFromDisk();
                var saved = _recordings.FirstOrDefault(r => r.FilePath == finalPath);
                if (saved != null) SelectRecording(saved);
                AnimateRecordingsLoad();
            }
            catch (Exception ex)
            {
                ShowAppModal("Save Error", ex.Message, null);
            }
            finally
            {
                ResetLiveRecording();
            }
        }

        #region Live Waveform Seeking (Pause & Preview)
        private void LiveWaveformCanvas_Seek(object sender, MouseButtonEventArgs e)
        {
            if (!_isPaused && !_isPreviewPlaying) return;
            _isLiveScrubbing = true;
            LiveWaveformCanvas.CaptureMouse();
            SeekLivePreview(e.GetPosition(LiveWaveformCanvas));
        }

        private void LiveWaveformCanvas_DragSeek(object sender, MouseEventArgs e)
        {
            if (_isLiveScrubbing)
            {
                SeekLivePreview(e.GetPosition(LiveWaveformCanvas));
            }
        }

        private void LiveWaveformCanvas_SeekUp(object sender, MouseButtonEventArgs e)
        {
            if (_isLiveScrubbing)
            {
                _isLiveScrubbing = false;
                LiveWaveformCanvas.ReleaseMouseCapture();
            }
        }

        private void SeekLivePreview(Point pt)
        {
            byte[] snapshot;
            lock (_recordedBytes) snapshot = _recordedBytes.ToArray();
            if (snapshot.Length == 0) return;

            double w = Math.Max(LiveWaveformCanvas.ActualWidth, 100);
            double barW = 2.8, gap = 3.0;
            double step = barW + gap;
            int barCount = (int)(w / step);
            if (barCount < 30) barCount = 30;
            double startX = (w - barCount * step) / 2;
            double totalSpan = barCount * step;

            double progress = Math.Clamp((pt.X - startX) / totalSpan, 0.0, 1.0);

            if (_previewRawStream != null && _previewWaveOut != null)
            {
                long targetByte = (long)(progress * _previewRawStream.Length);
                targetByte = (targetByte / 4) * 4; // Align to 4-byte stereo 16-bit block
                _previewRawStream.Position = Math.Clamp(targetByte, 0, _previewRawStream.Length);

                var curr = _previewRawStream.CurrentTime;
                TxtRecTime.Text = $"{(int)curr.TotalMinutes}:{curr.Seconds:D2}";
            }
            else
            {
                // In paused state without preview playing
                double totalSecs = snapshot.Length / 192000.0;
                double targetSecs = progress * totalSecs;
                var ts = TimeSpan.FromSeconds(targetSecs);
                TxtRecTime.Text = $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
            }

            List<float> bars;
            lock (_audioLock) bars = new List<float>(_liveRecordedBars);
            DrawScaledSessionWaveform(LiveWaveformCanvas, bars, progress);
        }
        #endregion

        // Ticks every 50ms: appends exactly ONE fixed bar to history. Zero twitching!
        private void RecTimer_Tick(object? sender, EventArgs e)
        {
            var elapsed = _recStopwatch.Elapsed;
            TxtRecTime.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";

            if (_isRecording && !_isPaused)
            {
                float barVal = 0.02f;
                lock (_audioLock)
                {
                    if (_pendingLiveSamples.Count > 0)
                    {
                        float peak = 0;
                        double sum = 0;
                        for (int i = 0; i < _pendingLiveSamples.Count; i++)
                        {
                            float v = _pendingLiveSamples[i];
                            if (v > peak) peak = v;
                            sum += v;
                        }
                        float avg = (float)(sum / _pendingLiveSamples.Count);
                        barVal = Math.Clamp(0.6f * peak + 0.4f * avg, 0.02f, 1.0f);
                        _pendingLiveSamples.Clear();
                    }
                }

                _liveRecordedBars.Add(barVal);
                DrawLiveRecordingWaveform(LiveWaveformCanvas, _liveRecordedBars);
            }
        }
        #endregion

        #region Interactive Player & Seekable Waveform
        private void SelectRecording(RecordingItem item)
        {
            _selectedRecording = item;
            _currentSeekPosition = TimeSpan.Zero;
            StopLibPlayback();

            TxtPlayingTitle.Text = item.Title;
            TxtPlayingTitle.Foreground = Brushes.White;
            TxtPlayingDescription.Text = $"{item.DateStr}   •   {item.DurationStr}   •   {item.FileSizeStr}   •   48kHz WAV";
            TxtPlayingDescription.Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140));
            PlayingDotIndicator.Fill = new SolidColorBrush(CyanNeon);
            TxtPlayTime.Text = "0:00";

            RefreshRecordingsUI();
            DrawFileWaveform(PlayWaveformCanvas, item.WaveformSamples, 0);
        }

        private void ClearSelectedRecording()
        {
            _selectedRecording = null;
            _currentSeekPosition = TimeSpan.Zero;
            StopLibPlayback();

            TxtPlayingTitle.Text = "No audio loaded";
            TxtPlayingTitle.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
            TxtPlayingDescription.Text = "Select a recording to play";
            TxtPlayingDescription.Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85));
            PlayingDotIndicator.Fill = new SolidColorBrush(Color.FromRgb(85, 85, 85));
            TxtPlayTime.Text = "0:00";

            RefreshRecordingsUI();
            DrawEmptyWaveform(PlayWaveformCanvas);
        }

        private void BtnPlayToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedRecording == null)
            {
                if (_recordings.Count > 0) SelectRecording(_recordings[0]);
                else return;
            }

            if (_isLibPlaying)
            {
                PauseLibPlayback();
            }
            else
            {
                TimeSpan startAt = _currentSeekPosition;
                if (_libAudioReader != null)
                {
                    startAt = _libAudioReader.CurrentTime;
                }

                // If at the end, restart from 0
                if (startAt >= _selectedRecording!.Duration - TimeSpan.FromMilliseconds(150))
                {
                    startAt = TimeSpan.Zero;
                    _currentSeekPosition = TimeSpan.Zero;
                }

                StartLibPlayback(_selectedRecording!, startAt);
            }
        }

        private void StartLibPlayback(RecordingItem item, TimeSpan startAt)
        {
            try
            {
                if (!File.Exists(item.FilePath)) return;

                _currentSeekPosition = startAt;

                if (_libWaveOut != null && _libAudioReader != null && _selectedRecording == item)
                {
                    _libAudioReader.CurrentTime = startAt;
                    _libWaveOut.Play();
                    _libPlayTimer.Start();
                    _isLibPlaying = true;
                    UpdatePlaybackButtonUI(true);
                    return;
                }

                StopLibPlayback();

                _libAudioReader = new AudioFileReader(item.FilePath);
                if (startAt < _libAudioReader.TotalTime)
                    _libAudioReader.CurrentTime = startAt;

                int outDevice = CmbHeadphones.SelectedIndex >= 0 ? CmbHeadphones.SelectedIndex : 0;
                if (outDevice >= WaveOut.DeviceCount) outDevice = 0;

                _libWaveOut = new WaveOut { DeviceNumber = outDevice };
                _libWaveOut.Init(_libAudioReader);
                _libWaveOut.PlaybackStopped += (s, a) =>
                {
                    if (_isLibPlaying)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            StopLibPlayback();
                            _currentSeekPosition = TimeSpan.Zero;
                            TxtPlayTime.Text = "0:00";
                            DrawFileWaveform(PlayWaveformCanvas, item.WaveformSamples, 0);
                        });
                    }
                };

                _libWaveOut.Play();
                _libPlayTimer.Start();
                _isLibPlaying = true;
                UpdatePlaybackButtonUI(true);
            }
            catch (Exception ex)
            {
                ShowAppModal("Playback Error", ex.Message, null);
            }
        }

        private void PauseLibPlayback()
        {
            if (_libWaveOut != null && _isLibPlaying)
            {
                _libWaveOut.Pause();
                _libPlayTimer.Stop();
                _isLibPlaying = false;
                if (_libAudioReader != null)
                    _currentSeekPosition = _libAudioReader.CurrentTime;
                UpdatePlaybackButtonUI(false);
            }
        }

        private void StopLibPlayback()
        {
            _libPlayTimer.Stop();
            _isLibPlaying = false;
            UpdatePlaybackButtonUI(false);

            _libWaveOut?.Stop();
            _libWaveOut?.Dispose();
            _libWaveOut = null;

            _libAudioReader?.Dispose();
            _libAudioReader = null;
        }

        private void UpdatePlaybackButtonUI(bool isPlaying)
        {
            if (isPlaying)
            {
                TxtPlayIcon.Visibility = Visibility.Collapsed;
                PlayPauseBars.Visibility = Visibility.Visible;
            }
            else
            {
                PlayPauseBars.Visibility = Visibility.Collapsed;
                TxtPlayIcon.Visibility = Visibility.Visible;
            }
        }

        private void LibPlayTimer_Tick(object? sender, EventArgs e)
        {
            if (_libAudioReader == null || _selectedRecording == null || _isScrubbing) return;

            var curr = _libAudioReader.CurrentTime;
            _currentSeekPosition = curr;
            TxtPlayTime.Text = $"{(int)curr.TotalMinutes}:{curr.Seconds:D2}";

            double progress = _libAudioReader.TotalTime.TotalMilliseconds > 0
                ? curr.TotalMilliseconds / _libAudioReader.TotalTime.TotalMilliseconds
                : 0.0;

            DrawFileWaveform(PlayWaveformCanvas, _selectedRecording.WaveformSamples, progress);

            // Auto-follow playhead in scroll viewer if necessary
            double playheadX = progress * PlayWaveformCanvas.ActualWidth;
            double visibleRight = WaveformScrollViewer.HorizontalOffset + WaveformScrollViewer.ActualWidth;
            double visibleLeft = WaveformScrollViewer.HorizontalOffset;
            if (playheadX > visibleRight - 60 || playheadX < visibleLeft + 60)
            {
                double target = playheadX - WaveformScrollViewer.ActualWidth / 2;
                WaveformScrollViewer.ScrollToHorizontalOffset(Math.Max(0, target));
            }
        }

        // Seek on click / drag — Does NOT play immediately unless already playing
        private void PlayWaveformCanvas_Seek(object sender, MouseButtonEventArgs e)
        {
            _isScrubbing = true;
            PlayWaveformCanvas.CaptureMouse();
            SeekToPosition(e.GetPosition(PlayWaveformCanvas));
        }

        private void PlayWaveformCanvas_DragSeek(object sender, MouseEventArgs e)
        {
            if (_isScrubbing) SeekToPosition(e.GetPosition(PlayWaveformCanvas));
        }

        private void PlayWaveformCanvas_SeekUp(object sender, MouseButtonEventArgs e)
        {
            if (_isScrubbing)
            {
                _isScrubbing = false;
                PlayWaveformCanvas.ReleaseMouseCapture();
            }
        }

        private void SeekToPosition(Point pt)
        {
            if (_selectedRecording == null || PlayWaveformCanvas.ActualWidth <= 0) return;

            double w = PlayWaveformCanvas.ActualWidth > 80 ? PlayWaveformCanvas.ActualWidth : 560;
            double barW = 2.8, gap = 3.0;
            double step = barW + gap;
            int barCount = (int)(w / step);
            if (barCount < 30) barCount = 30;
            double startX = (w - barCount * step) / 2;
            double totalSpan = barCount * step;

            double progress = Math.Clamp((pt.X - startX) / totalSpan, 0.0, 1.0);
            TimeSpan targetPos = TimeSpan.FromMilliseconds(progress * _selectedRecording.Duration.TotalMilliseconds);

            _currentSeekPosition = targetPos;
            TxtPlayTime.Text = $"{(int)targetPos.TotalMinutes}:{targetPos.Seconds:D2}";
            DrawFileWaveform(PlayWaveformCanvas, _selectedRecording.WaveformSamples, progress);

            if (_isLibPlaying && _libAudioReader != null)
            {
                _libAudioReader.CurrentTime = targetPos;
            }
            // If NOT playing, DO NOT start playback! Wait until user presses play button.
        }

        // Scroll to pan horizontally (no zoom)
        private void WaveformScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            WaveformScrollViewer.ScrollToHorizontalOffset(
                WaveformScrollViewer.HorizontalOffset - e.Delta * 0.5);
        }

        private void BtnToggleSelectMode_Click(object sender, RoutedEventArgs e)
        {
            _isBatchSelectMode = !_isBatchSelectMode;
            if (_isBatchSelectMode)
            {
                BtnToggleSelectMode.Content = "Cancel";
                BtnSelectAll.Visibility = Visibility.Visible;
                BtnDeleteBatch.Visibility = Visibility.Visible;
                BtnDeleteBatch.Content = $"Delete ({_selectedFilePaths.Count})";
            }
            else
            {
                BtnToggleSelectMode.Content = "Select";
                BtnSelectAll.Visibility = Visibility.Collapsed;
                BtnDeleteBatch.Visibility = Visibility.Collapsed;
                _selectedFilePaths.Clear();
            }
            RefreshRecordingsUI();
        }

        private void ToggleItemSelection(string filePath)
        {
            if (_selectedFilePaths.Contains(filePath))
                _selectedFilePaths.Remove(filePath);
            else
                _selectedFilePaths.Add(filePath);

            BtnDeleteBatch.Content = $"Delete ({_selectedFilePaths.Count})";
            RefreshRecordingsUI();
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFilePaths.Count == _recordings.Count)
            {
                _selectedFilePaths.Clear();
            }
            else
            {
                _selectedFilePaths.Clear();
                foreach (var r in _recordings)
                    _selectedFilePaths.Add(r.FilePath);
            }
            BtnDeleteBatch.Content = $"Delete ({_selectedFilePaths.Count})";
            RefreshRecordingsUI();
        }

        private void BtnDeleteBatch_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFilePaths.Count == 0)
            {
                ShowAppModal("Delete Recordings", "No recordings selected. Select one or more recordings to delete.", null);
                return;
            }

            int count = _selectedFilePaths.Count;
            ShowAppModal("Confirm Delete", $"Permanently delete {count} selected recording{(count == 1 ? "" : "s")}?", () =>
            {
                StopLibPlayback();
                try
                {
                    foreach (var path in _selectedFilePaths)
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    _selectedFilePaths.Clear();
                    _isBatchSelectMode = false;
                    BtnToggleSelectMode.Content = "Select";
                    BtnSelectAll.Visibility = Visibility.Collapsed;
                    BtnDeleteBatch.Visibility = Visibility.Collapsed;
                    _selectedRecording = null;
                    LoadRecordingsFromDisk(autoSelectFirst: false);
                }
                catch (Exception ex)
                {
                    ShowAppModal("Delete Error", ex.Message, null);
                }
            });
        }

        private void DeleteSingleRecording(RecordingItem item)
        {
            ShowAppModal("Confirm Delete", $"Delete \"{item.Title}\"?", () =>
            {
                if (_selectedRecording?.FilePath == item.FilePath)
                {
                    StopLibPlayback();
                    _selectedRecording = null;
                }
                try
                {
                    if (File.Exists(item.FilePath))
                        File.Delete(item.FilePath);
                    _selectedFilePaths.Remove(item.FilePath);
                    LoadRecordingsFromDisk(autoSelectFirst: false);
                }
                catch (Exception ex)
                {
                    ShowAppModal("Error", $"Could not delete: {ex.Message}", null);
                }
            });
        }

        private void StartInlineRename(string filePath)
        {
            _inlineEditingFilePath = filePath;
            RefreshRecordingsUI();
        }

        private void CommitInlineRename(RecordingItem item, string newName)
        {
            _inlineEditingFilePath = null;
            newName = newName.Trim();
            if (string.IsNullOrEmpty(newName) || newName == item.Title)
            {
                RefreshRecordingsUI();
                return;
            }

            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                newName = newName.Replace(c.ToString(), "");

            string dir = System.IO.Path.GetDirectoryName(item.FilePath) ?? _recordingsFolder;
            string newPath = System.IO.Path.Combine(dir, newName + ".wav");
            try
            {
                StopLibPlayback();
                if (File.Exists(newPath))
                {
                    ShowAppModal("Error", "A file with that name already exists.", null);
                    RefreshRecordingsUI();
                    return;
                }
                File.Move(item.FilePath, newPath);
                LoadRecordingsFromDisk();
                var renamed = _recordings.FirstOrDefault(r => r.FilePath == newPath);
                if (renamed != null) SelectRecording(renamed);
            }
            catch (Exception ex)
            {
                ShowAppModal("Rename Error", ex.Message, null);
                RefreshRecordingsUI();
            }
        }
        #endregion

        #region Folders & Recordings Library (Explorer Grid Layout)
        private void LoadRecordingsFromDisk(bool autoSelectFirst = false)
        {
            if (!Directory.Exists(_explorerFolder))
            {
                _explorerFolder = _rootRecordingsFolder;
                Directory.CreateDirectory(_explorerFolder);
            }
            if (!Directory.Exists(_recordingsFolder))
            {
                _recordingsFolder = _explorerFolder;
            }

            // Scan subfolders of _explorerFolder
            _subfolders.Clear();
            try
            {
                var dirs = Directory.GetDirectories(_explorerFolder);
                foreach (var d in dirs)
                {
                    int audioCount = 0;
                    try
                    {
                        audioCount = Directory.GetFiles(d, "*.wav").Length + Directory.GetFiles(d, "*.mp3").Length;
                    }
                    catch { }

                    _subfolders.Add(new FolderItem
                    {
                        FullPath   = d,
                        Name       = System.IO.Path.GetFileName(d),
                        AudioCount = audioCount
                    });
                }
            }
            catch { }

            RefreshFoldersUI();

            LoadRecordingsFromFolder(_recordingsFolder, autoSelectFirst);
        }

        private void LoadRecordingsFromFolder(string folderPath, bool autoSelectFirst = false)
        {
            if (!Directory.Exists(folderPath)) return;
            _recordingsFolder = folderPath;

            // Update Header Subtitle ("in <FolderName>")
            UpdateRecordingsSubtitle();

            _recordings.Clear();

            // Scan Audio Files
            try
            {
                var wavFiles = Directory.GetFiles(_recordingsFolder, "*.wav");
                var mp3Files = Directory.GetFiles(_recordingsFolder, "*.mp3");
                var allFiles = wavFiles.Concat(mp3Files)
                                       .OrderByDescending(f => File.GetCreationTime(f))
                                       .ToList();

                foreach (var f in allFiles)
                {
                    try
                    {
                        using var reader = new AudioFileReader(f);
                        var dur   = reader.TotalTime;
                        var date  = File.GetCreationTime(f).ToString("dd-MM-yyyy");
                        var title = System.IO.Path.GetFileNameWithoutExtension(f);
                        float[] samples = GenerateWaveformSamples(reader, 320);

                        var fi = new FileInfo(f);
                        long bytes = fi.Length;
                        string sizeStr = bytes >= 1024 * 1024 
                            ? $"{(bytes / (1024.0 * 1024.0)):F1} MB" 
                            : $"{Math.Max(1, bytes / 1024)} KB";

                        _recordings.Add(new RecordingItem
                        {
                            FilePath        = f,
                            Title           = title,
                            Duration        = dur,
                            DurationStr     = $"{(int)dur.TotalMinutes}:{dur.Seconds:D2}",
                            DateStr         = date,
                            FileSizeStr     = sizeStr,
                            WaveformSamples = samples
                        });
                    }
                    catch { }
                }
            }
            catch { }

            if (_recordings.Count > 0 && autoSelectFirst && _selectedRecording == null)
            {
                SelectRecording(_recordings[0]);
            }
            else if (_selectedRecording != null)
            {
                var existing = _recordings.FirstOrDefault(r => r.FilePath == _selectedRecording.FilePath);
                if (existing != null)
                {
                    _selectedRecording = existing;
                    TxtPlayingTitle.Text = existing.Title;
                    TxtPlayingDescription.Text = $"{existing.DateStr}   •   {existing.DurationStr}   •   {existing.FileSizeStr}   •   48kHz WAV";
                }
                else
                {
                    ClearSelectedRecording();
                }
            }
            else
            {
                ClearSelectedRecording();
            }

            RefreshRecordingsUI();
        }

        private void UpdateRecordingsSubtitle()
        {
            bool isRoot = string.Equals(_recordingsFolder.TrimEnd('\\', '/'), _rootRecordingsFolder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            if (isRoot)
            {
                TxtRecordingsFolderSubtitle.Text = "in Main";
            }
            else
            {
                var folderName = System.IO.Path.GetFileName(_recordingsFolder);
                if (string.IsNullOrEmpty(folderName)) folderName = _recordingsFolder;
                TxtRecordingsFolderSubtitle.Text = $"in {folderName}";
            }
            TxtRecordingsFolderSubtitle.ToolTip = _recordingsFolder;
        }

        private void NavigateToFolder(string targetFolder)
        {
            if (!Directory.Exists(targetFolder)) return;
            _explorerFolder = targetFolder;
            _recordingsFolder = targetFolder;
            _selectedSubfolderPath = null;
            LoadRecordingsFromDisk(autoSelectFirst: false);
            AnimateRecordingsLoad();
        }

        private void AnimateRecordingsLoad()
        {
            try
            {
                var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                var slideUp = new DoubleAnimation(12.0, 0.0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                RecordingsScrollViewer.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                RecordingsTranslateTransform.BeginAnimation(TranslateTransform.YProperty, slideUp);
            }
            catch { }
        }

        private void RefreshFoldersUI()
        {
            FoldersWrapPanel.Children.Clear();

            bool isRoot = string.Equals(_explorerFolder.TrimEnd('\\', '/'), _rootRecordingsFolder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

            // If inside a subfolder in the explorer view, add a "Back" navigation card first
            if (!isRoot)
            {
                Border backCard = CreateFolderCard("⮤ Back", isSelected: false, isSpecialBack: true, clickAction: () =>
                {
                    try
                    {
                        var parent = Directory.GetParent(_explorerFolder);
                        string target = parent?.FullName ?? _rootRecordingsFolder;
                        if (!string.IsNullOrEmpty(_rootRecordingsFolder) && 
                            _explorerFolder.TrimEnd('\\', '/').Equals(_rootRecordingsFolder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        {
                            target = _rootRecordingsFolder;
                        }
                        NavigateToFolder(target);
                    }
                    catch
                    {
                        NavigateToFolder(_rootRecordingsFolder);
                    }
                });
                backCard.Tag = "BACK";
                FoldersWrapPanel.Children.Add(backCard);
            }

            var list = _subfolders;
            if (!string.IsNullOrWhiteSpace(_folderSearchQuery))
            {
                list = _subfolders.Where(f => f.Name.Contains(_folderSearchQuery, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (list.Count == 0 && isRoot)
            {
                Border mainCard = CreateFolderCard("Main", isSelected: true, clickAction: () =>
                {
                    UpdateCardSelection(_rootRecordingsFolder);
                    LoadRecordingsFromFolder(_rootRecordingsFolder);
                    AnimateRecordingsLoad();
                });
                mainCard.Tag = new FolderItem { FullPath = _rootRecordingsFolder, Name = "Main" };
                FoldersWrapPanel.Children.Add(mainCard);
                UpdateFolderScrollIndicator();
                return;
            }

            foreach (var folder in list)
            {
                bool isSelected = string.Equals(_selectedSubfolderPath, folder.FullPath, StringComparison.OrdinalIgnoreCase);

                bool hasSubfolders = false;
                try
                {
                    hasSubfolders = Directory.Exists(folder.FullPath) && Directory.GetDirectories(folder.FullPath).Length > 0;
                }
                catch { }

                Border folderCard = CreateFolderCard(folder.Name, isSelected: isSelected, clickAction: () =>
                {
                    // Single-click selects the folder
                    UpdateCardSelection(folder.FullPath);

                    // If it has NO subfolders, also load all audios in that folder down below right away
                    if (!hasSubfolders)
                    {
                        LoadRecordingsFromFolder(folder.FullPath);
                        AnimateRecordingsLoad();
                    }
                }, doubleClickAction: () =>
                {
                    if (hasSubfolders)
                    {
                        // Folder has subfolders -> open next explorer view
                        NavigateToFolder(folder.FullPath);
                    }
                    else
                    {
                        // Folder does NOT have subfolders:
                        // "the next view shld not open, instead it should just load all the audios in that folder down below, and show some visual indication that folder was selected"
                        UpdateCardSelection(folder.FullPath);
                        LoadRecordingsFromFolder(folder.FullPath);
                        AnimateRecordingsLoad();
                    }
                });

                folderCard.Tag = folder;
                FoldersWrapPanel.Children.Add(folderCard);
            }

            UpdateFolderScrollIndicator();
        }

        private Border CreateFolderCard(string name, bool isSelected = false, bool isSpecialBack = false, Action? clickAction = null, Action? doubleClickAction = null)
        {
            Border card = new Border
            {
                Width           = 74,
                Height          = 70,
                CornerRadius    = new CornerRadius(8),
                Padding         = new Thickness(4, 6, 4, 6),
                Margin          = new Thickness(0, 0, 8, 8),
                Cursor          = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1.0, 1.0)
            };

            StackPanel sp = new StackPanel
            {
                Orientation       = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };

            if (isSpecialBack)
            {
                Border backIcon = new Border
                {
                    Width = 36, Height = 22,
                    Background = new SolidColorBrush(Color.FromRgb(28, 34, 44)),
                    CornerRadius = new CornerRadius(4),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                backIcon.Child = new TextBlock
                {
                    Text = "⮤",
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(CyanNeon),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                sp.Children.Add(backIcon);
            }
            else
            {
                Grid folderGraphic = new Grid
                {
                    Width = 36, Height = 22,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                Border tab = new Border
                {
                    Tag = "FolderTab",
                    Width = 14, Height = 5,
                    CornerRadius = new CornerRadius(2, 2, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };
                Border body = new Border
                {
                    Tag = "FolderBody",
                    Width = 36, Height = 19,
                    CornerRadius = new CornerRadius(3),
                    VerticalAlignment = VerticalAlignment.Bottom
                };
                folderGraphic.Children.Add(tab);
                folderGraphic.Children.Add(body);
                sp.Children.Add(folderGraphic);
            }

            TextBlock nameBlock = new TextBlock
            {
                Tag               = "FolderNameBlock",
                Text              = name,
                FontSize          = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment     = TextAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
                MaxWidth          = 66,
                Margin            = new Thickness(0, 5, 0, 0)
            };
            sp.Children.Add(nameBlock);

            card.Child = sp;

            if (isSpecialBack)
            {
                ApplyBackCardStyle(card);
            }
            else
            {
                ApplyFolderCardStyle(card, isSelected);
            }

            card.MouseEnter += (s, e) =>
            {
                bool selected = (card.Tag is FolderItem fi) && string.Equals(_selectedSubfolderPath, fi.FullPath, StringComparison.OrdinalIgnoreCase);
                if (!selected && !isSpecialBack)
                {
                    card.Background = new SolidColorBrush(Color.FromRgb(25, 27, 34));
                    card.BorderBrush = new SolidColorBrush(Color.FromRgb(45, 48, 60));
                }
            };

            card.MouseLeave += (s, e) =>
            {
                bool selected = (card.Tag is FolderItem fi) && string.Equals(_selectedSubfolderPath, fi.FullPath, StringComparison.OrdinalIgnoreCase);
                if (!selected && !isSpecialBack)
                {
                    card.Background = new SolidColorBrush(Color.FromRgb(16, 16, 20));
                    card.BorderBrush = new SolidColorBrush(Color.FromRgb(26, 26, 32));
                }
            };

            card.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 1)
                {
                    clickAction?.Invoke();
                }
                else if (e.ClickCount >= 2)
                {
                    if (doubleClickAction != null) doubleClickAction.Invoke();
                    else clickAction?.Invoke();
                }
            };

            return card;
        }

        private void ApplyFolderCardStyle(Border card, bool isSelected)
        {
            if (isSelected)
            {
                // Clear, glowing visual indication for the selected folder
                card.Background      = new SolidColorBrush(Color.FromArgb(50, 0, 229, 255));
                card.BorderBrush     = new SolidColorBrush(CyanNeon);
                card.BorderThickness = new Thickness(1.5);
            }
            else
            {
                card.Background      = new SolidColorBrush(Color.FromRgb(16, 16, 20));
                card.BorderBrush     = new SolidColorBrush(Color.FromRgb(26, 26, 32));
                card.BorderThickness = new Thickness(1);
            }

            if (card.Child is StackPanel sp)
            {
                foreach (UIElement child in sp.Children)
                {
                    if (child is Grid folderGraphic)
                    {
                        foreach (UIElement part in folderGraphic.Children)
                        {
                            if (part is Border tab && Equals(tab.Tag, "FolderTab"))
                            {
                                tab.Background = isSelected 
                                    ? new SolidColorBrush(CyanNeon) 
                                    : new SolidColorBrush(Color.FromRgb(84, 160, 224));
                            }
                            else if (part is Border body && Equals(body.Tag, "FolderBody"))
                            {
                                body.Background = isSelected 
                                    ? new SolidColorBrush(Color.FromRgb(0, 195, 230)) 
                                    : new SolidColorBrush(Color.FromRgb(96, 169, 232));
                            }
                        }
                    }
                    else if (child is TextBlock nameBlock && Equals(nameBlock.Tag, "FolderNameBlock"))
                    {
                        nameBlock.Foreground = isSelected 
                            ? new SolidColorBrush(CyanNeon) 
                            : new SolidColorBrush(Color.FromRgb(215, 215, 215));
                        nameBlock.FontWeight = isSelected ? FontWeights.Bold : FontWeights.Normal;
                    }
                }
            }
        }

        private void ApplyBackCardStyle(Border card)
        {
            card.Background      = new SolidColorBrush(Color.FromRgb(16, 16, 20));
            card.BorderBrush     = new SolidColorBrush(Color.FromRgb(26, 26, 32));
            card.BorderThickness = new Thickness(1);

            if (card.Child is StackPanel sp)
            {
                foreach (UIElement child in sp.Children)
                {
                    if (child is TextBlock nameBlock && Equals(nameBlock.Tag, "FolderNameBlock"))
                    {
                        nameBlock.Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 190));
                        nameBlock.FontWeight = FontWeights.Normal;
                    }
                }
            }
        }

        private void PulseCardAnimation(Border card)
        {
            try
            {
                if (card.RenderTransform is ScaleTransform scale)
                {
                    var anim = new DoubleAnimation(0.92, 1.0, TimeSpan.FromMilliseconds(160))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                }
            }
            catch { }
        }

        private void UpdateCardSelection(string? selectedPath)
        {
            _selectedSubfolderPath = selectedPath;
            foreach (UIElement child in FoldersWrapPanel.Children)
            {
                if (child is Border card)
                {
                    if (card.Tag is FolderItem folder)
                    {
                        bool isSelected = string.Equals(_selectedSubfolderPath, folder.FullPath, StringComparison.OrdinalIgnoreCase);
                        ApplyFolderCardStyle(card, isSelected);
                        if (isSelected)
                        {
                            PulseCardAnimation(card);
                        }
                    }
                    else if (Equals(card.Tag, "BACK"))
                    {
                        ApplyBackCardStyle(card);
                    }
                }
            }
        }

        #region Expandable Search Handlers
        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                Border? parentBorder = (tb == TxtFolderSearch) ? FolderSearchBorder : RecordingSearchBorder;
                if (parentBorder != null)
                {
                    var anim = new DoubleAnimation(150, TimeSpan.FromMilliseconds(200))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    parentBorder.BeginAnimation(FrameworkElement.WidthProperty, anim);
                    parentBorder.BorderBrush = new SolidColorBrush(CyanNeon);
                }
            }
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                Border? parentBorder = (tb == TxtFolderSearch) ? FolderSearchBorder : RecordingSearchBorder;
                if (parentBorder != null)
                {
                    if (string.IsNullOrEmpty(tb.Text))
                    {
                        var anim = new DoubleAnimation(85, TimeSpan.FromMilliseconds(200))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        };
                        parentBorder.BeginAnimation(FrameworkElement.WidthProperty, anim);
                    }
                    parentBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(37, 37, 43));
                }
            }
        }

        private void TxtFolderSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _folderSearchQuery = TxtFolderSearch.Text.Trim();
            TxtFolderSearchPlaceholder.Visibility = string.IsNullOrEmpty(TxtFolderSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
            BtnClearFolderSearch.Visibility = string.IsNullOrEmpty(TxtFolderSearch.Text) ? Visibility.Collapsed : Visibility.Visible;
            RefreshFoldersUI();
        }

        private void BtnClearFolderSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtFolderSearch.Text = "";
            TxtFolderSearch.Focus();
        }

        private void TxtRecordingSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _recordingSearchQuery = TxtRecordingSearch.Text.Trim();
            TxtRecordingSearchPlaceholder.Visibility = string.IsNullOrEmpty(TxtRecordingSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
            BtnClearRecordingSearch.Visibility = string.IsNullOrEmpty(TxtRecordingSearch.Text) ? Visibility.Collapsed : Visibility.Visible;
            RefreshRecordingsUI();
        }

        private void BtnClearRecordingSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtRecordingSearch.Text = "";
            TxtRecordingSearch.Focus();
        }
        #endregion

        #region Folder Cards Row Scrolling & Indicator
        private void UpdateFolderScrollIndicator()
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (FoldersScrollViewer == null || FolderScrollIndicator == null) return;

                double scrollable = FoldersScrollViewer.ScrollableHeight;
                if (scrollable <= 1.0)
                {
                    FolderScrollIndicator.Visibility = Visibility.Collapsed;
                    return;
                }

                FolderScrollIndicator.Visibility = Visibility.Visible;
                double offset = FoldersScrollViewer.VerticalOffset;
                bool atBottom = offset >= (scrollable - 2.0);

                if (TxtScrollArrow != null)
                {
                    TxtScrollArrow.Text = atBottom ? "▴" : "▾";
                }
                if (TxtScrollIndicatorText != null)
                {
                    TxtScrollIndicatorText.Text = atBottom ? "scroll up" : "scroll";
                }
            }));
        }

        private void FoldersScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            const double rowHeight = 78.0;
            double currentOffset = FoldersScrollViewer.VerticalOffset;
            double targetOffset = e.Delta < 0 ? currentOffset + rowHeight : currentOffset - rowHeight;
            double snappedOffset = Math.Round(targetOffset / rowHeight) * rowHeight;
            double clampedOffset = Math.Clamp(snappedOffset, 0, FoldersScrollViewer.ScrollableHeight);
            FoldersScrollViewer.ScrollToVerticalOffset(clampedOffset);
        }

        private void FoldersScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            UpdateFolderScrollIndicator();
        }

        private void FolderScrollIndicator_Click(object sender, MouseButtonEventArgs e)
        {
            const double rowHeight = 78.0;
            bool atBottom = FoldersScrollViewer.VerticalOffset >= (FoldersScrollViewer.ScrollableHeight - 2.0);
            double currentOffset = FoldersScrollViewer.VerticalOffset;
            double targetOffset = atBottom ? currentOffset - rowHeight : currentOffset + rowHeight;
            double snappedOffset = Math.Round(targetOffset / rowHeight) * rowHeight;
            double clampedOffset = Math.Clamp(snappedOffset, 0, FoldersScrollViewer.ScrollableHeight);
            FoldersScrollViewer.ScrollToVerticalOffset(clampedOffset);
        }
        #endregion

        // Generates a well-normalized, naturally dynamic waveform thumbnail.
        // Key insight: each 4096-byte buffer (~5ms of audio) becomes one raw data point.
        // We then bucket + normalize so the loudest moment = 1.0, silence = near 0.
        // This produces the same natural varied heights as the live recording graph.
        private float[] GenerateWaveformSamples(AudioFileReader reader, int count)
        {
            float[] result = new float[count];
            reader.Position = 0;

            bool isFloat32 = (reader.WaveFormat.BitsPerSample == 32) || 
                             (reader.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat);
            int bytesPerSample = isFloat32 ? 4 : 2;

            byte[] byteBuf = new byte[4096];
            List<float> rawChunks = new List<float>(8192);
            int read;

            while ((read = reader.Read(byteBuf, 0, byteBuf.Length)) > 0)
            {
                float maxSample = 0;
                double sumSq = 0;
                int sampleCount = 0;

                if (isFloat32)
                {
                    for (int i = 0; i + 3 < read; i += 4)
                    {
                        float a = Math.Abs(BitConverter.ToSingle(byteBuf, i));
                        if (float.IsNaN(a) || float.IsInfinity(a)) continue;
                        if (a > maxSample) maxSample = a;
                        sumSq += a * a;
                        sampleCount++;
                    }
                }
                else
                {
                    for (int i = 0; i + 1 < read; i += 2)
                    {
                        short s = (short)((byteBuf[i + 1] << 8) | byteBuf[i]);
                        float a = Math.Abs(s / 32768f);
                        if (a > maxSample) maxSample = a;
                        sumSq += a * a;
                        sampleCount++;
                    }
                }

                float rms = sampleCount > 0 ? (float)Math.Sqrt(sumSq / sampleCount) : 0f;
                // Blend peak and RMS — exactly like the live recorder engine
                float chunkVal = Math.Clamp(0.5f * maxSample + 0.5f * (rms * 2.2f), 0.02f, 1.0f);
                rawChunks.Add(chunkVal);
            }

            if (rawChunks.Count == 0) return result;

            // Bucket the raw chunks into `count` bars
            double bucketSize = (double)rawChunks.Count / count;
            for (int i = 0; i < count; i++)
            {
                int start = (int)(i * bucketSize);
                int end   = Math.Min(rawChunks.Count, (int)((i + 1) * bucketSize));
                if (end <= start) end = Math.Min(rawChunks.Count, start + 1);

                float peak = 0;
                double sum = 0;
                int cnt = 0;
                for (int j = start; j < end; j++)
                {
                    float v = rawChunks[j];
                    if (v > peak) peak = v;
                    sum += v;
                    cnt++;
                }
                float avg = cnt > 0 ? (float)(sum / cnt) : 0f;
                result[i] = 0.55f * peak + 0.45f * avg;
            }

            // Normalize so the highest peak fills to 1.0, preserving natural dynamics
            float maxVal = 0.001f;
            for (int i = 0; i < count; i++)
                if (result[i] > maxVal) maxVal = result[i];

            float norm = 1.0f / maxVal;
            for (int i = 0; i < count; i++)
                result[i] = Math.Clamp(result[i] * norm, 0.02f, 1.0f);

            return result;
        }

        private void RefreshRecordingsUI()
        {
            RecordingsStackPanel.Children.Clear();

            var displayList = _recordings;
            if (!string.IsNullOrWhiteSpace(_recordingSearchQuery))
            {
                displayList = _recordings.Where(r => r.Title.Contains(_recordingSearchQuery, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (displayList.Count == 0)
            {
                var emptyPanel = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 40, 0, 0)
                };
                emptyPanel.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(_recordingSearchQuery) ? "No recordings in this folder" : "No matches found",
                    FontSize = 13,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 6)
                });
                emptyPanel.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(_recordingSearchQuery) ? "Click ● to record audio" : "Try a different search query",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                RecordingsStackPanel.Children.Add(emptyPanel);
                return;
            }

            foreach (var item in displayList)
            {
                bool isSelected = (_selectedRecording != null && _selectedRecording.FilePath == item.FilePath);
                bool isEditing  = (_inlineEditingFilePath == item.FilePath);
                bool isChecked  = _selectedFilePaths.Contains(item.FilePath);

                Border rowBorder = new Border
                {
                    Margin          = new Thickness(0, 2, 0, 2),
                    CornerRadius    = new CornerRadius(8),
                    Padding         = new Thickness(8, 7, 8, 7),
                    Background      = isChecked
                        ? new SolidColorBrush(Color.FromArgb(50, 230, 57, 70))
                        : (isSelected ? new SolidColorBrush(Color.FromArgb(50, 0, 212, 255)) : Brushes.Transparent),
                    BorderBrush     = isChecked
                        ? new SolidColorBrush(Color.FromArgb(100, 230, 57, 70))
                        : (isSelected ? new SolidColorBrush(Color.FromArgb(80, 0, 212, 255)) : Brushes.Transparent),
                    BorderThickness = new Thickness(1),
                    Cursor          = Cursors.Hand
                };

                Grid row = new Grid();
                if (_isBatchSelectMode)
                {
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });

                    Border chk = new Border
                    {
                        Width = 14,
                        Height = 14,
                        CornerRadius = new CornerRadius(3),
                        BorderBrush = new SolidColorBrush(isChecked ? Color.FromRgb(255, 77, 77) : Color.FromRgb(90, 90, 90)),
                        BorderThickness = new Thickness(1.5),
                        Background = isChecked ? new SolidColorBrush(Color.FromArgb(70, 255, 77, 77)) : Brushes.Transparent,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    if (isChecked)
                    {
                        chk.Child = new TextBlock
                        {
                            Text = "✓",
                            FontSize = 9,
                            FontWeight = FontWeights.Bold,
                            Foreground = Brushes.White,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, -2, 0, 0)
                        };
                    }
                    Grid.SetColumn(chk, 0);
                    row.Children.Add(chk);
                }
                else
                {
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                }

                // Left: dot + title / edit box
                StackPanel leftSp = new StackPanel
                {
                    Orientation       = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Ellipse dot = new Ellipse
                {
                    Width  = 6,
                    Height = 6,
                    Margin = new Thickness(0, 0, 9, 0),
                    Fill   = isSelected
                        ? new SolidColorBrush(CyanNeon)
                        : new SolidColorBrush(Color.FromRgb(210, 50, 60))
                };
                leftSp.Children.Add(dot);

                if (isEditing)
                {
                    TextBox editBox = new TextBox
                    {
                        Text            = item.Title,
                        FontSize        = 12,
                        Foreground      = Brushes.White,
                        Background      = new SolidColorBrush(Color.FromRgb(22, 22, 22)),
                        BorderBrush     = new SolidColorBrush(Color.FromArgb(120, 0, 212, 255)),
                        BorderThickness = new Thickness(1),
                        Padding         = new Thickness(5, 2, 5, 2),
                        MinWidth        = 140
                    };

                    editBox.KeyDown += (s, ke) =>
                    {
                        if (ke.Key == Key.Enter)       CommitInlineRename(item, editBox.Text);
                        else if (ke.Key == Key.Escape) { _inlineEditingFilePath = null; RefreshRecordingsUI(); }
                    };
                    editBox.LostFocus += (s, lfe) => CommitInlineRename(item, editBox.Text);

                    leftSp.Children.Add(editBox);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        editBox.Focus();
                        editBox.SelectAll();
                    }), DispatcherPriority.Input);
                }
                else
                {
                    TextBlock title = new TextBlock
                    {
                        Text              = item.Title,
                        FontSize          = 12,
                        Foreground        = isSelected ? Brushes.White : new SolidColorBrush(Color.FromRgb(170, 170, 170)),
                        TextTrimming      = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    leftSp.Children.Add(title);
                }
                Grid.SetColumn(leftSp, _isBatchSelectMode ? 1 : 0);
                row.Children.Add(leftSp);

                TextBlock dur = new TextBlock
                {
                    Text                = item.DurationStr,
                    FontSize            = 11,
                    Foreground          = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment   = VerticalAlignment.Center
                };
                Grid.SetColumn(dur, _isBatchSelectMode ? 2 : 1);
                row.Children.Add(dur);

                TextBlock dt = new TextBlock
                {
                    Text                = item.DateStr,
                    FontSize            = 10,
                    Foreground          = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Margin              = new Thickness(0, 0, 2, 0)
                };
                Grid.SetColumn(dt, _isBatchSelectMode ? 3 : 2);
                row.Children.Add(dt);

                if (!_isBatchSelectMode)
                {
                    TextBlock btnDeleteIcon = new TextBlock
                    {
                        Text                = "✕",
                        FontSize            = 11,
                        Foreground          = new SolidColorBrush(Color.FromRgb(160, 60, 60)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Cursor              = Cursors.Hand,
                        Opacity             = 0.35,
                        ToolTip             = "Delete recording"
                    };
                    btnDeleteIcon.MouseEnter += (s, ev) =>
                    {
                        ((TextBlock)s).Opacity = 1.0;
                        ((TextBlock)s).Foreground = new SolidColorBrush(Color.FromRgb(255, 77, 77));
                    };
                    btnDeleteIcon.MouseLeave += (s, ev) =>
                    {
                        ((TextBlock)s).Opacity = 0.35;
                        ((TextBlock)s).Foreground = new SolidColorBrush(Color.FromRgb(160, 60, 60));
                    };
                    btnDeleteIcon.MouseLeftButtonDown += (s, ev) =>
                    {
                        ev.Handled = true;
                        DeleteSingleRecording(item);
                    };
                    Grid.SetColumn(btnDeleteIcon, 3);
                    row.Children.Add(btnDeleteIcon);

                    ContextMenu cm = new ContextMenu
                    {
                        Background  = new SolidColorBrush(Color.FromRgb(18, 18, 20)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(40, 40, 45))
                    };
                    MenuItem miRename = new MenuItem { Header = "Rename", Foreground = Brushes.White, FontSize = 11 };
                    miRename.Click += (s, ev) => StartInlineRename(item.FilePath);
                    MenuItem miDelete = new MenuItem { Header = "Delete", Foreground = new SolidColorBrush(Color.FromRgb(255, 77, 77)), FontSize = 11 };
                    miDelete.Click += (s, ev) => DeleteSingleRecording(item);
                    cm.Items.Add(miRename);
                    cm.Items.Add(miDelete);
                    rowBorder.ContextMenu = cm;
                }

                rowBorder.Child = row;

                rowBorder.MouseLeftButtonDown += (s, ev) =>
                {
                    if (isEditing) return;
                    if (_isBatchSelectMode)
                    {
                        ToggleItemSelection(item.FilePath);
                        return;
                    }
                    if (ev.ClickCount == 1)      SelectRecording(item);
                    else if (ev.ClickCount == 2) StartInlineRename(item.FilePath);
                };

                // Hover effect
                rowBorder.MouseEnter += (s, ev) =>
                {
                    if (!isSelected && !isChecked)
                        ((Border)s).Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
                };
                rowBorder.MouseLeave += (s, ev) =>
                {
                    if (!isSelected && !isChecked)
                        ((Border)s).Background = Brushes.Transparent;
                };

                RecordingsStackPanel.Children.Add(rowBorder);
            }
        }
        #endregion

        #region App-Themed Modal Dialog
        private void ShowAppModal(string title, string message, Action? onConfirm)
        {
            TxtModalTitle.Text   = title;
            TxtModalMessage.Text = message;
            _modalConfirmAction  = onConfirm;
            CustomModalOverlay.Visibility = Visibility.Visible;
        }

        private void BtnModalConfirm_Click(object sender, RoutedEventArgs e)
        {
            CustomModalOverlay.Visibility = Visibility.Collapsed;
            _modalConfirmAction?.Invoke();
        }

        private void BtnModalCancel_Click(object sender, RoutedEventArgs e)
        {
            CustomModalOverlay.Visibility = Visibility.Collapsed;
        }

        #region Save Destination Folder Picker Modal
        private void ShowSaveDestinationModal(byte[] data)
        {
            _pendingSaveData = data;
            _pendingSaveDestinationFolder = _recordingsFolder; // default to currently selected folder

            PopulateSaveDestinationsList();

            SaveDestinationModalOverlay.Visibility = Visibility.Visible;
        }

        private void ToggleDirectSaveOption_Click(object sender, MouseButtonEventArgs e)
        {
            _saveDirectlyToSelectedFolder = !_saveDirectlyToSelectedFolder;
            UpdateDirectSaveCheckboxUI();
        }

        private void UpdateDirectSaveCheckboxUI()
        {
            if (_saveDirectlyToSelectedFolder)
            {
                DirectSaveCheckBox.Background = new SolidColorBrush(Color.FromArgb(60, 0, 229, 255));
                DirectSaveCheckBox.BorderBrush = new SolidColorBrush(CyanNeon);
                DirectSaveFolderIcon.Opacity = 1.0;
                DirectSaveBadgeDot.Visibility = Visibility.Visible;
                DirectSaveCheckBox.ToolTip = "Direct save enabled: saves directly into selected folder";
            }
            else
            {
                DirectSaveCheckBox.Background = new SolidColorBrush(Color.FromRgb(20, 20, 24));
                DirectSaveCheckBox.BorderBrush = new SolidColorBrush(Color.FromRgb(44, 44, 56));
                DirectSaveFolderIcon.Opacity = 0.55;
                DirectSaveBadgeDot.Visibility = Visibility.Collapsed;
                DirectSaveCheckBox.ToolTip = "Click to save directly to selected folder (no folder picker prompt)";
            }
        }

        private void PopulateSaveDestinationsList()
        {
            SaveDestinationsStackPanel.Children.Clear();

            // Collect unique candidate folders starting from Main
            var candidates = new List<(string Name, string Path)>();
            candidates.Add(("Main (Root)", _rootRecordingsFolder));

            try
            {
                if (Directory.Exists(_rootRecordingsFolder))
                {
                    var subdirs = Directory.GetDirectories(_rootRecordingsFolder, "*", SearchOption.AllDirectories);
                    foreach (var d in subdirs)
                    {
                        string relative = System.IO.Path.GetRelativePath(_rootRecordingsFolder, d);
                        candidates.Add((relative, d));
                    }
                }
            }
            catch { }

            // If current recordings folder isn't in candidates, add it
            if (!candidates.Any(c => string.Equals(c.Path, _recordingsFolder, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add((System.IO.Path.GetFileName(_recordingsFolder), _recordingsFolder));
            }

            foreach (var item in candidates)
            {
                bool isSelected = string.Equals(_pendingSaveDestinationFolder, item.Path, StringComparison.OrdinalIgnoreCase);

                var row = new Border
                {
                    CornerRadius    = new CornerRadius(6),
                    Padding         = new Thickness(10, 8, 10, 8),
                    Margin          = new Thickness(0, 0, 0, 4),
                    Cursor          = Cursors.Hand,
                    Background      = isSelected ? new SolidColorBrush(Color.FromArgb(45, 0, 229, 255)) : new SolidColorBrush(Color.FromRgb(18, 18, 24)),
                    BorderBrush     = isSelected ? new SolidColorBrush(CyanNeon) : new SolidColorBrush(Color.FromRgb(30, 30, 38)),
                    BorderThickness = new Thickness(isSelected ? 1.5 : 1),
                    Tag             = item.Path
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

                var icon = new TextBlock
                {
                    Text = "📁",
                    FontSize = 13,
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(icon, 0);

                var txtName = new TextBlock
                {
                    Text = item.Name,
                    FontSize = 12,
                    Foreground = isSelected ? new SolidColorBrush(CyanNeon) : new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                    FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(txtName, 1);

                if (isSelected)
                {
                    var badge = new TextBlock
                    {
                        Text = "selected",
                        FontSize = 10,
                        Foreground = new SolidColorBrush(CyanNeon),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    Grid.SetColumn(badge, 2);
                    grid.Children.Add(badge);
                }

                grid.Children.Add(icon);
                grid.Children.Add(txtName);
                row.Child = grid;

                row.MouseLeftButtonDown += (s, e) =>
                {
                    _pendingSaveDestinationFolder = item.Path;
                    PopulateSaveDestinationsList();
                };

                SaveDestinationsStackPanel.Children.Add(row);
            }
        }

        private void BtnSaveModalConfirm_Click(object sender, RoutedEventArgs e)
        {
            SaveDestinationModalOverlay.Visibility = Visibility.Collapsed;

            string target = _pendingSaveDestinationFolder ?? _recordingsFolder;
            if (_pendingSaveData != null && _pendingSaveData.Length > 0)
            {
                CommitSaveRecordingToFolder(target, _pendingSaveData);
            }
            _pendingSaveData = null;
        }

        private void BtnSaveModalCancel_Click(object sender, RoutedEventArgs e)
        {
            SaveDestinationModalOverlay.Visibility = Visibility.Collapsed;
            _pendingSaveData = null;
            ResetLiveRecording();
        }
        #endregion
        #endregion

        #region Unified Mirrored Waveform Rendering (Shared Design System)

        // Draw symmetric solid pill-shaped bar with rounded caps (connected, seamless from top to bottom)
        private static void AddMirroredBar(Canvas canvas, double x, double centerY,
            double halfH, double barW, Color color, double alpha)
        {
            byte a = (byte)Math.Clamp(alpha * 255, 10, 255);
            var brush = new SolidColorBrush(Color.FromArgb(a, color.R, color.G, color.B));

            double totalHeight = Math.Max(barW, halfH * 2.0);

            // Single continuous bar from top to bottom — zero middle gap or seam
            Rectangle bar = new Rectangle
            {
                Width   = barW,
                Height  = totalHeight,
                Fill    = brush,
                RadiusX = barW / 2.0,
                RadiusY = barW / 2.0
            };
            Canvas.SetLeft(bar, x - barW / 2.0);
            Canvas.SetTop(bar,  centerY - totalHeight / 2.0);
            canvas.Children.Add(bar);
        }

        private void DrawEmptyWaveform(Canvas canvas)
        {
            canvas.Children.Clear();
            double w = 0;
            if (canvas == PlayWaveformCanvas)
            {
                w = WaveformScrollViewer.ActualWidth > 80 ? WaveformScrollViewer.ActualWidth : 560;
                canvas.Width = w;
            }
            else
            {
                w = canvas.ActualWidth > 80 ? canvas.ActualWidth : 560;
            }

            double h = canvas.ActualHeight > 30 ? canvas.ActualHeight : 120;
            double centerY = h / 2.0;

            // Subtle resting baseline connecting the grid during silence
            canvas.Children.Add(new Line
            {
                X1 = 0, X2 = w,
                Y1 = centerY, Y2 = centerY,
                Stroke = new SolidColorBrush(Color.FromArgb(40, 90, 100, 115)),
                StrokeThickness = 1.0
            });
        }

        // 1. LIVE RECORDING WAVEFORM:
        // Bars are completely FIXED! Each committed bar never twitches, jumps, or resizes.
        // Fills left-to-right, then smoothly scrolls left as new bars appear.
        private void DrawLiveRecordingWaveform(Canvas canvas, List<float> bars)
        {
            canvas.Children.Clear();
            double w = Math.Max(canvas.ActualWidth, 100);
            double h = Math.Max(canvas.ActualHeight, 80);
            if (w < 10 || h < 10) return;
            double centerY = h / 2.0;

            // Subtle red baseline behind bars that connects silent gaps
            canvas.Children.Add(new Line
            {
                X1 = 0, X2 = w,
                Y1 = centerY, Y2 = centerY,
                Stroke = new SolidColorBrush(Color.FromArgb(35, 230, 57, 70)),
                StrokeThickness = 1.0
            });

            double barW = 2.8, gap = 3.0;
            double step = barW + gap;
            int barCount = (int)(w / step);
            if (barCount < 30) barCount = 30;
            double maxHalfH = h * 0.42;

            int totalBars = bars.Count;

            if (totalBars <= barCount)
            {
                // Filling left-to-right: each bar remains in its exact fixed slot, no fake baseline dots
                for (int i = 0; i < totalBars; i++)
                {
                    double x = i * step + barW / 2 + (w - barCount * step) / 2;
                    float amp = bars[i];
                    if (amp < 0.04f) continue;
                    double halfH = Math.Pow(amp, 1.2) * maxHalfH;
                    if (halfH < 1.5) continue;
                    Color barColor = (amp > 0.75f) ? Color.FromRgb(255, 215, 225) : RedRecording;
                    AddMirroredBar(canvas, x, centerY, halfH, barW, barColor, 0.90);
                }
            }
            else
            {
                // Full tape scrolling: bars slide left smoothly without twitching
                int startIdx = totalBars - barCount;
                for (int i = 0; i < barCount; i++)
                {
                    double x = i * step + barW / 2 + (w - barCount * step) / 2;
                    float amp = bars[startIdx + i];
                    if (amp < 0.04f) continue;
                    double halfH = Math.Pow(amp, 1.2) * maxHalfH;
                    if (halfH < 1.5) continue;
                    Color barColor = (amp > 0.75f) ? Color.FromRgb(255, 215, 225) : RedRecording;
                    AddMirroredBar(canvas, x, centerY, halfH, barW, barColor, 0.90);
                }
            }
        }

        // 2. SCALED SESSION WAVEFORM (Paused & Live Preview):
        // Shows the whole recording scaled across the canvas. Drawn stationary when paused.
        private void DrawScaledSessionWaveform(Canvas canvas, List<float> bars, double previewProgress = -1)
        {
            canvas.Children.Clear();
            double w = Math.Max(canvas.ActualWidth, 100);
            double h = Math.Max(canvas.ActualHeight, 80);
            if (w < 10 || h < 10) return;
            double centerY = h / 2.0;

            // Subtle baseline connecting silent gaps
            canvas.Children.Add(new Line
            {
                X1 = 0, X2 = w,
                Y1 = centerY, Y2 = centerY,
                Stroke = new SolidColorBrush(Color.FromArgb(40, 90, 100, 115)),
                StrokeThickness = 1.0
            });

            double barW = 2.8, gap = 3.0;
            double step = barW + gap;
            int barCount = (int)(w / step);
            if (barCount < 30) barCount = 30;
            double maxHalfH = h * 0.42;

            int totalBars = bars.Count;
            if (totalBars == 0)
            {
                DrawEmptyWaveform(canvas);
                return;
            }

            double startX = (w - barCount * step) / 2;
            double totalSpan = barCount * step;
            double cursorX = (previewProgress >= 0) ? Math.Clamp(startX + previewProgress * totalSpan, startX, startX + totalSpan) : -1;

            for (int i = 0; i < barCount; i++)
            {
                double x = startX + i * step + barW / 2;
                float amp = 0f;

                if (totalBars <= barCount)
                {
                    if (i < totalBars) amp = bars[i];
                    else continue;
                }
                else
                {
                    int startIdx = (int)((double)i * totalBars / barCount);
                    int endIdx = (int)((double)(i + 1) * totalBars / barCount);
                    endIdx = Math.Clamp(endIdx, startIdx + 1, totalBars);

                    float bMax = 0f;
                    for (int j = startIdx; j < endIdx; j++)
                        if (bars[j] > bMax) bMax = bars[j];
                    amp = bMax;
                }

                if (amp < 0.04f) continue;
                double halfH = Math.Pow(amp, 1.2) * maxHalfH;
                if (halfH < 1.5) continue;

                Color barColor;
                double alpha;

                if (previewProgress >= 0)
                {
                    bool isPreviewPlayed = (x <= cursorX + 0.5);
                    barColor = isPreviewPlayed ? CyanNeon : SlateUnplayed;
                    alpha = isPreviewPlayed ? 0.95 : 0.40;
                }
                else
                {
                    barColor = (amp > 0.75f) ? Color.FromRgb(255, 215, 225) : RedRecording;
                    alpha = 0.90;
                }

                AddMirroredBar(canvas, x, centerY, halfH, barW, barColor, alpha);
            }

            if (previewProgress >= 0 && cursorX >= 0)
            {
                // Live preview playhead
                canvas.Children.Add(new Line
                {
                    X1 = cursorX, X2 = cursorX,
                    Y1 = 2, Y2 = h - 2,
                    Stroke = new SolidColorBrush(CyanNeon),
                    StrokeThickness = 2.0
                });
                canvas.Children.Add(new Polygon
                {
                    Points = new PointCollection
                    {
                        new Point(cursorX - 5, 1),
                        new Point(cursorX + 5, 1),
                        new Point(cursorX, 9)
                    },
                    Fill = new SolidColorBrush(CyanNeon)
                });
            }
        }

        // 3. BOTTOM PLAYER WAVEFORM:
        // Uses the exact same design system as the top graph:
        // - Identical bar width (2.8px), gap (3.0px), pill radius (barW / 2)
        // - Identical breathing room (maxHalfH = h * 0.42)
        // - Identical natural power curve (Math.Pow(amp, 1.2))
        // - Electric Cyan (#00E5FF) for played audio, soft slate for unplayed
        // - Subtle Neon Pink (#FF2A85) accent ONLY on extreme peaks (> 0.82)
        private void DrawFileWaveform(Canvas canvas, float[] samples, double progress)
        {
            canvas.Children.Clear();

            double w = WaveformScrollViewer.ActualWidth > 80 ? WaveformScrollViewer.ActualWidth : 560;
            canvas.Width = w;

            double h = canvas.ActualHeight > 30 ? canvas.ActualHeight : 140;
            double centerY = h / 2.0;

            double barW = 2.8, gap = 3.0;
            double step = barW + gap;
            int barCount = (int)(w / step);
            if (barCount < 30) barCount = 30;
            double maxHalfH = h * 0.42;

            double startX = (w - barCount * step) / 2;
            double totalSpan = barCount * step;
            double cursorX = Math.Clamp(startX + progress * totalSpan, startX, startX + totalSpan);

            // Subtle baseline connecting gaps during silence (drawn behind bars)
            if (cursorX > 0)
            {
                canvas.Children.Add(new Line
                {
                    X1 = 0, X2 = cursorX,
                    Y1 = centerY, Y2 = centerY,
                    Stroke = new SolidColorBrush(Color.FromArgb(70, 0, 229, 255)),
                    StrokeThickness = 1.0
                });
            }
            if (cursorX < w)
            {
                canvas.Children.Add(new Line
                {
                    X1 = cursorX, X2 = w,
                    Y1 = centerY, Y2 = centerY,
                    Stroke = new SolidColorBrush(Color.FromArgb(45, 90, 100, 115)),
                    StrokeThickness = 1.0
                });
            }

            for (int i = 0; i < barCount; i++)
            {
                double x = startX + i * step + barW / 2;
                double barProg = (double)i / barCount;
                bool isPast = (x <= cursorX + 0.5);

                float amp = 0f;
                if (samples.Length > 0)
                {
                    double sPos = barProg * (samples.Length - 1);
                    int sIdx0 = (int)sPos;
                    int sIdx1 = Math.Min(samples.Length - 1, sIdx0 + 1);
                    double frac = sPos - sIdx0;
                    amp = (float)((1.0 - frac) * samples[sIdx0] + frac * samples[sIdx1]);
                }

                // If silent or quiet pause, don't draw any dot/bar — eliminates the middle horizontal line completely!
                if (amp < 0.04f) continue;

                double halfH = Math.Pow(amp, 1.25) * maxHalfH;
                if (halfH < 1.5) continue;

                Color barColor;
                double alpha;

                if (isPast)
                {
                    // Played segment: Clean Electric Cyan, with Neon Pink highlight only on true peak spikes
                    if (amp > 0.82f)
                    {
                        barColor = PinkNeon;
                        alpha = 1.0;
                    }
                    else
                    {
                        barColor = CyanNeon;
                        alpha = 0.95;
                    }
                }
                else
                {
                    // Unplayed segment: Soft, elegant slate tone
                    barColor = SlateUnplayed;
                    alpha = 0.50;
                }

                AddMirroredBar(canvas, x, centerY, halfH, barW, barColor, alpha);
            }

            // Playhead Glow Line
            Line playheadGlow = new Line
            {
                X1 = cursorX, X2 = cursorX,
                Y1 = 2, Y2 = h - 2,
                Stroke = new SolidColorBrush(Color.FromArgb(90, 0, 229, 255)),
                StrokeThickness = 4.0
            };
            canvas.Children.Add(playheadGlow);

            // Playhead Crisp Core Line
            Line playhead = new Line
            {
                X1 = cursorX, X2 = cursorX,
                Y1 = 2, Y2 = h - 2,
                Stroke = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                StrokeThickness = 1.6
            };
            canvas.Children.Add(playhead);

            // Playhead Top Diamond Marker
            Polygon playheadCap = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(cursorX - 5, 1),
                    new Point(cursorX + 5, 1),
                    new Point(cursorX, 9)
                },
                Fill = new SolidColorBrush(CyanNeon)
            };
            canvas.Children.Add(playheadCap);
        }

        private void LiveWaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_isRecording && !_isPaused)
            {
                DrawEmptyWaveform(LiveWaveformCanvas);
            }
            else if (_isPaused)
            {
                List<float> snapshot;
                lock (_audioLock) snapshot = new List<float>(_liveRecordedBars);
                DrawScaledSessionWaveform(LiveWaveformCanvas, snapshot, -1);
            }
            else
            {
                List<float> snapshot;
                lock (_audioLock) snapshot = new List<float>(_liveRecordedBars);
                DrawLiveRecordingWaveform(LiveWaveformCanvas, snapshot);
            }
        }

        private void WaveformScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RedrawPlayWaveform();
        }

        private void PlayWaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RedrawPlayWaveform();
        }

        private void RedrawPlayWaveform()
        {
            if (_selectedRecording != null)
            {
                double prog = (_selectedRecording.Duration.TotalMilliseconds > 0)
                    ? _currentSeekPosition.TotalMilliseconds / _selectedRecording.Duration.TotalMilliseconds
                    : 0;
                DrawFileWaveform(PlayWaveformCanvas, _selectedRecording.WaveformSamples, prog);
            }
            else
            {
                DrawEmptyWaveform(PlayWaveformCanvas);
            }
        }

        private void RedrawAllWaveforms()
        {
            if (!_isRecording && !_isPaused)
            {
                DrawEmptyWaveform(LiveWaveformCanvas);
            }
            else if (_isPaused)
            {
                List<float> snapshot;
                lock (_audioLock) snapshot = new List<float>(_liveRecordedBars);
                DrawScaledSessionWaveform(LiveWaveformCanvas, snapshot, -1);
            }
            else
            {
                List<float> snapshot;
                lock (_audioLock) snapshot = new List<float>(_liveRecordedBars);
                DrawLiveRecordingWaveform(LiveWaveformCanvas, snapshot);
            }

            RedrawPlayWaveform();
        }
        #endregion

        #region Window Actions
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) this.DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)  => this.WindowState = WindowState.Minimized;
        private void BtnMaximize_Click(object sender, RoutedEventArgs e)  => this.WindowState = (this.WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            _waveIn?.StopRecording();
            _waveIn?.Dispose();
            StopLivePreview();
            StopLibPlayback();
            this.Close();
        }

        private void GitHubLink_Click(object sender, MouseButtonEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo { FileName = "https://github.com", UseShellExecute = true }); }
            catch { }
        }

        private void BtnLoadFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title            = "Select Recordings Folder",
                InitialDirectory = _explorerFolder
            };
            if (dialog.ShowDialog() == true)
            {
                _rootRecordingsFolder = dialog.FolderName;
                _explorerFolder = dialog.FolderName;
                _recordingsFolder = dialog.FolderName;
                _selectedSubfolderPath = null;
                LoadRecordingsFromDisk();
                AnimateRecordingsLoad();
            }
        }
        #endregion
    }
}
