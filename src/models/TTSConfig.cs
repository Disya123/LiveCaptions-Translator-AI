using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LiveCaptionsTranslator.models
{
    public class OpenAITTSConfig : INotifyPropertyChanged
    {
        private string apiKey = "";
        private string apiUrl = "https://api.openai.com/v1/audio/speech";
        private string model = "tts-1";
        private string voice = "alloy";

        public string ApiKey
        {
            get => apiKey;
            set
            {
                apiKey = value;
                OnPropertyChanged();
            }
        }
        
        public string ApiUrl
        {
            get => apiUrl;
            set
            {
                apiUrl = value;
                OnPropertyChanged();
            }
        }

        public string Model
        {
            get => model;
            set
            {
                model = value;
                OnPropertyChanged();
            }
        }

        public string Voice
        {
            get => voice;
            set
            {
                voice = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            Translator.Setting?.Save();
        }
    }

    public class GoogleTTSConfig : INotifyPropertyChanged
    {
        private string apiKey = "";
        private string voiceName = "en-US-Wavenet-D";
        private string languageCode = "en-US";

        public string ApiKey
        {
            get => apiKey;
            set
            {
                apiKey = value;
                OnPropertyChanged();
            }
        }

        public string VoiceName
        {
            get => voiceName;
            set
            {
                voiceName = value;
                OnPropertyChanged();
            }
        }

        public string LanguageCode
        {
            get => languageCode;
            set
            {
                languageCode = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            Translator.Setting?.Save();
        }
    }
}
