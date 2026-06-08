using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace YoloWithWPF.Enums
{
    public enum ConnectStatusEnum
    {
        // 연결 안됨
        Disconnected,
        // 연결 중
        Connecting,
        // 연결됨
        Connected,
        // 재연결 중
        Reconnecting,
        // 연결 실패
        ConnectionFailed,
        // 자동 재연결 실패
        AutoReconnectFailed,
        // 프레임 수신 중지
        FrameReceiveStopped
    }
}
