using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Threading.Tasks;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class Caption : INotifyPropertyChanged
    {
        public const int MAX_CONTEXTS = 10;

        private static Caption? instance = null;
        public event PropertyChangedEventHandler? PropertyChanged;

        private string displayOriginalCaption = string.Empty;
        private string displayTranslatedCaption = string.Empty;
        private string overlayOriginalCaption = " ";
        private string overlayCurrentTranslation = " ";
        private string overlayNoticePrefix = " ";

        public string OriginalCaption { get; set; } = string.Empty;
        public string TranslatedCaption { get; set; } = string.Empty;

        public Queue<TranslationHistoryEntry> Contexts { get; } = new(MAX_CONTEXTS);

        public IEnumerable<TranslationHistoryEntry> AwareContexts => GetPreviousContexts(Translator.Setting.NumContexts);
        public string AwareContextsCaption => GetPreviousText(Translator.Setting.NumContexts, TextType.Caption);

        public IEnumerable<TranslationHistoryEntry> DisplayLogCards =>
            GetPreviousContexts(Translator.Setting.DisplaySentences).Reverse();

        public string DisplayOriginalCaption
        {
            get => displayOriginalCaption;
            set
            {
                displayOriginalCaption = value;
                OnPropertyChanged("DisplayOriginalCaption");
            }
        }
        public string DisplayTranslatedCaption
        {
            get => displayTranslatedCaption;
            set
            {
                displayTranslatedCaption = value;
                OnPropertyChanged("DisplayTranslatedCaption");
            }
        }

        public string OverlayOriginalCaption
        {
            get => overlayOriginalCaption;
            set
            {
                overlayOriginalCaption = value;
                OnPropertyChanged("OverlayOriginalCaption");
            }
        }
        public string OverlayNoticePrefix
        {
            get => overlayNoticePrefix;
            set
            {
                overlayNoticePrefix = value;
                OnPropertyChanged("OverlayNoticePrefix");
            }
        }
        public string OverlayCurrentTranslation
        {
            get => overlayCurrentTranslation;
            set
            {
                overlayCurrentTranslation = value;
                OnPropertyChanged("OverlayCurrentTranslation");
            }
        }

        public string OverlayPreviousTranslation =>
            GetPreviousText(Translator.Setting.DisplaySentences, TextType.Translation);

        private SubtitleBlock? currentActiveSubtitleBlock = null;
        private bool _needsNewBlock = true;

        public ObservableCollection<SubtitleBlock> ActiveSubtitles { get; } = new();

        public void PrepareForNewBlock()
        {
            App.Current?.Dispatcher.Invoke(() =>
            {
                _needsNewBlock = true;
            });
        }

        public double TranslationFontSize => Translator.Setting.OverlayWindow.FontSize * 1.25;
        public double TranslationFontStroke => Translator.Setting.OverlayWindow.FontStroke;
        public FontWeight TranslationFontWeight => Translator.Setting.OverlayWindow.FontBold >= LiveCaptionsTranslator.Utils.FontBold.TranslationOnly ? FontWeights.Bold : FontWeights.Regular;

        public void NotifyTranslationStyleChanged()
        {
            OnPropertyChanged(nameof(TranslationFontSize));
            OnPropertyChanged(nameof(TranslationFontStroke));
            OnPropertyChanged(nameof(TranslationFontWeight));
        }

        public void StartNewSubtitleBlock(Color highlightColor, Brush baseBrush)
        {
            App.Current?.Dispatcher.Invoke(() =>
            {
                var blockToFade = currentActiveSubtitleBlock;
                if (blockToFade != null)
                {
                    blockToFade.IsFadingOut = true;
                    _ = Task.Delay(2000).ContinueWith(_ =>
                    {
                        try
                        {
                            App.Current?.Dispatcher.Invoke(() =>
                            {
                                ActiveSubtitles.Remove(blockToFade);
                            });
                        }
                        catch {}
                    });
                }

                currentActiveSubtitleBlock = new SubtitleBlock();
                
                // Initialize block foreground with highlight color and animate to base color
                var brush = new SolidColorBrush(highlightColor);
                currentActiveSubtitleBlock.Foreground = brush;
                
                ActiveSubtitles.Add(currentActiveSubtitleBlock);

                // Start transition to base translation color
                var targetColor = Colors.White;
                if (baseBrush is SolidColorBrush solidBaseBrush)
                {
                    targetColor = solidBaseBrush.Color;
                }
                
                var colorAnim = new ColorAnimation
                {
                    From = highlightColor,
                    To = targetColor,
                    Duration = new Duration(TimeSpan.FromSeconds(1.5)),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                brush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
            });
        }

        public void UpdateCurrentSubtitleBlock(string text)
        {
            App.Current?.Dispatcher.Invoke(() =>
            {
                if (_needsNewBlock || currentActiveSubtitleBlock == null)
                {
                    var baseBrush = OverlayWindow.ColorMap[Translator.Setting.OverlayWindow.FontColor];
                    StartNewSubtitleBlock(Color.FromRgb(255, 165, 0), baseBrush);
                    _needsNewBlock = false;
                }
                if (currentActiveSubtitleBlock != null)
                {
                    currentActiveSubtitleBlock.Text = text;
                }
            });
        }

        public void ClearCurrentSubtitleBlockText()
        {
            App.Current?.Dispatcher.Invoke(() =>
            {
                if (currentActiveSubtitleBlock != null)
                {
                    currentActiveSubtitleBlock.Text = string.Empty;
                }
            });
        }

        public void UpdateAllActiveSubtitlesBrush(Brush brush)
        {
            App.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var block in ActiveSubtitles)
                {
                    if (!block.IsFadingOut)
                    {
                        block.Foreground = brush;
                    }
                }
            });
        }

        public void ClearActiveSubtitles()
        {
            App.Current?.Dispatcher.Invoke(() =>
            {
                ActiveSubtitles.Clear();
                currentActiveSubtitleBlock = null;
                _needsNewBlock = true;
            });
        }

        private Caption()
        {
        }

        public static Caption GetInstance()
        {
            if (instance != null)
                return instance;
            instance = new Caption();
            return instance;
        }

        public string GetPreviousText(int count, TextType textType)
        {
            if (count <= 0 || Contexts.Count == 0)
                return string.Empty;

            var prev = Contexts
                .Reverse().Take(count).Reverse()
                .Select(entry => entry == null || string.CompareOrdinal(entry.TranslatedText, "N/A") == 0 ||
                                 entry.TranslatedText.Contains("[ERROR]") || entry.TranslatedText.Contains("[WARNING]") ?
                    "" : (textType == TextType.Caption ? entry.SourceText : entry.TranslatedText))
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => RegexPatterns.NoticePrefix().Replace(s, "").Trim())
                .ToList();

            if (prev.Count == 0)
                return string.Empty;

            string joined = string.Join("\n", prev);
            if (!string.IsNullOrEmpty(joined))
                joined += "\n";
            return joined;
        }

        public IEnumerable<TranslationHistoryEntry> GetPreviousContexts(int count)
        {
            if (count <= 0 || Contexts.Count == 0)
                return [];

            return Contexts
                .Reverse().Take(count).Reverse()
                .Where(entry => entry != null && string.CompareOrdinal(entry.TranslatedText, "N/A") != 0 &&
                                !entry.TranslatedText.Contains("[ERROR]") &&
                                !entry.TranslatedText.Contains("[WARNING]"));
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }

    public enum TextType
    {
        Caption,
        Translation
    }
}