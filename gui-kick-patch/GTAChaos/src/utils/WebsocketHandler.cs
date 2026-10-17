// Copyright (c) 2019 Lordmau5
using Fleck;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTAChaos.Utils
{
    public class SocketMessageEventArgs : EventArgs
    {
        public string Data { get; set; }
    }

    public enum VoteChoice
    {
        UNDETERMINED = -1,
        NONE = 0,

        FIRST = 1 << 0,
        SECOND = 1 << 1,
        THIRD = 1 << 2,

        FIRST_SECOND = FIRST | SECOND,
        FIRST_THIRD = FIRST | THIRD,
        SECOND_THIRD = SECOND | THIRD,

        ALL = FIRST | SECOND | THIRD,
    }

    public class WebsocketHandler
    {
        public static WebsocketHandler INSTANCE = new();

        public event EventHandler<SocketMessageEventArgs> OnSocketMessage;

        private WebSocketServer server;
        private readonly List<IWebSocketConnection> sockets = new();
        private readonly List<string> socketBuffer = new();
        private readonly object overlayLock = new();
        private readonly HashSet<IWebSocketConnection> obsOverlaySockets = new();

        /// <summary>True while at least one OBS voting overlay is connected.</summary>
        public bool IsObsVotingOverlayActive
        {
            get
            {
                lock (this.overlayLock)
                    return this.obsOverlaySockets.Count > 0;
            }
        }

        public void CreateWebsocketServer()
        {
            if (this.server is not null)
            {
                return;
            }

            int port = Config.Instance().WebsocketPort;
            if (!this.CheckIfPortAvailable(port))
            {
                MessageBox.Show($"Couldn't create websocket server on port {port}. Is it in use?", "Websocket Error");
                return;
            }

            this.server = new WebSocketServer($"ws://0.0.0.0:{Config.Instance().WebsocketPort}");

            this.sockets.Clear();

            this.server.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    this.sockets.Add(socket);
                    this.SendWebsocketBuffer();
                };
                socket.OnClose = () => this.RemoveSocket(socket);
                socket.OnError = error =>
                {
                    this.RemoveSocket(socket);
                    socket.Close();
                };
                socket.OnMessage = message => this.HandleClientMessage(socket, message);
            });
        }

        private void HandleClientMessage(IWebSocketConnection socket, string message)
        {
            // The OBS browser source announces itself so the game can conceal option names.
            // Socket close/error handling removes it from the active set automatically.
            try
            {
                JObject json = JObject.Parse(message);
                if (string.Equals(json["type"]?.ToString(), "obs-overlay", StringComparison.OrdinalIgnoreCase))
                {
                    string state = json["state"]?.ToString() ?? string.Empty;
                    lock (this.overlayLock)
                    {
                        if (string.Equals(state, "active", StringComparison.OrdinalIgnoreCase))
                            this.obsOverlaySockets.Add(socket);
                        else
                            this.obsOverlaySockets.Remove(socket);
                    }
                    return;
                }
            }
            catch
            {
                // Preserve the existing message event behavior for non-overlay clients.
            }

            OnSocketMessage?.Invoke(this, new SocketMessageEventArgs { Data = message });
        }

        private void RemoveSocket(IWebSocketConnection socket)
        {
            this.sockets.Remove(socket);
            lock (this.overlayLock)
                this.obsOverlaySockets.Remove(socket);
        }

        public bool CheckIfPortAvailable(int port)
        {
            IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
            IPEndPoint[] end_point = properties.GetActiveTcpListeners();
            List<int> ports = end_point.Select(p => p.Port).ToList<int>();

            return !ports.Contains(port);
        }

        public void RestartWebsocketServer()
        {
            this.server?.Dispose();
            this.server = null;

            this.CreateWebsocketServer();
        }

        private void SendToAllClients(string text)
        {
            foreach (IWebSocketConnection socket in this.sockets)
            {
                socket.Send(text);
            }
        }

        private void SendWebsocketBuffer()
        {
            if (this.socketBuffer.Count > 0)
            {
                foreach (string buffer in this.socketBuffer)
                {
                    this.SendToAllClients(buffer);
                }

                this.socketBuffer.Clear();
            }
        }

        public void SendDataToWebsocket(JObject jsonObject)
        {
            Task.Run(() =>
            {
                string json = JsonConvert.SerializeObject(jsonObject);

                if (this.sockets.Count > 0)
                {
                    this.SendWebsocketBuffer();

                    this.SendToAllClients(json);
                }
                else
                {
                    string type = jsonObject["type"]?.ToObject<string>() ?? string.Empty;
                    // Live chat and chat settings should never be replayed later as
                    // stale backlog. The game requests fresh settings on each connect.
                    if (type != "time" && type != "chat" && type != "chat-config")
                    {
                        this.socketBuffer.Add(json);
                    }
                }
            });
        }

        public void SendTimeToGame(int remaining, int cooldown = 0, string mode = "")
        {
            JObject jsonObject = JObject.FromObject(new
            {
                type = "time",
                data = new
                {
                    remaining,
                    cooldown,
                    mode
                }
            });

            this.SendDataToWebsocket(jsonObject);
        }

        public void SendStreamChatConfig(bool enabled, bool hideDuringHideChaosGui, int maxMessages, int durationSeconds, int fontSizePercent)
        {
            JObject jsonObject = JObject.FromObject(new
            {
                type = "chat-config",
                data = new
                {
                    enabled,
                    hideDuringHideChaosUi = hideDuringHideChaosGui,
                    maxMessages,
                    durationSeconds,
                    fontSizePercent
                }
            });

            this.SendDataToWebsocket(jsonObject);
        }

        public void SendChatMessage(string platform, string username, string message)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(message))
                return;

            JObject jsonObject = JObject.FromObject(new
            {
                type = "chat",
                data = new
                {
                    platform = platform ?? "Unknown",
                    username,
                    message
                }
            });

            this.SendDataToWebsocket(jsonObject);
        }

        public void SendVotes(string[] effects, int[] votes, VoteChoice pickedChoice = VoteChoice.UNDETERMINED)
        {
            JObject jsonObject = JObject.FromObject(new
            {
                type = "votes",
                data = new
                {
                    effects,
                    votes,
                    pickedChoice
                }
            });

            this.SendDataToWebsocket(jsonObject);
        }

        /// <summary>
        /// Sends unredacted option names only to an active OBS overlay. The regular
        /// "votes" event may intentionally contain ??? while the overlay is connected.
        /// </summary>
        public void SendOverlayVotes(string[] effects, int[] votes, VoteChoice pickedChoice = VoteChoice.UNDETERMINED)
        {
            if (!this.IsObsVotingOverlayActive)
                return;

            JObject jsonObject = JObject.FromObject(new
            {
                type = "obs-votes",
                data = new
                {
                    effects,
                    votes,
                    pickedChoice
                }
            });

            this.SendDataToWebsocket(jsonObject);
        }

        public void SendEffectToGame(string effectID, object effectData = null, int duration = -1, string displayName = "", string subtext = "", bool rapidFire = false)
        {
            // Enforce the stream-mode limit at the final outgoing boundary as well,
            // so fixed/custom effect durations cannot bypass the 30-second cap.
            if (Shared.IsStreamMode && duration > 0)
            {
                duration = Math.Min(duration, 1000 * 30);
            }

            if (rapidFire)
            {
                duration = Math.Min(duration, 1000 * 15); // 15 seconds max. for Rapid-Fire
            }

            JObject jsonObject = JObject.FromObject(new
            {
                type = "effect",
                data = new
                {
                    effectID,
                    effectData = effectData ?? (new { }),
                    duration,
                    displayName = this.IsNullOrEmpty(displayName) ? effectID : displayName,
                    subtext
                }
            });

            this.SendDataToWebsocket(jsonObject);
        }

        private bool IsNullOrEmpty(string value) => value == null || value.Length == 0;
    }
}
