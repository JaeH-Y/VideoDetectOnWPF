using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using YoloWithWPF.Enums;
using YoloWithWPF.Services;

namespace YoloWithWPF.Models
{
    public class CameraStreamItem : ObservableObject
    {
        public string Name { get; set; }

        public string RtspUrl { get; set; }

        private string _resolution = "-";
        public string Resolution
        {
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

        private ConnectStatusEnum _status = ConnectStatusEnum.Disconnected;
        public ConnectStatusEnum Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    SetProperty(ref _status, value);
                    OnPropertyChanged(nameof(IsStatusBlinking));
                    SetConnectStatus(value);
                }
            }
        }

        public bool IsStatusBlinking =>
            Status is ConnectStatusEnum.Connecting
                or ConnectStatusEnum.Reconnecting
                or ConnectStatusEnum.FrameReceiveStopped
                or ConnectStatusEnum.AutoReconnectFailed;

        private string _connectStatus = "연결 끊김";
        public string ConnectStatus
        {
            get => _connectStatus;
            set => SetProperty(ref _connectStatus, value);
        }

        private int _reconnectCount;
        public int ReconnectCount
        {
            get => _reconnectCount;
            set 
            {
                if(value != _reconnectCount)
                {
                    SetProperty(ref _reconnectCount, value);
                    if(ConnectStatus.Equals("재연결 중..."))
                    {
                        ConnectStatus = $"재연결 중... {ReconnectCount}/{Service.MaxReconnectAttempts}회";
                    }
                }
            }
        }

        private Visibility _reconnectVisibility = Visibility.Hidden;
        public Visibility ReconnectVisibility
        {
            get => _reconnectVisibility;
            set => SetProperty(ref _reconnectVisibility, value);
        }

        private bool _reconnectEnabled = false;
        public bool ReconnectEnabled
        {
            get => _reconnectEnabled;
            set => SetProperty(ref _reconnectEnabled, value);
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

        public void SetConnectStatus(ConnectStatusEnum status)
        {
            switch (status)
            {
                case ConnectStatusEnum.Connected:
                    ConnectStatus = "연결됨";
                    ReconnectVisibility = Visibility.Hidden;
                    ReconnectEnabled = false;
                    ReconnectCount = 0;
                    break;
                case ConnectStatusEnum.Connecting:
                    ConnectStatus = "연결 중...";
                    break;
                case ConnectStatusEnum.ConnectionFailed:
                    ConnectStatus = "연결 실패";
                    break;
                case ConnectStatusEnum.Disconnected:
                    ConnectStatus = "연결 끊김";
                    break;
                case ConnectStatusEnum.Reconnecting:
                    ConnectStatus = "재연결 중...";
                    break;
                case ConnectStatusEnum.AutoReconnectFailed:
                    ConnectStatus = "자동 재연결 실패";
                    ReconnectVisibility = Visibility.Visible;
                    ReconnectEnabled = true;
                    break;
                case ConnectStatusEnum.FrameReceiveStopped:
                    ConnectStatus = "영상 끊김";
                    break;
                default:
                    ConnectStatus = "알 수 없는 상태";
                    break;
            }
        }
    }
}
