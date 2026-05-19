using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.Utils;
using Button = Wpf.Ui.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorEnum = LiveCaptionsTranslator.Utils.Color;

namespace LiveCaptionsTranslator
{
    public partial class OverlayWindow : Window
    {
        public static readonly Dictionary<ColorEnum, SolidColorBrush> ColorMap = new()
        {
            {ColorEnum.White, Brushes.White},
            {ColorEnum.Yellow, Brushes.Yellow},
            {ColorEnum.LimeGreen, Brushes.LimeGreen},
            {ColorEnum.Aqua, Brushes.Aqua},
            {ColorEnum.Blue, Brushes.Blue},
            {ColorEnum.DeepPink, Brushes.DeepPink},
            {ColorEnum.Red, Brushes.Red},
            {ColorEnum.Black, Brushes.Black},
        };
        private readonly Dictionary<ColorEnum, SolidColorBrush> colorMap = ColorMap;
        private CaptionVisible onlyMode = CaptionVisible.Both;

        // Streaming character queue and typewriter timer
        private readonly Queue<char> _charQueue = new();
        private readonly DispatcherTimer _chunkFlushTimer;
        private readonly object _chunkLock = new();

        // Color animation for new translation text
        private Color _highlightColor = Color.FromRgb(255, 165, 0); // Orange highlight
        private Color _baseTranslationColor = Colors.White;

        public CaptionVisible OnlyMode
        {
            get => onlyMode;
            set
            {
                onlyMode = value;
                ResizeForOnlyMode();
            }
        }
        public CaptionLocation SwitchMode { get; set; } = CaptionLocation.TranslationTop;

        public OverlayWindow()
        {
            InitializeComponent();
            DataContext = Translator.Caption;

            Loaded += (s, e) => Translator.Caption.PropertyChanged += TranslatedChanged;
            Unloaded += (s, e) => Translator.Caption.PropertyChanged -= TranslatedChanged;

            OriginalCaption.FontWeight = Translator.Setting.OverlayWindow.FontBold == Utils.FontBold.Both ?
                FontWeights.Bold : FontWeights.Regular;
            TranslatedCaption.FontWeight = Translator.Setting.OverlayWindow.FontBold >= Utils.FontBold.TranslationOnly ?
                FontWeights.Bold : FontWeights.Regular;

            OriginalCaptionDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;
            if (NoticePrefixDecorator != null)
                NoticePrefixDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;

            OriginalCaption.Foreground = colorMap[Translator.Setting.OverlayWindow.FontColor];
            UpdateTranslationColor(colorMap[Translator.Setting.OverlayWindow.FontColor]);

            BorderBackground.Background = colorMap[Translator.Setting.OverlayWindow.BackgroundColor];
            BorderBackground.Opacity = Translator.Setting.OverlayWindow.Opacity;

            ApplyFontSize();
            ApplyBackgroundOpacity();

            // Initialize typewriter timer (fires every 20ms for smooth character-by-character flow)
            _chunkFlushTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(20)
            };
            _chunkFlushTimer.Tick += (s, e) => FlushChunkBuffer();

            // Subscribe to streaming events from the translation queue
            Translator.TranslationTaskQueue.ChunkReceived += OnChunkReceived;
            Translator.TranslationTaskQueue.StreamingStarted += OnStreamingStarted;
        }

        private void OnChunkReceived(string chunk)
        {
            lock (_chunkLock)
            {
                foreach (char c in chunk)
                {
                    _charQueue.Enqueue(c);
                }
            }

            // Ensure timer is running on UI thread
            Dispatcher.BeginInvoke(() =>
            {
                if (!_chunkFlushTimer.IsEnabled)
                    _chunkFlushTimer.Start();
            });
        }

        private void OnStreamingStarted()
        {
            lock (_chunkLock)
            {
                _charQueue.Clear();
            }
        }

