using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace LiveCaptionsTranslator.models
{
    public class TranslationTaskQueue
    {
        private readonly Channel<ITranslationTask> _channel = Channel.CreateUnbounded<ITranslationTask>();

        private (string translatedText, bool isChoke) output = (string.Empty, false);
        public (string translatedText, bool isChoke) Output => output;

        /// <summary>
        /// Fired for each streaming chunk received from an LLM provider.
        /// </summary>
        public event Action<string>? ChunkReceived;

        /// <summary>
        /// Fired when a new streaming translation begins (to reset UI state).
        /// </summary>
        public event Action? StreamingStarted;

        public TranslationTaskQueue()
        {
            _ = Task.Run(ProcessQueueAsync);
        }

        /// <summary>
        /// Enqueue a non-streaming translation task (Google, DeepL, etc.)
        /// </summary>
        public void Enqueue(Func<CancellationToken, Task<(string, bool)>> worker, string originalText)
        {
            var task = new StandardTranslationTask(worker, originalText, new CancellationTokenSource());
            _channel.Writer.TryWrite(task);
        }

        /// <summary>
        /// Enqueue a streaming translation task (OpenAI, Ollama, OpenRouter).
        /// The worker receives an Action&lt;string&gt; onChunk callback.
        /// </summary>
        public void EnqueueStreaming(Func<Action<string>, CancellationToken, Task<(string, bool)>> worker, string originalText)
        {
            var task = new StreamingTranslationTask(worker, originalText, new CancellationTokenSource());
            _channel.Writer.TryWrite(task);
        }

        private async Task ProcessQueueAsync()
        {
            await foreach (var currentTask in _channel.Reader.ReadAllAsync())
            {
                try
                {
                    (string, bool) result;
                    bool isOverwrite = await Translator.IsOverwrite(currentTask.OriginalText);

                    if (currentTask is StreamingTranslationTask streamingTask)
                    {
                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            if (!isOverwrite)
                            {
                                Translator.Caption?.PrepareForNewBlock();
                            }
                            else
                            {
                                Translator.Caption?.ClearCurrentSubtitleBlockText();
                            }
                        });

                        StreamingStarted?.Invoke();
                        result = await streamingTask.ExecuteAsync(chunk => ChunkReceived?.Invoke(chunk));
                    }
                    else if (currentTask is StandardTranslationTask standardTask)
                    {
                        result = await standardTask.ExecuteAsync();

                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            if (!isOverwrite)
                            {
                                Translator.Caption?.PrepareForNewBlock();
                            }

                            string textToUpdate = result.Item1;
                            if (!textToUpdate.Contains("[ERROR]") && !textToUpdate.Contains("[WARNING]"))
                            {
                                var match = LiveCaptionsTranslator.utils.RegexPatterns.NoticePrefixAndTranslation().Match(textToUpdate);
                                textToUpdate = match.Groups[2].Value.Trim();
                            }
                            Translator.Caption?.UpdateCurrentSubtitleBlock(textToUpdate);
                        });
                    }
                    else continue;

                    output = result;

                    // Log after translation.
                    if (!isOverwrite)
                        await Translator.AddContexts();
                    await Translator.Log(currentTask.OriginalText, result.Item1, isOverwrite);
                }
                catch (OperationCanceledException)
                {
                    // Task was cancelled
                }
                catch (Exception ex)
                {
                    string errMsg = $"[ERROR] Translation Failed: {ex.Message}";
                    output = (errMsg, false);
                    Translator.Caption.UpdateCurrentSubtitleBlock(errMsg);
                }
            }
        }
    }

    public interface ITranslationTask
    {
        string OriginalText { get; }
        CancellationTokenSource CTS { get; }
    }

    public class StandardTranslationTask : ITranslationTask
    {
        private readonly Func<CancellationToken, Task<(string, bool)>> _worker;
        public string OriginalText { get; }
        public CancellationTokenSource CTS { get; }

        public StandardTranslationTask(Func<CancellationToken, Task<(string, bool)>> worker,
            string originalText, CancellationTokenSource cts)
        {
            _worker = worker;
            OriginalText = originalText;
            CTS = cts;
        }

        public Task<(string, bool)> ExecuteAsync()
        {
            return _worker(CTS.Token);
        }
    }

    public class StreamingTranslationTask : ITranslationTask
    {
        private readonly Func<Action<string>, CancellationToken, Task<(string, bool)>> _worker;
        public string OriginalText { get; }
        public CancellationTokenSource CTS { get; }

        public StreamingTranslationTask(Func<Action<string>, CancellationToken, Task<(string, bool)>> worker,
            string originalText, CancellationTokenSource cts)
        {
            _worker = worker;
            OriginalText = originalText;
            CTS = cts;
        }

        public Task<(string, bool)> ExecuteAsync(Action<string> onChunk)
        {
            return _worker(onChunk, CTS.Token);
        }
    }
}