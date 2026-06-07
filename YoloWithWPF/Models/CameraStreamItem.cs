using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using YoloWithWPF.Services;

namespace YoloWithWPF.Models
{
    public class CameraStreamItem : ObservableObject
    {
        public string Name { get; set; }

        public string RtspUrl { get; set; }

        private string _resolution;
        public string Resolution { 
            get => _resolution;
            set => SetProperty(ref _resolution, value);
        }  // ex) "1920X1080"

        private double _fps;
        public double Fps
        {
            get => _fps;
            set => SetProperty(ref _fps, value);
        }

        private BitmapSource? _thumbnail;
        public BitmapSource? Thumbnail
        {
            get => _thumbnail;
            set => SetProperty(ref _thumbnail, value);
        }

        public int FrameCount { get; set; }

        public CameraService Service { get; }

        public CameraStreamItem(string name, string rtspUrl, string resolution)
        {
            Name = name;
            RtspUrl = rtspUrl;
            Resolution = resolution;
            Service = new CameraService();
        }
    }
}