        private void FlushChunkBuffer()
        {
            string newText = string.Empty;
            lock (_chunkLock)
            {
                int queueCount = _charQueue.Count;
                if (queueCount == 0)
                {
                    _chunkFlushTimer.Stop();
                    return;
                }

                // Dynamic speed adjustment based on queue size:
                // If queue is long, type more characters per tick to avoid lagging behind speech.
                int charsToType = 1;
                if (queueCount > 80)
                    charsToType = 6;
                else if (queueCount > 40)
                    charsToType = 3;
                else if (queueCount > 15)
                    charsToType = 2;

                var sb = new StringBuilder();
                for (int i = 0; i < charsToType && _charQueue.Count > 0; i++)
                {
                    sb.Append(_charQueue.Dequeue());
                }
                newText = sb.ToString();
            }

            // Append characters to Caption (triggers UI binding updates)
            Translator.Caption.TranslatedCaption += newText;
            Translator.Caption.DisplayTranslatedCaption =
                LiveCaptionsTranslator.utils.TextUtil.ShortenDisplaySentence(
                    Translator.Caption.TranslatedCaption,
                    LiveCaptionsTranslator.utils.TextUtil.VERYLONG_THRESHOLD);

            if (!Translator.Caption.TranslatedCaption.Contains("[ERROR]") &&
                !Translator.Caption.TranslatedCaption.Contains("[WARNING]"))
            {
                var match = LiveCaptionsTranslator.utils.RegexPatterns.NoticePrefixAndTranslation()
                    .Match(Translator.Caption.TranslatedCaption);
                Translator.Caption.OverlayNoticePrefix = match.Groups[1].Value.Trim();
                Translator.Caption.OverlayCurrentTranslation = match.Groups[2].Value.Trim();
                Translator.Caption.UpdateCurrentSubtitleBlock(Translator.Caption.OverlayCurrentTranslation);
            }
            else
            {
                Translator.Caption.UpdateCurrentSubtitleBlock(Translator.Caption.TranslatedCaption);
            }
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                this.DragMove();
        }

        private void TopThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            double newHeight = this.Height - e.VerticalChange;

