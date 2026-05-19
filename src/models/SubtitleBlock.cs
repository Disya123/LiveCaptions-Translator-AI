using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace LiveCaptionsTranslator.models
{
    public class SubtitleBlock : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private string text = string.Empty;
        private bool isFadingOut = false;
        private Brush? foreground;

        public string Text
        {
            get => text;
            set
            {
                if (text != value)
                {
                    text = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsFadingOut
        {
            get => isFadingOut;
            set
            {
                if (isFadingOut != value)
                {
                    isFadingOut = value;
                    OnPropertyChanged();
                }
            }
        }

        public Brush? Foreground
        {
            get => foreground;
            set
            {
                if (foreground != value)
                {
                    foreground = value;
                    OnPropertyChanged();
                }
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
