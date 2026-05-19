using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace LiveCaptionsTranslator.models
{
    public class TranslationTaskQueue
    {
        private readonly Channel<TranslationTask> _channel = Channel.CreateUnbounded<TranslationTask>();

        private (string translatedText, bool isChoke) output = (string.Empty, false);
        public (string translatedText, bool isChoke) Output => output;

        public TranslationTaskQueue()
        {
            _ = Task.Run(ProcessQueueAsync);
        }

        public void Enqueue(Func<CancellationToken, Task<(string, bool)>> worker, string originalText)
        {
            var newTranslationTask = new TranslationTask(worker, originalText, new CancellationTokenSource());
            _channel.Writer.TryWrite(newTranslationTask);
        }

        private async Task ProcessQueueAsync()
        {
            await foreach (var currentTask in _channel.Reader.ReadAllAsync())
            {
                try
                {
                    var result = await currentTask.ExecuteAsync();
                    output = result;

                    // Log after translation.
                    bool isOverwrite = await Translator.IsOverwrite(currentTask.OriginalText);
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
                    output = ($"[ERROR] Translation Failed: {ex.Message}", false);
                }
            }
        }
    }

    public class TranslationTask
    {
        private readonly Func<CancellationToken, Task<(string, bool)>> _worker;
        public string OriginalText { get; }
        public CancellationTokenSource CTS { get; }

        public TranslationTask(Func<CancellationToken, Task<(string, bool)>> worker,
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
}