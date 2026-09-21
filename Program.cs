using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace SongYuDesktopPetV2
{
    internal static class AppDataPaths
    {
        public static readonly string DirectoryPath = ResolveDirectory();

        private static string ResolveDirectory()
        {
            var preferred = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SongYuDesktopPet");
            try
            {
                Directory.CreateDirectory(preferred);
                var probe = Path.Combine(preferred, "write-test.tmp");
                using (var stream = new FileStream(probe, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
                try { File.Delete(probe); } catch { }
                return preferred;
            }
            catch
            {
                var fallback = Path.Combine(AppContext.BaseDirectory, "user-data");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }
    }

    internal static class Program
    {
        private const string ShowEventName = "SongYuDesktopPet_V3_ShowEvent";

        [STAThread]
        private static void Main()
        {
            try { RunApplication(); }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppDataPaths.DirectoryPath, "crash-v3.log"), ex.ToString(), Encoding.UTF8);
                }
                catch { }
                try { MessageBox.Show(ex.Message, "宋玉桌宠 V3 启动失败", MessageBoxButton.OK, MessageBoxImage.Error); }
                catch { }
            }
        }

        private static void RunApplication()
        {
            var appDataDir = AppDataPaths.DirectoryPath;
            FileStream instanceLock = null;
            try
            {
                instanceLock = new FileStream(Path.Combine(appDataDir, "v3-running.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch
            {
                try
                {
                    using (var existingEvent = EventWaitHandle.OpenExisting(ShowEventName)) existingEvent.Set();
                }
                catch { }
                return;
            }

            using (instanceLock)
            {
                bool eventCreated;
                using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out eventCreated))
                {
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new PetWindow();
            app.MainWindow = window;
                    var listener = new Thread(delegate()
                    {
                        while (true)
                        {
                            try
                            {
                                showEvent.WaitOne();
                                app.Dispatcher.BeginInvoke(new Action(delegate { window.RestoreFromExternalRequest(); }));
                            }
                            catch { return; }
                        }
                    });
                    listener.IsBackground = true;
                    listener.Start();
            app.Run(window);
                }
            }
        }
    }

    internal sealed class PetWindow : Window
    {
        private const double BaseWidth = 330.0;
        private readonly string _baseDir = AppContext.BaseDirectory;
        private readonly string _settingsPath;
        private readonly string _dialoguesPath;
        private readonly Dictionary<string, List<BitmapImage>> _spriteFrames = new Dictionary<string, List<BitmapImage>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[]> _dialogues = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _customDialogues = new List<string>();
        private readonly Random _random = new Random();
        private readonly Grid _floatingLayer;
        private readonly Image _spriteImage;
        private readonly Border _speechBubble;
        private readonly TextBlock _speechText;
        private readonly DispatcherTimer _speechTimer;
        private readonly DispatcherTimer _stateTimer;
        private readonly DispatcherTimer _autoTimer;
        private readonly DispatcherTimer _reminderTimer;
        private readonly DispatcherTimer _presenceTimer;
        private readonly DispatcherTimer _frameTimer;
        private readonly ScaleTransform _pressScale;

        private Storyboard _motionStoryboard;
        private Forms.NotifyIcon _trayIcon;
        private string _state = "idle";
        private string _dockedState;
        private int _frameIndex;
        private double _scale = 1.0;
        private bool _animationEnabled = true;
        private bool _randomActivities = true;
        private bool _speechEnabled = true;
        private bool _remindersEnabled;
        private bool _idleFadeEnabled = true;
        private bool _systemNotifications = true;
        private bool _loadedPosition;
        private bool _wasSystemIdle;
        private bool _isIdleFaded;
        private int _idleFadeSeconds = 8;
        private double _baseOpacity = 1.0;
        private DateTime _nextAutoAction;
        private DateTime _lastPetActivity = DateTime.Now;
        private Drawing.Point _dragStart;
        private HwndSource _windowSource;
        private const int HotkeyId = 0x534F;
        private const int WmHotkey = 0x0312;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint VkH = 0x48;

        public PetWindow()
        {
            var settingsDir = AppDataPaths.DirectoryPath;
            _settingsPath = Path.Combine(settingsDir, "settings-v3.ini");
            _dialoguesPath = Path.Combine(settingsDir, "custom-dialogues.txt");
            ConfigureWindow();
            BuildDialogues();
            LoadCustomDialogues();
            LoadSprites();

            _floatingLayer = new Grid
            {
                Background = Brushes.Transparent,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };

            _spriteImage = new Image
            {
                Source = _spriteFrames["idle"][0],
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(_spriteImage, BitmapScalingMode.NearestNeighbor);
            _pressScale = new ScaleTransform(1, 1);
            _spriteImage.RenderTransform = _pressScale;
            _spriteImage.RenderTransformOrigin = new Point(0.5, 0.75);
            _floatingLayer.Children.Add(_spriteImage);

            _speechText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(38, 57, 67)),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 180
            };
            _speechBubble = new Border
            {
                Child = _speechText,
                Background = new SolidColorBrush(Color.FromArgb(242, 247, 253, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(85, 191, 221)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(13),
                Padding = new Thickness(11, 8, 11, 8),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 7, 3, 0),
                Opacity = 0,
                IsHitTestVisible = false
            };
            _floatingLayer.Children.Add(_speechBubble);

            Content = _floatingLayer;

            _speechTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.3) };
            _speechTimer.Tick += delegate { HideSpeech(); };
            _stateTimer = new DispatcherTimer();
            _stateTimer.Tick += delegate { _stateTimer.Stop(); ReturnToRestingState(); };
            _frameTimer = new DispatcherTimer();
            _frameTimer.Tick += OnFrameTick;
            _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _autoTimer.Tick += OnAutoTick;
            _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(45) };
            _reminderTimer.Tick += delegate
            {
                SetState("tea", 6, "修行有度。起来走走，也喝口水吧。");
                if (_systemNotifications && _trayIcon != null)
                    _trayIcon.ShowBalloonTip(5000, "宋玉提醒", "已经专注很久了，起来走走、喝口水吧。", Forms.ToolTipIcon.Info);
            };
            _presenceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _presenceTimer.Tick += OnPresenceTick;

            LoadSettings();
            BuildContextMenu();
            ApplyScale();
            ResetNextAutoAction();

            Loaded += OnLoaded;
            Closing += OnClosing;
            MouseLeftButtonDown += OnMouseLeftButtonDown;
            MouseWheel += OnMouseWheel;
            MouseMove += delegate { MarkPetActivity(); };
            MouseEnter += delegate { MarkPetActivity(); };
        }

        private void ConfigureWindow()
        {
            Title = "宋玉桌宠 V3";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.Manual;
            UseLayoutRounding = true;
        }

        private void BuildDialogues()
        {
            _dialogues["idle"] = new[]
            {
                "道友，今日也要稳健修行呀。", "白凤峰上，清风正好。", "莫急，机缘总会来的。",
                "要不要歇一会儿？", "宋玉在此，陪你修行。", "今日诸事顺遂。"
            };
            _dialogues["head"] = new[]
            {
                "发饰可不能弄乱啦。", "道友……这是在做什么？", "轻一点，我知道你在。", "这支白凤簪，好看吗？"
            };
            _dialogues["sleeve"] = new[]
            {
                "袖中可没有藏着灵石。", "这身衣裳，是白凤峰的颜色。", "别扯衣袖呀。", "清风入袖，倒也自在。"
            };
            _dialogues["happy"] = new[]
            {
                "见到你，我很高兴。", "今日心境澄明。", "修行之外，也该有些欢喜。", "道友真有趣。"
            };
            _dialogues["surprise"] = new[]
            {
                "呀！", "你突然出现，吓我一跳。", "道友，下次先唤我一声。", "我可一直看着呢。"
            };
            _dialogues["tea"] = new[]
            {
                "清茶一盏，可静心神。", "道友也喝一口吧。", "慢一些，今日不必太赶。", "茶香正好。"
            };
            _dialogues["meditate"] = new[]
            {
                "凝神静气，抱元守一。", "我先运转一个周天。", "心静，方能见真。", "道友可愿一同打坐？"
            };
            _dialogues["sleep"] = new[]
            {
                "唔……只歇一会儿。", "夜深了，道友也早些休息。", "白凤峰的风，很安静……", "我没有睡着，只是在入定。"
            };
            _dialogues["shy"] = new[]
            {
                "别这样看着我……", "道友莫要取笑。", "我、我只是有些热。", "先让我藏一会儿。"
            };
            _dialogues["wave"] = new[]
            {
                "道友，又见面了。", "今日安好？", "宋玉有礼了。", "我在这里。"
            };
        }

        private void LoadCustomDialogues()
        {
            _customDialogues.Clear();
            try
            {
                if (!File.Exists(_dialoguesPath)) return;
                foreach (var line in File.ReadAllLines(_dialoguesPath, Encoding.UTF8))
                {
                    var text = line.Trim();
                    if (text.Length > 0 && !_customDialogues.Contains(text)) _customDialogues.Add(text);
                }
            }
            catch { }
        }

        private string CustomDialoguesText()
        {
            return string.Join(Environment.NewLine, _customDialogues.ToArray());
        }

        private void SaveCustomDialogues(string text)
        {
            try
            {
                File.WriteAllText(_dialoguesPath, text ?? string.Empty, Encoding.UTF8);
                LoadCustomDialogues();
            }
            catch { }
        }

        private void LoadSprites()
        {
            var assets = Path.Combine(_baseDir, "assets");
            LoadSpriteFrames("idle", assets, "idle.png", "idle_2.png");
            LoadSpriteFrames("wave", assets, "wave.png", "wave_2.png");
            LoadSpriteFrames("meditate", assets, "meditate.png", "meditate_2.png");
            LoadSpriteFrames("sleep", assets, "sleep.png", "sleep_2.png");
            LoadSpriteFrames("surprise", assets, "surprise.png", "surprise_2.png");
            LoadSpriteFrames("tea", assets, "tea.png", "tea_2.png");
            LoadSpriteFrames("shy", assets, "shy.png", "shy_2.png");
            LoadSpriteFrames("happy", assets, "happy.png", "happy_2.png");
            LoadSpriteFrames("edge_left", assets, "edge_left.png");
            LoadSpriteFrames("edge_right", assets, "edge_right.png");
            LoadSpriteFrames("edge_top", assets, "edge_top.png");
            LoadSpriteFrames("edge_bottom", assets, "edge_bottom.png");
        }

        private void LoadSpriteFrames(string key, string assets, params string[] fileNames)
        {
            var frames = new List<BitmapImage>();
            foreach (var fileName in fileNames)
            {
                var path = Path.Combine(assets, fileName);
                if (!File.Exists(path))
                    throw new FileNotFoundException("缺少动作素材：" + fileName, path);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                frames.Add(bitmap);
            }
            _spriteFrames[key] = frames;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_loadedPosition || !PositionIsVisible(Left, Top)) ResetPosition();
            SetState("wave", 3.5, "道友，又见面了。");
            _autoTimer.Start();
            _presenceTimer.Start();
            if (_remindersEnabled) _reminderTimer.Start();
            CreateTrayIcon();
            RegisterVisibilityHotkey();
        }

        public void RestoreFromExternalRequest()
        {
            var preferredTopmost = Topmost;
            Show();
            WindowState = WindowState.Normal;
            Topmost = true;
            Activate();
            Topmost = preferredTopmost;
            SetState("wave", 3.5, "我已经在这里啦。");
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (e.OriginalSource is Button) return;
            MarkPetActivity();

            if (e.ClickCount >= 2)
            {
                SetState("wave", 4, RandomLine("wave"));
                return;
            }

            var point = e.GetPosition(this);
            _dragStart = Forms.Cursor.Position;
            _dockedState = null;
            SetState("surprise", 0, null);
            PressSprite();
            try { DragMove(); } catch { }
            ReleaseSprite();
            var end = Forms.Cursor.Position;
            var moved = Math.Abs(end.X - _dragStart.X) + Math.Abs(end.Y - _dragStart.Y) >= 8;
            if (moved)
            {
                var edgeState = SnapToNearestEdge();
                if (edgeState != null)
                {
                    _dockedState = edgeState;
                    SetState(edgeState, 0, EdgeLine(edgeState));
                }
                else SetState("happy", 2.2, "御风而行，也不过如此。");
                return;
            }

            var ratio = ActualHeight <= 0 ? 0.5 : point.Y / ActualHeight;
            if (ratio < 0.42) SetState("shy", 3.8, RandomLine("head"));
            else if (ratio > 0.72) SetState("surprise", 3.2, RandomLine("sleeve"));
            else SetState("happy", 3.8, RandomLine("happy"));
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            MarkPetActivity();
            _scale += e.Delta > 0 ? 0.1 : -0.1;
            _scale = Math.Max(0.55, Math.Min(1.8, _scale));
            ApplyScale();
        }

        private void PressSprite()
        {
            _pressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _pressScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _pressScale.ScaleX = 0.965;
            _pressScale.ScaleY = 0.965;
        }

        private void ReleaseSprite()
        {
            var ease = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
            var x = new DoubleAnimation(_pressScale.ScaleX, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease };
            var y = new DoubleAnimation(_pressScale.ScaleY, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease };
            _pressScale.BeginAnimation(ScaleTransform.ScaleXProperty, x);
            _pressScale.BeginAnimation(ScaleTransform.ScaleYProperty, y);
        }

        private string SnapToNearestEdge()
        {
            var screen = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
            const double threshold = 90;
            var right = screen.Right - Width;
            var bottom = screen.Bottom - Height;
            var distances = new[]
            {
                Math.Abs(Left - screen.Left), Math.Abs(Left - right),
                Math.Abs(Top - screen.Top), Math.Abs(Top - bottom)
            };
            var nearest = 0;
            for (var i = 1; i < distances.Length; i++) if (distances[i] < distances[nearest]) nearest = i;
            string edgeState = null;
            if (distances[nearest] <= threshold)
            {
                if (nearest == 0) { Left = screen.Left; edgeState = "edge_left"; }
                else if (nearest == 1) { Left = right; edgeState = "edge_right"; }
                else if (nearest == 2) { Top = screen.Top; edgeState = "edge_top"; }
                else { Top = bottom; edgeState = "edge_bottom"; }
            }
            Left = Math.Max(screen.Left, Math.Min(right, Left));
            Top = Math.Max(screen.Top, Math.Min(bottom, Top));
            return edgeState;
        }

        private string EdgeLine(string state)
        {
            if (state == "edge_left") return "我在屏幕左边看看。";
            if (state == "edge_right") return "这边视野不错。";
            if (state == "edge_top") return "登高些，灵气也清明。";
            return "暂且在这里坐一会儿。";
        }

        private void MarkPetActivity()
        {
            _lastPetActivity = DateTime.Now;
            if (_isIdleFaded)
            {
                _isIdleFaded = false;
                AnimateWindowOpacity(_baseOpacity);
            }
        }

        private void OnPresenceTick(object sender, EventArgs e)
        {
            if (!_idleFadeEnabled)
            {
                if (_isIdleFaded) { _isIdleFaded = false; AnimateWindowOpacity(_baseOpacity); }
                return;
            }
            if (!_isIdleFaded && (DateTime.Now - _lastPetActivity).TotalSeconds >= _idleFadeSeconds)
            {
                _isIdleFaded = true;
                AnimateWindowOpacity(Math.Min(0.48, _baseOpacity));
            }
        }

        private void AnimateWindowOpacity(double target)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, target, TimeSpan.FromMilliseconds(240)));
        }

        private void OnAutoTick(object sender, EventArgs e)
        {
            var idleSeconds = GetSystemIdleSeconds();
            if (idleSeconds >= 120)
            {
                if (!_wasSystemIdle)
                {
                    _wasSystemIdle = true;
                    SetState("sleep", 0, DateTime.Now.Hour >= 22 || DateTime.Now.Hour < 7 ? "夜深了，道友也早些休息。" : "你忙吧，我先歇一会儿。");
                }
                return;
            }

            if (_wasSystemIdle)
            {
                _wasSystemIdle = false;
                SetState("wave", 4, "你回来啦。");
                ResetNextAutoAction();
                return;
            }

            if (!_randomActivities || _dockedState != null || DateTime.Now < _nextAutoAction || _state != "idle") return;
            var choices = new[] { "tea", "meditate", "wave", "happy" };
            var action = choices[_random.Next(choices.Length)];
            SetState(action, action == "meditate" ? 8 : 5, _random.Next(3) == 0 ? RandomLine(action) : null);
            ResetNextAutoAction();
        }

        private void ResetNextAutoAction()
        {
            _nextAutoAction = DateTime.Now.AddSeconds(_random.Next(28, 56));
        }

        private void SetState(string state, double seconds, string speech)
        {
            if (!_spriteFrames.ContainsKey(state)) state = "idle";
            _stateTimer.Stop();
            _state = state;
            _frameIndex = 0;
            _spriteImage.Source = _spriteFrames[state][0];
            StartFrameAnimation();
            StartMotionForState();
            if (!string.IsNullOrEmpty(speech)) ShowSpeech(speech);
            if (seconds > 0)
            {
                _stateTimer.Interval = TimeSpan.FromSeconds(seconds);
                _stateTimer.Start();
            }
        }

        private void ReturnToRestingState()
        {
            SetState(_dockedState ?? "idle", 0, null);
        }

        private void StartFrameAnimation()
        {
            _frameTimer.Stop();
            if (!_animationEnabled || !_spriteFrames.ContainsKey(_state) || _spriteFrames[_state].Count < 2) return;
            _frameTimer.Interval = _state == "idle" ? TimeSpan.FromSeconds(2.8) : FrameIntervalForState(_state);
            _frameTimer.Start();
        }

        private TimeSpan FrameIntervalForState(string state)
        {
            if (state == "sleep") return TimeSpan.FromMilliseconds(1050);
            if (state == "meditate") return TimeSpan.FromMilliseconds(820);
            if (state == "tea") return TimeSpan.FromMilliseconds(680);
            if (state == "wave" || state == "happy") return TimeSpan.FromMilliseconds(360);
            return TimeSpan.FromMilliseconds(520);
        }

        private void OnFrameTick(object sender, EventArgs e)
        {
            List<BitmapImage> frames;
            if (!_spriteFrames.TryGetValue(_state, out frames) || frames.Count < 2)
            {
                _frameTimer.Stop();
                return;
            }
            _frameIndex = (_frameIndex + 1) % frames.Count;
            _spriteImage.Source = frames[_frameIndex];
            if (_state == "idle")
                _frameTimer.Interval = _frameIndex == 0 ? TimeSpan.FromSeconds(_random.Next(24, 46) / 10.0) : TimeSpan.FromMilliseconds(150);
        }

        private void StartMotionForState()
        {
            StopMotion();
            if (!_animationEnabled) return;

            var transform = new TranslateTransform();
            _floatingLayer.RenderTransform = transform;
            double height = _state == "sleep" ? -2 : (_state == "meditate" ? -4 : -6);
            double duration = _state == "sleep" ? 2.5 : (_state == "wave" || _state == "happy" ? 1.0 : 1.7);
            var animation = new DoubleAnimation
            {
                From = 0,
                To = height,
                Duration = TimeSpan.FromSeconds(duration),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            _motionStoryboard = new Storyboard();
            _motionStoryboard.Children.Add(animation);
            Storyboard.SetTarget(animation, transform);
            Storyboard.SetTargetProperty(animation, new PropertyPath(TranslateTransform.YProperty));
            _motionStoryboard.Begin(this, true);
        }

        private void StopMotion()
        {
            if (_motionStoryboard != null)
            {
                _motionStoryboard.Remove(this);
                _motionStoryboard = null;
            }
            _floatingLayer.RenderTransform = Transform.Identity;
        }

        private void ShowSpeech(string text)
        {
            if (!_speechEnabled || string.IsNullOrEmpty(text)) return;
            _speechText.Text = text;
            _speechBubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            _speechTimer.Stop();
            _speechTimer.Start();
        }

        private void HideSpeech()
        {
            _speechTimer.Stop();
            _speechBubble.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220)));
        }

        private string RandomLine(string category)
        {
            if (_customDialogues.Count > 0 && (category == "idle" || _random.Next(4) == 0))
                return _customDialogues[_random.Next(_customDialogues.Count)];
            string[] lines;
            if (!_dialogues.TryGetValue(category, out lines)) lines = _dialogues["idle"];
            return lines[_random.Next(lines.Length)];
        }

        private void BuildContextMenu()
        {
            var menu = new ContextMenu { FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 13 };
            var actions = new MenuItem { Header = "互动动作" };
            AddActionItem(actions, "聊聊天", "idle");
            AddActionItem(actions, "挥手", "wave");
            AddActionItem(actions, "开心", "happy");
            AddActionItem(actions, "害羞", "shy");
            AddActionItem(actions, "喝茶", "tea");
            AddActionItem(actions, "打坐", "meditate");
            AddActionItem(actions, "睡觉", "sleep");
            menu.Items.Add(actions);

            var sizeMenu = new MenuItem { Header = "大小" };
            AddSizeItem(sizeMenu, "小巧 75%", 0.75);
            AddSizeItem(sizeMenu, "标准 100%", 1.0);
            AddSizeItem(sizeMenu, "较大 125%", 1.25);
            AddSizeItem(sizeMenu, "很大 150%", 1.5);
            menu.Items.Add(sizeMenu);

            var opacityMenu = new MenuItem { Header = "透明度" };
            AddOpacityItem(opacityMenu, "100%", 1.0);
            AddOpacityItem(opacityMenu, "85%", 0.85);
            AddOpacityItem(opacityMenu, "70%", 0.70);
            menu.Items.Add(opacityMenu);

            var random = new MenuItem { Header = "随机活动", IsCheckable = true, IsChecked = _randomActivities };
            random.Click += delegate { _randomActivities = random.IsChecked; ResetNextAutoAction(); };
            menu.Items.Add(random);

            var speech = new MenuItem { Header = "对白气泡", IsCheckable = true, IsChecked = _speechEnabled };
            speech.Click += delegate { _speechEnabled = speech.IsChecked; if (!_speechEnabled) HideSpeech(); };
            menu.Items.Add(speech);

            var reminder = new MenuItem { Header = "45分钟休息提醒", IsCheckable = true, IsChecked = _remindersEnabled };
            reminder.Click += delegate
            {
                _remindersEnabled = reminder.IsChecked;
                if (_remindersEnabled) { _reminderTimer.Stop(); _reminderTimer.Start(); }
                else _reminderTimer.Stop();
            };
            menu.Items.Add(reminder);

            var animation = new MenuItem { Header = "动作动画", IsCheckable = true, IsChecked = _animationEnabled };
            animation.Click += delegate
            {
                _animationEnabled = animation.IsChecked;
                if (_animationEnabled) { StartMotionForState(); StartFrameAnimation(); }
                else { StopMotion(); _frameTimer.Stop(); }
            };
            menu.Items.Add(animation);

            var topmost = new MenuItem { Header = "始终置顶", IsCheckable = true, IsChecked = Topmost };
            topmost.Click += delegate { Topmost = topmost.IsChecked; };
            menu.Items.Add(topmost);

            var reset = new MenuItem { Header = "回到右下角" };
            reset.Click += delegate { ResetPosition(); };
            menu.Items.Add(reset);

            var hide = new MenuItem { Header = "暂时隐藏" };
            hide.Click += delegate { Hide(); };
            menu.Items.Add(hide);

            var settings = new MenuItem { Header = "设置…" };
            settings.Click += delegate { OpenSettings(); };
            menu.Items.Add(settings);
            menu.Items.Add(new Separator());

            var exit = new MenuItem { Header = "退出" };
            exit.Click += delegate { Close(); };
            menu.Items.Add(exit);
            ContextMenu = menu;
        }

        private void AddActionItem(MenuItem parent, string label, string state)
        {
            var item = new MenuItem { Header = label };
            item.Click += delegate
            {
                var seconds = state == "sleep" || state == "meditate" ? 10 : 5;
                SetState(state, seconds, RandomLine(state));
            };
            parent.Items.Add(item);
        }

        private void AddSizeItem(MenuItem parent, string label, double scale)
        {
            var item = new MenuItem { Header = label };
            item.Click += delegate { _scale = scale; ApplyScale(); };
            parent.Items.Add(item);
        }

        private void AddOpacityItem(MenuItem parent, string label, double opacity)
        {
            var item = new MenuItem { Header = label };
            item.Click += delegate
            {
                _baseOpacity = opacity;
                BeginAnimation(OpacityProperty, null);
                Opacity = opacity;
                MarkPetActivity();
            };
            parent.Items.Add(item);
        }

        private void OpenSettings()
        {
            MarkPetActivity();
            var dialog = new SettingsWindow(
                _animationEnabled,
                _randomActivities,
                _speechEnabled,
                _remindersEnabled,
                _idleFadeEnabled,
                _idleFadeSeconds,
                _systemNotifications,
                Topmost,
                _baseOpacity,
                CustomDialoguesText());
            dialog.Owner = this;
            if (dialog.ShowDialog() != true) return;

            _animationEnabled = dialog.AnimationEnabled;
            _randomActivities = dialog.RandomActivities;
            _speechEnabled = dialog.SpeechEnabled;
            _remindersEnabled = dialog.RemindersEnabled;
            _idleFadeEnabled = dialog.IdleFadeEnabled;
            _idleFadeSeconds = dialog.IdleFadeSeconds;
            _systemNotifications = dialog.SystemNotifications;
            Topmost = dialog.AlwaysOnTop;
            _baseOpacity = dialog.BaseOpacity;
            BeginAnimation(OpacityProperty, null);
            Opacity = _baseOpacity;
            _isIdleFaded = false;
            SaveCustomDialogues(dialog.CustomDialogues);

            if (_remindersEnabled) { _reminderTimer.Stop(); _reminderTimer.Start(); }
            else _reminderTimer.Stop();
            if (_animationEnabled) { StartMotionForState(); StartFrameAnimation(); }
            else { StopMotion(); _frameTimer.Stop(); }
            ResetNextAutoAction();
            BuildContextMenu();
            SaveSettings();
            SetState("happy", 3, "设置已经记下了。");
        }

        private void ApplyScale()
        {
            Width = Math.Round(BaseWidth * _scale);
            Height = Math.Round(BaseWidth * _scale);
        }

        private void ResetPosition()
        {
            _dockedState = null;
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 24;
            Top = area.Bottom - Height - 18;
            if (_spriteImage != null) SetState("idle", 0, null);
        }

        private static bool PositionIsVisible(double left, double top)
        {
            if (double.IsNaN(left) || double.IsNaN(top)) return false;
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var r = screen.WorkingArea;
                if (left >= r.Left - 100 && left <= r.Right - 40 && top >= r.Top - 100 && top <= r.Bottom - 40) return true;
            }
            return false;
        }

        private void CreateTrayIcon()
        {
            _trayIcon = new Forms.NotifyIcon { Text = "宋玉桌宠 V3", Icon = Drawing.SystemIcons.Information, Visible = true };
            var trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Items.Add("显示宋玉", null, delegate { Dispatcher.Invoke(delegate { Show(); Activate(); }); });
            trayMenu.Items.Add("挥手问候", null, delegate { Dispatcher.Invoke(delegate { Show(); SetState("wave", 4, RandomLine("wave")); }); });
            trayMenu.Items.Add("打坐修炼", null, delegate { Dispatcher.Invoke(delegate { Show(); SetState("meditate", 10, RandomLine("meditate")); }); });
            trayMenu.Items.Add("设置…", null, delegate { Dispatcher.Invoke(delegate { Show(); OpenSettings(); }); });
            trayMenu.Items.Add("退出", null, delegate { Dispatcher.Invoke(Close); });
            _trayIcon.ContextMenuStrip = trayMenu;
            _trayIcon.DoubleClick += delegate { Dispatcher.Invoke(delegate { Show(); Activate(); }); };
        }

        private void RegisterVisibilityHotkey()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                _windowSource = HwndSource.FromHwnd(handle);
                if (_windowSource != null) _windowSource.AddHook(WindowMessageHook);
                RegisterHotKey(handle, HotkeyId, ModControl | ModShift, VkH);
            }
            catch { }
        }

        private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
            {
                if (IsVisible) Hide();
                else { Show(); Activate(); SetState("wave", 3, "我回来啦。"); }
                handled = true;
            }
            return IntPtr.Zero;
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        private static double GetSystemIdleSeconds()
        {
            var info = new LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(info);
            if (!GetLastInputInfo(ref info)) return 0;
            return (Environment.TickCount - info.dwTime) / 1000.0;
        }

        private void LoadSettings()
        {
            if (!File.Exists(_settingsPath)) return;
            try
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(_settingsPath, Encoding.UTF8))
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length == 2) values[parts[0].Trim()] = parts[1].Trim();
                }
                double value;
                bool flag;
                int intValue;
                string text;
                if (values.TryGetValue("Scale", out text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) _scale = Math.Max(0.55, Math.Min(1.8, value));
                if (values.TryGetValue("Left", out text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) { Left = value; _loadedPosition = true; }
                if (values.TryGetValue("Top", out text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) Top = value;
                if (values.TryGetValue("Opacity", out text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) { _baseOpacity = Math.Max(0.45, Math.Min(1.0, value)); Opacity = _baseOpacity; }
                if (values.TryGetValue("Topmost", out text) && bool.TryParse(text, out flag)) Topmost = flag;
                if (values.TryGetValue("Animation", out text) && bool.TryParse(text, out flag)) _animationEnabled = flag;
                if (values.TryGetValue("RandomActivities", out text) && bool.TryParse(text, out flag)) _randomActivities = flag;
                if (values.TryGetValue("Speech", out text) && bool.TryParse(text, out flag)) _speechEnabled = flag;
                if (values.TryGetValue("Reminders", out text) && bool.TryParse(text, out flag)) _remindersEnabled = flag;
                if (values.TryGetValue("IdleFade", out text) && bool.TryParse(text, out flag)) _idleFadeEnabled = flag;
                if (values.TryGetValue("IdleFadeSeconds", out text) && int.TryParse(text, out intValue)) _idleFadeSeconds = Math.Max(3, Math.Min(300, intValue));
                if (values.TryGetValue("SystemNotifications", out text) && bool.TryParse(text, out flag)) _systemNotifications = flag;
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                File.WriteAllLines(_settingsPath, new[]
                {
                    "Scale=" + _scale.ToString(CultureInfo.InvariantCulture),
                    "Left=" + Left.ToString(CultureInfo.InvariantCulture),
                    "Top=" + Top.ToString(CultureInfo.InvariantCulture),
                    "Opacity=" + _baseOpacity.ToString(CultureInfo.InvariantCulture),
                    "Topmost=" + Topmost,
                    "Animation=" + _animationEnabled,
                    "RandomActivities=" + _randomActivities,
                    "Speech=" + _speechEnabled,
                    "Reminders=" + _remindersEnabled,
                    "IdleFade=" + _idleFadeEnabled,
                    "IdleFadeSeconds=" + _idleFadeSeconds,
                    "SystemNotifications=" + _systemNotifications
                }, Encoding.UTF8);
            }
            catch { }
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            SaveSettings();
            _autoTimer.Stop();
            _reminderTimer.Stop();
            _presenceTimer.Stop();
            _frameTimer.Stop();
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                UnregisterHotKey(handle, HotkeyId);
                if (_windowSource != null) _windowSource.RemoveHook(WindowMessageHook);
            }
            catch { }
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }
    }

    internal sealed class SettingsWindow : Window
    {
        private readonly CheckBox _animation;
        private readonly CheckBox _random;
        private readonly CheckBox _speech;
        private readonly CheckBox _reminders;
        private readonly CheckBox _idleFade;
        private readonly CheckBox _notifications;
        private readonly CheckBox _topmost;
        private readonly TextBox _idleSeconds;
        private readonly ComboBox _opacity;
        private readonly TextBox _customDialogues;

        public bool AnimationEnabled { get { return _animation.IsChecked == true; } }
        public bool RandomActivities { get { return _random.IsChecked == true; } }
        public bool SpeechEnabled { get { return _speech.IsChecked == true; } }
        public bool RemindersEnabled { get { return _reminders.IsChecked == true; } }
        public bool IdleFadeEnabled { get { return _idleFade.IsChecked == true; } }
        public bool SystemNotifications { get { return _notifications.IsChecked == true; } }
        public bool AlwaysOnTop { get { return _topmost.IsChecked == true; } }
        public int IdleFadeSeconds { get; private set; }
        public double BaseOpacity { get; private set; }
        public string CustomDialogues { get { return _customDialogues.Text; } }

        public SettingsWindow(bool animation, bool random, bool speech, bool reminders, bool idleFade,
            int idleSeconds, bool notifications, bool topmost, double opacity, string customDialogues)
        {
            Title = "宋玉桌宠 V3 · 设置";
            Width = 500;
            Height = 620;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            Background = new SolidColorBrush(Color.FromRgb(244, 251, 253));

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var stack = new StackPanel();
            var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 0);
            root.Children.Add(scroll);

            stack.Children.Add(Heading("行为与互动"));
            _animation = AddCheck(stack, "动作动画", "启用眨眼、挥手、呼吸等逐帧动作与轻微漂浮", animation);
            _random = AddCheck(stack, "随机活动", "偶尔自动喝茶、打坐、挥手或开心", random);
            _speech = AddCheck(stack, "对白气泡", "显示点击、动作和提醒对白", speech);
            _topmost = AddCheck(stack, "始终置顶", "让宋玉保持在普通窗口上方", topmost);

            stack.Children.Add(Heading("闲置与提醒"));
            _idleFade = AddCheck(stack, "闲置渐隐", "一段时间未与桌宠互动后自动变淡", idleFade);
            stack.Children.Add(LabelText("渐隐等待秒数（3–300）"));
            _idleSeconds = new TextBox { Text = idleSeconds.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(0, 3, 0, 8), Padding = new Thickness(7) };
            stack.Children.Add(_idleSeconds);
            _reminders = AddCheck(stack, "45分钟休息提醒", "切换喝茶动作并提醒起身和饮水", reminders);
            _notifications = AddCheck(stack, "Windows 系统提醒", "休息提醒时同时显示托盘通知", notifications);

            stack.Children.Add(Heading("外观"));
            stack.Children.Add(LabelText("普通透明度"));
            _opacity = new ComboBox { Margin = new Thickness(0, 3, 0, 8), Padding = new Thickness(7) };
            _opacity.Items.Add("100%");
            _opacity.Items.Add("85%");
            _opacity.Items.Add("70%");
            _opacity.SelectedIndex = opacity >= 0.925 ? 0 : (opacity >= 0.775 ? 1 : 2);
            stack.Children.Add(_opacity);

            stack.Children.Add(Heading("自定义对白"));
            stack.Children.Add(LabelText("每行一句；程序会与内置对白混合使用。"));
            _customDialogues = new TextBox
            {
                Text = customDialogues ?? string.Empty,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 135,
                Margin = new Thickness(0, 5, 6, 10),
                Padding = new Thickness(7)
            };
            stack.Children.Add(_customDialogues);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var cancel = new Button { Content = "取消", Width = 82, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
            cancel.Click += delegate { DialogResult = false; };
            var save = new Button { Content = "保存", Width = 82, Height = 32, IsDefault = true, Background = new SolidColorBrush(Color.FromRgb(82, 181, 211)), Foreground = Brushes.White };
            save.Click += OnSave;
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            Grid.SetRow(buttons, 1);
            root.Children.Add(buttons);
            Content = root;
        }

        private static TextBlock Heading(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.Bold,
                FontSize = 16,
                Foreground = new SolidColorBrush(Color.FromRgb(42, 102, 121)),
                Margin = new Thickness(0, 10, 0, 7)
            };
        }

        private static TextBlock LabelText(string text)
        {
            return new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(74, 87, 93)), Margin = new Thickness(0, 2, 0, 0) };
        }

        private static CheckBox AddCheck(Panel panel, string title, string help, bool value)
        {
            var box = new CheckBox { IsChecked = value, Margin = new Thickness(0, 4, 0, 1), FontSize = 14 };
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            content.Children.Add(new TextBlock { Text = help, FontSize = 12, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
            box.Content = content;
            panel.Children.Add(box);
            return box;
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            int seconds;
            if (!int.TryParse(_idleSeconds.Text.Trim(), out seconds) || seconds < 3 || seconds > 300)
            {
                MessageBox.Show(this, "渐隐等待时间请输入 3 到 300 之间的整数。", "设置", MessageBoxButton.OK, MessageBoxImage.Information);
                _idleSeconds.Focus();
                return;
            }
            IdleFadeSeconds = seconds;
            BaseOpacity = _opacity.SelectedIndex == 1 ? 0.85 : (_opacity.SelectedIndex == 2 ? 0.70 : 1.0);
            DialogResult = true;
        }
    }
}
