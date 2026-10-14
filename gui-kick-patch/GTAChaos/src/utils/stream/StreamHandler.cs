namespace GTAChaos.Utils.Stream
{
    public class StreamHandler
    {
        public enum STREAM_MODE
        {
            TWITCH,
            YOUTUBE,
            KICK
        };

        public static IStreamConnection GetStreamConnection()
        {
            switch (Config.Instance().StreamMode)
            {
                case STREAM_MODE.TWITCH:
                    {
                        return Config.Instance().TwitchUsePolls ? new TwitchPollConnection() : new TwitchChatConnection();
                    }
                case STREAM_MODE.YOUTUBE: return new YouTubeChatConnection();
                case STREAM_MODE.KICK: return new KickChatConnection();
                default: return new DebugConnection();
            }
        }

        public static STREAM_MODE GetStreamMode() => Config.Instance().StreamMode;

        public static bool IsTwitchMode() => GetStreamMode() == STREAM_MODE.TWITCH;

        public static bool IsYouTubeMode() => GetStreamMode() == STREAM_MODE.YOUTUBE;

        public static bool IsKickMode() => GetStreamMode() == STREAM_MODE.KICK;
    }
}