            if (newHeight >= this.MinHeight)
            {
                this.Top += e.VerticalChange;
                this.Height = newHeight;
            }
        }

        private void BottomThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            double newHeight = this.Height + e.VerticalChange;

            if (newHeight >= this.MinHeight)
            {
                this.Height = newHeight;
            }
        }

        private void LeftThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = this.Width - e.HorizontalChange;

            if (newWidth >= this.MinWidth)
            {
                this.Left += e.HorizontalChange;
                this.Width = newWidth;
            }
        }

        private void RightThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = this.Width + e.HorizontalChange;

            if (newWidth >= this.MinWidth)
            {
                this.Width = newWidth;
            }
        }

        private void TopLeftThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            TopThumb_OnDragDelta(sender, e);
            LeftThumb_OnDragDelta(sender, e);
        }

        private void TopRightThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            TopThumb_OnDragDelta(sender, e);
            RightThumb_OnDragDelta(sender, e);
        }

        private void BottomLeftThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            BottomThumb_OnDragDelta(sender, e);
            LeftThumb_OnDragDelta(sender, e);
        }

        private void BottomRightThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            BottomThumb_OnDragDelta(sender, e);
            RightThumb_OnDragDelta(sender, e);
        }

        private void TranslatedChanged(object sender, PropertyChangedEventArgs e)
        {
            ApplyFontSize();
        }

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            ControlPanel.Visibility = Visibility.Visible;
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            ControlPanel.Visibility = Visibility.Hidden;
        }

        private void FontIncrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontSize + StyleConsts.DELTA_FONT_SIZE < StyleConsts.MAX_FONT_SIZE)
            {
                Translator.Setting.OverlayWindow.FontSize += StyleConsts.DELTA_FONT_SIZE;
                ApplyFontSize();
            }
        }

        private void FontDecrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontSize - StyleConsts.DELTA_FONT_SIZE > StyleConsts.MIN_FONT_SIZE)
            {
                Translator.Setting.OverlayWindow.FontSize -= StyleConsts.DELTA_FONT_SIZE;
                ApplyFontSize();
            }
        }

        private void FontBold_Click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.OverlayWindow.FontBold++;
            if (Translator.Setting.OverlayWindow.FontBold > Utils.FontBold.Both)
                Translator.Setting.OverlayWindow.FontBold = Utils.FontBold.None;
            switch (Translator.Setting.OverlayWindow.FontBold)
            {
                case Utils.FontBold.None:
                    OriginalCaption.FontWeight = FontWeights.Regular;
                    TranslatedCaption.FontWeight = FontWeights.Regular;
                    break;
                case Utils.FontBold.TranslationOnly:
                    OriginalCaption.FontWeight = FontWeights.Regular;
                    TranslatedCaption.FontWeight = FontWeights.Bold;
                    break;
                case Utils.FontBold.SubtitleOnly:
                    OriginalCaption.FontWeight = FontWeights.Bold;
                    TranslatedCaption.FontWeight = FontWeights.Regular;
                    break;
                case Utils.FontBold.Both:
                    OriginalCaption.FontWeight = FontWeights.Bold;
                    TranslatedCaption.FontWeight = FontWeights.Bold;
                    break;
            }
        }

        private void FontStrokeIncrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontStroke + StyleConsts.DELTA_STROKE > StyleConsts.MAX_STROKE)
                return;
            Translator.Setting.OverlayWindow.FontStroke += StyleConsts.DELTA_STROKE;
            ApplyFontStroke();
        }

        private void FontStrokeDecrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontStroke - StyleConsts.DELTA_STROKE < StyleConsts.MIN_STROKE)
                return;
            Translator.Setting.OverlayWindow.FontStroke -= StyleConsts.DELTA_STROKE;
            ApplyFontStroke();
        }

        private void FontColorCycle_Click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.OverlayWindow.FontColor++;
            if (Translator.Setting.OverlayWindow.FontColor > ColorEnum.Black)
                Translator.Setting.OverlayWindow.FontColor = ColorEnum.White;
            OriginalCaption.Foreground = colorMap[Translator.Setting.OverlayWindow.FontColor];
            TranslatedCaption.Foreground = colorMap[Translator.Setting.OverlayWindow.FontColor];
            UpdateTranslationColor(colorMap[Translator.Setting.OverlayWindow.FontColor]);
        }

        private void BackgroundOpacityIncrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.Opacity + StyleConsts.DELTA_OPACITY < StyleConsts.MAX_OPACITY)
                Translator.Setting.OverlayWindow.Opacity += StyleConsts.DELTA_OPACITY;
            else
                Translator.Setting.OverlayWindow.Opacity = StyleConsts.MAX_OPACITY;
            ApplyBackgroundOpacity();
        }

        private void BackgroundOpacityDecrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.Opacity - StyleConsts.DELTA_OPACITY > StyleConsts.MIN_OPACITY)
                Translator.Setting.OverlayWindow.Opacity -= StyleConsts.DELTA_OPACITY;
            else
                Translator.Setting.OverlayWindow.Opacity = StyleConsts.MIN_OPACITY;
            ApplyBackgroundOpacity();
        }

        private void BackgroundColorCycle_Click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.OverlayWindow.BackgroundColor++;
            if (Translator.Setting.OverlayWindow.BackgroundColor > ColorEnum.Black)
                Translator.Setting.OverlayWindow.BackgroundColor = ColorEnum.White;
            BorderBackground.Background = colorMap[Translator.Setting.OverlayWindow.BackgroundColor];

            BorderBackground.Opacity = Translator.Setting.OverlayWindow.Opacity;
            ApplyBackgroundOpacity();
        }

        private void OnlyModeButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var symbolIcon = button?.Icon as SymbolIcon;

            if (onlyMode == CaptionVisible.SubtitleOnly)
            {
                // (0) Subtitle + Translation
                symbolIcon.Symbol = SymbolRegular.PanelBottom20;
                OnlyMode = CaptionVisible.Both;
            }
            else if (onlyMode == CaptionVisible.Both)
            {
                // (1) Translation Only
                symbolIcon.Symbol = SymbolRegular.PanelTopExpand20;
                OnlyMode = CaptionVisible.TranslationOnly;
            }
            else
            {
                // (2) Subtitle Only
                symbolIcon.Symbol = SymbolRegular.PanelTopContract20;
                OnlyMode = CaptionVisible.SubtitleOnly;
            }
        }

        private void SwitchModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (SwitchMode == CaptionLocation.TranslationTop)
            {
                Grid.SetRow(TranslatedCaptionCard, 1);
                Grid.SetRow(OriginalCaptionCard, 0);
                SwitchMode = CaptionLocation.SubtitleTop;
            }
            else
            {
                Grid.SetRow(TranslatedCaptionCard, 0);
                Grid.SetRow(OriginalCaptionCard, 1);
                SwitchMode = CaptionLocation.TranslationTop;
            }
        }

        private void ClickThrough_Click(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var extendedStyle = WindowsAPI.GetWindowLong(hwnd, WindowsAPI.GWL_EXSTYLE);
            WindowsAPI.SetWindowLong(hwnd, WindowsAPI.GWL_EXSTYLE, extendedStyle | WindowsAPI.WS_EX_TRANSPARENT);
            ControlPanel.Visibility = Visibility.Collapsed;
        }

        public void ResizeForOnlyMode()
        {
            if (onlyMode == CaptionVisible.TranslationOnly)
            {
                // (1) Translation Only
                OriginalCaptionCard.Visibility = Visibility.Collapsed;
                this.MinHeight -= StyleConsts.DELTA_OVERLAY_HEIGHT;
                this.Height -= StyleConsts.DELTA_OVERLAY_HEIGHT;
                this.Top += StyleConsts.DELTA_OVERLAY_HEIGHT;
            }
            if (onlyMode == CaptionVisible.SubtitleOnly)
            {
                // restore
                OriginalCaptionCard.Visibility = Visibility.Visible;
                this.Top -= StyleConsts.DELTA_OVERLAY_HEIGHT;
                this.Height += StyleConsts.DELTA_OVERLAY_HEIGHT;
                this.MinHeight += StyleConsts.DELTA_OVERLAY_HEIGHT;

                // (2) Subtitle Only
                TranslatedCaptionCard.Visibility = Visibility.Collapsed;
                this.MinHeight -= StyleConsts.DELTA_OVERLAY_HEIGHT;
                this.Height -= StyleConsts.DELTA_OVERLAY_HEIGHT;
            }
            else if (onlyMode == CaptionVisible.Both)
            {
                // restore
                TranslatedCaptionCard.Visibility = Visibility.Visible;
                this.Height += StyleConsts.DELTA_OVERLAY_HEIGHT;
                this.MinHeight += StyleConsts.DELTA_OVERLAY_HEIGHT;
            }
        }

        public void ApplyFontSize()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                OriginalCaption.FontSize = Translator.Setting.OverlayWindow.FontSize;
                TranslatedCaption.FontSize = (int)(OriginalCaption.FontSize * 1.25);
            }), DispatcherPriority.Background);
        }

        public void ApplyFontStroke()
        {
            OriginalCaptionDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;
            if (NoticePrefixDecorator != null)
                NoticePrefixDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;
        }

        public void ApplyBackgroundOpacity()
        {
            Color color = ((SolidColorBrush)BorderBackground.Background).Color;
            BorderBackground.Background = new SolidColorBrush(Color.FromArgb(
                (byte)Translator.Setting.OverlayWindow.Opacity, color.R, color.G, color.B));
        }

        private void UpdateTranslationColor(SolidColorBrush brush)
        {
            var color = brush.Color;

            double target = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 127 ? 0 : 255;
            byte r = (byte)Math.Clamp(color.R + (target - color.R) * 0.3, 0, 255);
            byte g = (byte)Math.Clamp(color.G + (target - color.G) * 0.4, 0, 255);
            byte b = (byte)Math.Clamp(color.B + (target - color.B) * 0.3, 0, 255);

            if (NoticePrefixText != null)
                NoticePrefixText.Foreground = brush;

            _baseTranslationColor = Color.FromRgb(r, g, b);

            // Dynamically set highlight color for best contrast with the background/text color:
            double brightness = 0.299 * _baseTranslationColor.R + 0.587 * _baseTranslationColor.G + 0.114 * _baseTranslationColor.B;
            if (brightness > 127)
            {
                _highlightColor = Color.FromRgb(255, 140, 0); // Vibrant Dark Orange
            }
            else
            {
                _highlightColor = Color.FromRgb(0, 120, 215); // Vibrant Blue
            }

            var baseBrush = new SolidColorBrush(_baseTranslationColor);
            if (TranslatedCaption != null)
                TranslatedCaption.Foreground = baseBrush;
            Translator.Caption?.UpdateAllActiveSubtitlesBrush(baseBrush);
        }
    }
}
