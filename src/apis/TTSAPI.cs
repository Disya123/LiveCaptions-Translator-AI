using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.apis
{
    public static class TTSAPI
    {
        private static readonly HttpClient client = new HttpClient();

        public static async Task<Stream?> OpenAIStream(string text)
        {
            var config = Translator.Setting.OpenAITTSConfig;
            
            var requestData = new
            {
                model = config.Model,
                input = text,
                voice = config.Voice,
                response_format = "mp3"
            };

            string jsonString = JsonSerializer.Serialize(requestData);
            using var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, config.ApiUrl);
            request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
            request.Content = content;

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync();
            var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            memoryStream.Position = 0;
            return memoryStream;
        }

        public static async Task<Stream?> GoogleStream(string text)
        {
            var config = Translator.Setting.GoogleTTSConfig;
            string url = $"https://texttospeech.googleapis.com/v1/text:synthesize?key={config.ApiKey}";

            var requestData = new
            {
                input = new { text = text },
                voice = new { languageCode = config.LanguageCode, name = config.VoiceName },
                audioConfig = new { audioEncoding = "MP3" }
            };

            string jsonString = JsonSerializer.Serialize(requestData);
            using var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            string responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            
            if (doc.RootElement.TryGetProperty("audioContent", out JsonElement audioContentElement))
            {
                string base64Audio = audioContentElement.GetString() ?? "";
                byte[] audioBytes = Convert.FromBase64String(base64Audio);
                return new MemoryStream(audioBytes);
            }

            return null;
        }
    }
}
