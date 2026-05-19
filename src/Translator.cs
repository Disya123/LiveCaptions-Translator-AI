using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public static class Translator
    {
        private static AutomationElement? window = null;
        private static Caption? caption = null;
        private static Setting? setting = null;

        private static readonly Queue<string> pendingTextQueue = new();
        private static readonly TranslationTaskQueue _translationTaskQueue = new();
        public static TranslationTaskQueue TranslationTaskQueue => _translationTaskQueue;

        private static readonly List<string> translatedSentences = new();
        private static readonly List<string> stableBuffer = new();
        private static string lastRawText = string.Empty;
        private static DateTime lastChangeTime = DateTime.MinValue;

        public static AutomationElement? Window
        {
            get => window;
            set => window = value;
        }
        public static Caption? Caption => caption;
        public static Setting? Setting => setting;

        public static bool LogOnlyFlag { get; set; } = false;
        public static bool FirstUseFlag { get; set; } = false;

        public static event Action? TranslationLogged;

        static Translator()
        {
            window = LiveCaptionsHandler.LaunchLiveCaptions();
            LiveCaptionsHandler.FixLiveCaptions(Window);
            LiveCaptionsHandler.HideLiveCaptions(Window);

            if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), models.Setting.FILENAME)))
                FirstUseFlag = true;

            caption = Caption.GetInstance();
            setting = Setting.Load();
        }

        public static void SyncLoop()
        {
            while (true)
            {
                if (Window == null)
                {
                    Thread.Sleep(2000);
                    continue;
                }

                string fullText = string.Empty;
                try
                {
                    // Check LiveCaptions.exe still alive
                    var info = Window.Current;
                    var name = info.Name;
                    // Get the text recognized by LiveCaptions (10-20ms)
                    fullText = LiveCaptionsHandler.GetCaptions(Window);
                }
                catch (ElementNotAvailableException)
                {
                    Window = null;
                    translatedSentences.Clear();
                    stableBuffer.Clear();
                    lastRawText = string.Empty;
                    continue;
                }
                if (string.IsNullOrEmpty(fullText))
                    continue;

                // Preprocess
                fullText = RegexPatterns.Acronym().Replace(fullText, "$1$2");
                fullText = RegexPatterns.AcronymWithWords().Replace(fullText, "$1 $2");
                fullText = RegexPatterns.PunctuationSpace().Replace(fullText, "$1 ");
                fullText = RegexPatterns.CJPunctuationSpace().Replace(fullText, "$1");
                fullText = TextUtil.ReplaceNewlines(fullText, TextUtil.MEDIUM_THRESHOLD);

                // Detect changes
                bool textChanged = string.CompareOrdinal(fullText, lastRawText) != 0;
                if (textChanged)
                {
                    lastRawText = fullText;
                    lastChangeTime = DateTime.Now;
                }

                var currentSentences = TextUtil.GetSentences(fullText);
                if (currentSentences.Count == 0)
                {
                    Thread.Sleep(25);
                    continue;
                }

                // Update real-time display of original captions
                string latestCaption = currentSentences[^1];
                if (string.CompareOrdinal(Caption.DisplayOriginalCaption, latestCaption) != 0)
                {
                    Caption.DisplayOriginalCaption = latestCaption;
                    Caption.DisplayOriginalCaption =
                        TextUtil.ShortenDisplaySentence(Caption.DisplayOriginalCaption, TextUtil.VERYLONG_THRESHOLD);
                }

                // Update Overlay original text showing recent sentences
                int displayCount = Math.Min(Setting.DisplaySentences + 1, currentSentences.Count);
                var displaySentences = currentSentences.Skip(currentSentences.Count - displayCount);
                Caption.OverlayOriginalCaption = string.Join("\n", displaySentences.Select(s => s.Trim()));

                // Align currentSentences with translatedSentences to find new unprocessed sentences
                // First, remove trailing incomplete sentence from translatedSentences if it exists,
                // but only if the text has actually changed to avoid infinite loop of re-adding during silence.
                if (textChanged && translatedSentences.Count > 0 && Array.IndexOf(TextUtil.PUNC_EOS, translatedSentences[^1][^1]) == -1)
                {
                    translatedSentences.RemoveAt(translatedSentences.Count - 1);
                }

                int k = GetUnprocessedIndex(currentSentences, translatedSentences);
                var unprocessedSentences = currentSentences.Skip(k).ToList();

                TimeSpan idleTime = DateTime.Now - lastChangeTime;

                foreach (var s in unprocessedSentences)
                {
                    bool isLast = (s == currentSentences[^1]);
                    bool isStable = false;

                    if (!isLast)
                    {
                        // Not the last sentence -> 100% stable
                        isStable = true;
                    }
                    else
                    {
                        // It is the last sentence.
                        bool endsWithEos = Array.IndexOf(TextUtil.PUNC_EOS, s[^1]) != -1;
                        if (endsWithEos)
                        {
                            // Ends with EOS, wait for 1.5s pause
                            if (idleTime.TotalSeconds >= 1.5)
                                isStable = true;
                        }
                        else
                        {
                            // Incomplete, wait for 2.0s pause
                            if (idleTime.TotalSeconds >= 2.0)
                                isStable = true;
                        }
                    }

                    if (isStable)
                    {
                        stableBuffer.Add(s);
                        translatedSentences.Add(s);
                    }
                }

                // Prune translatedSentences to keep memory footprint and lookup times minimal
                if (translatedSentences.Count > 50)
                {
                    translatedSentences.RemoveRange(0, translatedSentences.Count - 50);
                }

                // Trigger translation if we accumulated 3 sentences or speech paused for 1.5s
                if (stableBuffer.Count >= 3 || (stableBuffer.Count > 0 && idleTime.TotalSeconds >= 1.5))
                {
                    string paragraph = TextUtil.JoinSentences(stableBuffer);
                    pendingTextQueue.Enqueue(paragraph);
                    stableBuffer.Clear();
                }

                Thread.Sleep(25);
            }
        }

        private static int GetUnprocessedIndex(List<string> currentSentences, List<string> translatedSentences)
        {
            // 1. Try strict suffix matching first (handles repetitions and ordering perfectly)
            for (int i = currentSentences.Count; i > 0; i--)
            {
                if (MatchesSuffix(currentSentences, i, translatedSentences))
                {
                    return i;
                }
            }

            // 2. Fallback: Limit search depth to the last 7 sentences to prevent overhead
            for (int i = currentSentences.Count - 1; i >= 0; i--)
            {
                string cur = currentSentences[i].Trim();
                int lookbackDepth = Math.Max(0, translatedSentences.Count - 7);

                for (int j = translatedSentences.Count - 1; j >= lookbackDepth; j--)
                {
                    string hist = translatedSentences[j].Trim();

                    // Length-difference heuristic to avoid computing similarity on strings of different lengths
                    if (Math.Abs(cur.Length - hist.Length) > 20)
                        continue;

                    if (string.CompareOrdinal(cur, hist) == 0 || TextUtil.Similarity(cur, hist) >= 0.9)
                    {
                        return i + 1;
                    }
                }
            }

            return 0;
        }

        private static bool MatchesSuffix(List<string> currentSentences, int count, List<string> translatedSentences)
        {
            if (translatedSentences.Count < count)
                return false;

            for (int i = 0; i < count; i++)
            {
                string cur = currentSentences[i].Trim();
                string hist = translatedSentences[translatedSentences.Count - count + i].Trim();
                if (string.CompareOrdinal(cur, hist) != 0 && TextUtil.Similarity(cur, hist) < 0.9)
                    return false;
            }
            return true;
        }

        public static async Task TranslateLoop()
        {
            while (true)
            {
                // Check LiveCaptions.exe still alive
                if (Window == null)
                {
                    Caption.DisplayTranslatedCaption = "[WARNING] LiveCaptions was unexpectedly closed, restarting...";
                    Window = LiveCaptionsHandler.LaunchLiveCaptions();
                    Caption.DisplayTranslatedCaption = "";
                }

                // Translate
                if (pendingTextQueue.Count > 0)
                {
                    var originalSnapshot = pendingTextQueue.Dequeue();

                    if (LogOnlyFlag)
                    {
                        bool isOverwrite = await IsOverwrite(originalSnapshot);
                        await LogOnly(originalSnapshot, isOverwrite);
                    }
                    else if (TranslateAPI.HasStreaming)
                    {
                        // Use streaming for LLM providers
                        _translationTaskQueue.EnqueueStreaming(
                            (onChunk, token) => Task.Run(
                                () => TranslateStreaming(originalSnapshot, onChunk, token), token),
                            originalSnapshot);
                    }
                    else
                    {
                        // Use non-streaming for traditional translation APIs
                        _translationTaskQueue.Enqueue(token => Task.Run(
                            () => Translate(originalSnapshot, token), token), originalSnapshot);
                    }
                }

                Thread.Sleep(40);
            }
        }

        public static async Task DisplayLoop()
        {
            // Subscribe to streaming start event (to reset Caption state)
            _translationTaskQueue.StreamingStarted += () =>
            {
                Caption.TranslatedCaption = string.Empty;
                Caption.DisplayTranslatedCaption = string.Empty;
                Caption.OverlayCurrentTranslation = string.Empty;
                Caption.PrepareForNewBlock();
            };
            // Note: ChunkReceived is handled by OverlayWindow with throttling and color animation.

            while (true)
            {
                var (translatedText, isChoke) = _translationTaskQueue.Output;

                if (LogOnlyFlag)
                {
                    Caption.TranslatedCaption = string.Empty;
                    Caption.DisplayTranslatedCaption = "[Paused]";
                    Caption.OverlayNoticePrefix = "[Paused]";
                    Caption.OverlayCurrentTranslation = string.Empty;
                }
                else if (!string.IsNullOrEmpty(RegexPatterns.NoticePrefix().Replace(
                             translatedText, string.Empty).Trim()) &&
                         string.CompareOrdinal(Caption.TranslatedCaption, translatedText) != 0)
                {
                    // Non-streaming output update (for Google, DeepL, etc.)
                    Caption.TranslatedCaption = translatedText;
                    Caption.DisplayTranslatedCaption =
                        TextUtil.ShortenDisplaySentence(Caption.TranslatedCaption, TextUtil.VERYLONG_THRESHOLD);

                    // Overlay window
                    if (Caption.TranslatedCaption.Contains("[ERROR]") || Caption.TranslatedCaption.Contains("[WARNING]"))
                        Caption.OverlayCurrentTranslation = Caption.TranslatedCaption;
                    else
                    {
                        var match = RegexPatterns.NoticePrefixAndTranslation().Match(Caption.TranslatedCaption);
                        Caption.OverlayNoticePrefix = match.Groups[1].Value.Trim();
                        Caption.OverlayCurrentTranslation = match.Groups[2].Value.Trim();
                    }
                }

                // If the original sentence is a complete sentence, choke for better visual experience.
                if (isChoke)
                    Thread.Sleep(720);
                Thread.Sleep(40);
            }
        }

        public static async Task<(string, bool)> Translate(string text, CancellationToken token = default)
        {
            string translatedText;
            bool isChoke = Array.IndexOf(TextUtil.PUNC_EOS, text[^1]) != -1;

            try
            {
                var sw = Setting.MainWindow.LatencyShow ? Stopwatch.StartNew() : null;

                if (Setting.ContextAware && !TranslateAPI.IsLLMBased)
                {
                    string rawTranslation = await TranslateAPI.TranslateFunction($"{Caption.AwareContextsCaption} 🔤 {text} 🔤", token);
                    var parts = rawTranslation.Split("🔤", StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        translatedText = parts[^1].Trim();
                    }
                    else
                    {
                        // Fallback: translate the target paragraph directly without context if delimiters are missing or mangled
                        translatedText = await TranslateAPI.TranslateFunction(text, token);
                    }
                    translatedText = translatedText.Replace("🔤", "");
                }
                else
                {
                    translatedText = await TranslateAPI.TranslateFunction(text, token);
                    translatedText = translatedText.Replace("🔤", "");
                }

                if (sw != null)
                {
                    sw.Stop();
                    translatedText = $"[{sw.ElapsedMilliseconds,4} ms] " + translatedText;
                }
            }
            catch (OperationCanceledException ex)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ($"[ERROR] Translation Failed: {ex.Message}", isChoke);
            }

            return (translatedText, isChoke);
        }

        public static async Task<(string, bool)> TranslateStreaming(string text, Action<string> onChunk, CancellationToken token = default)
        {
            bool isChoke = Array.IndexOf(TextUtil.PUNC_EOS, text[^1]) != -1;

            try
            {
                var sw = Setting.MainWindow.LatencyShow ? Stopwatch.StartNew() : null;

                var streamFunc = TranslateAPI.STREAM_FUNCTIONS[Setting.ApiName];
                string translatedText = await streamFunc(text, onChunk, token);
                translatedText = translatedText.Replace("🔤", "");

                if (sw != null)
                {
                    sw.Stop();
                    translatedText = $"[{sw.ElapsedMilliseconds,4} ms] " + translatedText;
                }

                return (translatedText, isChoke);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ($"[ERROR] Translation Failed: {ex.Message}", isChoke);
            }
        }

        public static async Task Log(string originalText, string translatedText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            string targetLanguage, apiName;
            if (Setting != null)
            {
                targetLanguage = Setting.TargetLanguage;
                apiName = Setting.ApiName;
            }
            else
            {
                targetLanguage = "N/A";
                apiName = "N/A";
            }

            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, translatedText, targetLanguage, apiName);
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Logging history failed.", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task LogOnly(string originalText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, "N/A", "N/A", "LogOnly");
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Logging history failed.", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task AddContexts(CancellationToken token = default)
        {
            var lastLog = await SQLiteHistoryLogger.LoadLastTranslation(token);
            if (lastLog == null)
                return;

            if (Caption?.Contexts.Count >= Caption.MAX_CONTEXTS)
                Caption.Contexts.Dequeue();
            Caption?.Contexts.Enqueue(lastLog);

            Caption?.OnPropertyChanged("DisplayLogCards");
            Caption?.OnPropertyChanged("OverlayPreviousTranslation");
        }

        public static void ClearContexts()
        {
            Caption?.Contexts.Clear();
            translatedSentences.Clear();
            stableBuffer.Clear();
            lastRawText = string.Empty;

            Caption?.ClearActiveSubtitles();

            Caption?.OnPropertyChanged("DisplayLogCards");
            Caption?.OnPropertyChanged("OverlayPreviousTranslation");
        }

        // If this text is too similar to the last one, overwrite it when logging.
        public static async Task<bool> IsOverwrite(string originalText, CancellationToken token = default)
        {
            string lastOriginalText = await SQLiteHistoryLogger.LoadLastSourceText(token);
            if (lastOriginalText == null)
                return false;

            int minLen = Math.Min(originalText.Length, lastOriginalText.Length);
            originalText = originalText.Substring(0, minLen);
            lastOriginalText = lastOriginalText.Substring(0, minLen);

            double similarity = TextUtil.Similarity(originalText, lastOriginalText);
            return similarity > TextUtil.SIM_THRESHOLD;
        }
    }
}
