using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using NAudio.Wave;
using LiveCaptionsTranslator.apis;

namespace LiveCaptionsTranslator.models
{
    public class TTSPlayer
    {
        private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();
        private WaveOutEvent? _outputDevice;
        private StreamMediaFoundationReader? _audioFileReader;
        
        private bool _isMuted = false;
        
        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                _isMuted = value;
                if (_isMuted)
                {
                    StopCurrentPlayback();
                    ClearQueue();
                }
            }
        }

        public TTSPlayer()
        {
            _ = Task.Run(ProcessQueueAsync);
        }

        public void Enqueue(string text)
        {
            if (IsMuted || string.IsNullOrWhiteSpace(text)) return;
            
            string engine = Translator.Setting.TTSEngine;
            if (engine == "None" || string.IsNullOrEmpty(engine)) return;

            _channel.Writer.TryWrite(text);
        }

        public void ClearQueue()
        {
            while (_channel.Reader.TryRead(out _)) { }
        }

        public void StopCurrentPlayback()
        {
            if (_outputDevice != null)
            {
                try
                {
                    _outputDevice.Stop();
                }
                catch { }
            }
        }

        private async Task ProcessQueueAsync()
        {
            await foreach (var text in _channel.Reader.ReadAllAsync())
            {
                if (IsMuted) continue;

                try
                {
                    string engine = Translator.Setting.TTSEngine;
                    Stream? audioStream = null;

                    if (engine == "OpenAI")
                    {
                        audioStream = await TTSAPI.OpenAIStream(text);
                    }
                    else if (engine == "Google")
                    {
                        audioStream = await TTSAPI.GoogleStream(text);
                    }

                    if (audioStream != null && !IsMuted)
                    {
                        await PlayAudioStreamSync(audioStream);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"TTS Error: {ex.Message}");
                }
            }
        }

        private Task PlayAudioStreamSync(Stream stream)
        {
            var tcs = new TaskCompletionSource<bool>();

            try
            {
                _outputDevice = new WaveOutEvent();
                // Mp3FileReader doesn't always support arbitrary MP3 streams, StreamMediaFoundationReader is more robust.
                _audioFileReader = new StreamMediaFoundationReader(stream);
                
                _outputDevice.Init(_audioFileReader);
                
                _outputDevice.PlaybackStopped += (s, a) =>
                {
                    _audioFileReader?.Dispose();
                    _outputDevice?.Dispose();
                    
                    _audioFileReader = null;
                    _outputDevice = null;
                    stream.Dispose();

                    tcs.TrySetResult(true);
                };

                _outputDevice.Play();
            }
            catch (Exception)
            {
                stream.Dispose();
                tcs.TrySetResult(false);
            }

            return tcs.Task;
        }
    }
}
