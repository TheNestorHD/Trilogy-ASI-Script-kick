using Flurl.Http;
using GTAChaos.Effects;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GTAChaos.Utils
{
    /// <summary>
    /// Read-only Kick public chat connection. It needs only the channel slug;
    /// it does not authenticate or post messages.
    /// </summary>
    public class KickChatConnection : IStreamConnection
    {
        // Current public Pusher app key used by recent Kick chat clients.
        private const string PusherUrl =
            "wss://ws-us2.pusher.com/app/32cbd69e4b950bf97679?protocol=7&client=js&version=8.4.0&flash=false";

        private readonly string channelSlug;
        private int chatroomId;
        private volatile bool isConnected;
        private ClientWebSocket currentSocket;
        private CancellationTokenSource connectionCancellation;
        private Task receiveTask;
        private TaskCompletionSource<bool> subscriptionReady;

        /// <summary>Diagnostic for the most recent failed connection attempt.</summary>
        public string LastError { get; private set; } = string.Empty;
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private readonly object seenLock = new object();
        private readonly HashSet<string> seenMessageIds = new HashSet<string>();
        private readonly Queue<string> seenMessageOrder = new Queue<string>();

        private Shared.VOTING_MODE VotingMode;
        private VoteChoice lastChoice = VoteChoice.UNDETERMINED;
        private readonly ChatEffectVoting effectVoting = new ChatEffectVoting();
        private readonly HashSet<string> rapidFireVoters = new HashSet<string>();

        public KickChatConnection()
        {
            this.channelSlug = (Config.Instance().StreamClientID ?? string.Empty)
                .Trim().TrimStart('@').Trim().TrimEnd('/');
        }

        public async Task<bool> TryConnect()
        {
            this.Kill();
            this.LastError = string.Empty;

            if (string.IsNullOrWhiteSpace(this.channelSlug))
            {
                this.LastError = "The Kick channel slug is empty. Enter the channel name only, without a URL.";
                OnLoginError?.Invoke(this, EventArgs.Empty);
                return false;
            }

            try
            {
                this.chatroomId = await this.ResolveChatroomIdAsync();
            }
            catch (Exception ex)
            {
                this.LastError = "Could not retrieve the public Kick chatroom ID. " +
                    ex.GetBaseException().Message;
                Debug.WriteLine("Kick: could not resolve chatroom: " + ex);
                OnLoginError?.Invoke(this, EventArgs.Empty);
                return false;
            }

            var cancellation = new CancellationTokenSource();
            this.connectionCancellation = cancellation;
            this.subscriptionReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.receiveTask = Task.Run(() => this.RunConnectionLoopAsync(cancellation.Token));

            Task completed = await Task.WhenAny(this.subscriptionReady.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            if (completed != this.subscriptionReady.Task || !this.subscriptionReady.Task.Result)
            {
                if (string.IsNullOrWhiteSpace(this.LastError))
                {
                    this.LastError = "Timed out waiting for Kick/Pusher to confirm the chatroom subscription. " +
                        "The public WebSocket endpoint may be blocked or may have changed.";
                }

                this.Kill();
                OnLoginError?.Invoke(this, EventArgs.Empty);
                return false;
            }

            this.LastError = string.Empty;
            return true;
        }

        public bool IsConnected() => this.isConnected;

        private async Task<int> ResolveChatroomIdAsync()
        {
            string escapedSlug = Uri.EscapeDataString(this.channelSlug);
            // Kick's public web endpoints have varied over time. Try the
            // chatroom endpoint and both channel-info routes, which may contain
            // chatroom.id. The v1 fallback must include "/channels/{slug}".
            string[] urls =
            {
                $"https://kick.com/api/v2/channels/{escapedSlug}/chatroom",
                $"https://kick.com/api/v1/channels/{escapedSlug}",
                $"https://kick.com/api/v2/channels/{escapedSlug}"
            };

            Exception lastError = null;
            foreach (string url in urls)
            {
                try
                {
                    string json = await url
                        .WithHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0 Safari/537.36")
                        .WithHeader("Accept", "application/json, text/plain, */*")
                        .WithHeader("Referer", $"https://kick.com/{escapedSlug}")
                        .GetStringAsync();

                    JObject root = JObject.Parse(json);
                    JToken idToken =
                        root["chatroom"]?["id"] ??
                        root["data"]?["chatroom"]?["id"] ??
                        root["data"]?["id"] ??
                        root["chatroom_id"] ??
                        root["id"];

                    if (idToken != null && int.TryParse(idToken.ToString(), out int id) && id > 0)
                        return id;

                    lastError = new InvalidOperationException("Kick returned JSON without a chatroom ID.");
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Debug.WriteLine("Kick chatroom resolver failed for " + url + ": " + ex.Message);
                }
            }

            throw new InvalidOperationException(
                "Could not resolve the public Kick chatroom. Kick may have changed or blocked its web endpoint.",
                lastError);
        }

        private async Task RunConnectionLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                ClientWebSocket socket = new ClientWebSocket();
                this.currentSocket = socket;
                try
                {
                    socket.Options.SetRequestHeader("Origin", "https://kick.com");
                    await socket.ConnectAsync(new Uri(PusherUrl), token);

                    while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                    {
                        string raw = await ReceiveTextAsync(socket, token);
                        if (raw == null)
                            break;

                        await HandlePusherEnvelopeAsync(socket, raw, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    this.LastError = "Kick WebSocket error: " + ex.GetBaseException().Message;
                    Debug.WriteLine("Kick websocket disconnected: " + ex);
                    this.subscriptionReady?.TrySetResult(false);
                }
                finally
                {
                    // Drop the transport state quietly. The GUI must not treat
                    // a transient socket failure as a user-requested disconnect.
                    this.isConnected = false;
                    try { socket.Dispose(); } catch { }
                    if (ReferenceEquals(this.currentSocket, socket))
                        this.currentSocket = null;
                }

                if (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(3), token); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private async Task HandlePusherEnvelopeAsync(ClientWebSocket socket, string raw, CancellationToken token)
        {
            JObject envelope;
            try { envelope = JObject.Parse(raw); }
            catch { return; }

            string eventName = envelope["event"]?.ToString() ?? string.Empty;

            if (eventName == "pusher:connection_established")
            {
                await SendPusherJsonAsync(socket, new
                {
                    @event = "pusher:subscribe",
                    data = new { channel = $"chatrooms.{this.chatroomId}.v2", auth = string.Empty }
                }, token);
                return;
            }

            if (eventName == "pusher:error" ||
                eventName == "pusher:subscription_error" ||
                eventName == "pusher_internal:subscription_error")
            {
                string detail = envelope["data"]?.ToString();
                this.LastError = $"Kick/Pusher rejected the chat subscription ({eventName}). {detail}";
                Debug.WriteLine(this.LastError);
                this.subscriptionReady?.TrySetResult(false);
                return;
            }

            if (eventName == "pusher_internal:subscription_succeeded")
            {
                bool wasConnected = this.isConnected;
                this.isConnected = true;
                this.LastError = string.Empty;
                this.subscriptionReady?.TrySetResult(true);
                if (!wasConnected)
                    OnConnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (eventName == "pusher:ping")
            {
                await SendPusherJsonAsync(socket, new { @event = "pusher:pong", data = new { } }, token);
                return;
            }

            if (eventName.IndexOf("ChatMessage", StringComparison.OrdinalIgnoreCase) >= 0)
                this.ProcessChatEvent(envelope);
        }

        private async Task SendPusherJsonAsync(ClientWebSocket socket, object value, CancellationToken token)
        {
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(value);
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            await this.sendLock.WaitAsync(token);
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            }
            finally
            {
                this.sendLock.Release();
            }
        }

        private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken token)
        {
            byte[] buffer = new byte[8192];
            using (var stream = new System.IO.MemoryStream())
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return null;
                    stream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private void ProcessChatEvent(JObject envelope)
        {
            JToken data = envelope["data"];
            JObject payload = data as JObject;
            if (payload == null && data != null)
            {
                try { payload = JObject.Parse(data.ToString()); }
                catch { return; }
            }
            if (payload == null)
                return;

            JObject sender = payload["sender"] as JObject ?? payload["user"] as JObject;
            string username =
                sender?["username"]?.ToString() ??
                sender?["slug"]?.ToString() ??
                payload["username"]?.ToString() ??
                payload["sender_username"]?.ToString();

            string content = payload["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(content) && payload["message"] is JObject messageObject)
            {
                content = messageObject["content"]?.ToString() ??
                          messageObject["message"]?.ToString();
            }

            string messageId = payload["id"]?.ToString() ??
                               payload["message_id"]?.ToString();

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(content))
                return;

            if (!string.IsNullOrWhiteSpace(messageId) && !this.RememberMessage(messageId))
                return;

            Debug.WriteLine($"[Kick] {username}: {content}");
            this.OnChatMessage(new KickChatItem(username, content, messageId));
        }

        private bool RememberMessage(string id)
        {
            lock (this.seenLock)
            {
                if (!this.seenMessageIds.Add(id))
                    return false;
                this.seenMessageOrder.Enqueue(id);
                while (this.seenMessageOrder.Count > 4096)
                    this.seenMessageIds.Remove(this.seenMessageOrder.Dequeue());
                return true;
            }
        }

        private class KickChatItem
        {
            public string Author { get; }
            public string Message { get; }
            public string MessageId { get; }

            public KickChatItem(string author, string message, string messageId)
            {
                this.Author = author;
                this.Message = message;
                this.MessageId = messageId;
            }
        }

        private void OnChatMessage(KickChatItem chatItem)
        {
            string username = chatItem.Author;
            string message = this.RemoveSpecialCharacters(chatItem.Message, true);

            if (this.VotingMode == Shared.VOTING_MODE.RAPID_FIRE)
            {
                if (this.rapidFireVoters.Contains(username))
                {
                    return;
                }

                AbstractEffect effect = EffectDatabase.GetByWord(message, Config.Instance().StreamAllowOnlyEnabledEffectsRapidFire);
                if (effect == null || !effect.IsRapidFire())
                {
                    return;
                }

                this.RapidFireEffect(new RapidFireEventArgs()
                {
                    Effect = effect.SetSubtext(this.RemoveSpecialCharacters(chatItem.Author))
                });

                this.rapidFireVoters.Add(username);

                return;
            }
            else if (this.VotingMode == Shared.VOTING_MODE.VOTING)
            {
                int choice = this.TryParseUserChoice(message);
                if (choice is >= 0 and <= 2)
                {
                    this.effectVoting?.TryAddVote(username, choice);
                }
            }
        }

        public int GetRemaining() => 0;

        private VoteChoice GetVoteChoice(int id)
        {
            return id switch
            {
                0 => VoteChoice.FIRST,
                1 => VoteChoice.SECOND,
                2 => VoteChoice.THIRD,
                _ => VoteChoice.NONE,
            };
        }

        public List<IVotingElement> GetVotedEffects()
        {
            this.lastChoice = VoteChoice.NONE;

            List<IVotingElement> elements = Config.Instance().StreamMajorityVotes ? this.effectVoting.GetMajorityVotes() : this.effectVoting.GetTrulyRandomVotes();
            foreach (IVotingElement e in elements)
            {
                e.GetEffect().SetSubtext($"{e.GetPercentage()}%");

                this.lastChoice |= this.GetVoteChoice(e.GetId());
            }

            return elements;
        }

        public void Kill()
        {
            CancellationTokenSource cancellation = this.connectionCancellation;
            ClientWebSocket socket = this.currentSocket;
            bool hadSession = cancellation != null || socket != null || this.receiveTask != null;

            try { cancellation?.Cancel(); } catch { }
            try { socket?.Abort(); } catch { }

            this.connectionCancellation = null;
            this.currentSocket = null;
            this.isConnected = false;

            if (hadSession)
                OnDisconnected?.Invoke(this, EventArgs.Empty);
        }

        public void SendEffectVotingToGame(bool undetermined = true)
        {
            if (this.effectVoting.IsEmpty())
            {
                return;
            }

            this.effectVoting.GetVotes(out string[] effects, out int[] votes, undetermined);

            if (Shared.Sync != null)
            {
                Shared.Sync.SendVotes(effects, votes, this.lastChoice, !undetermined);
            }
            else
            {
                WebsocketHandler.INSTANCE.SendVotes(effects, votes, this.lastChoice);
            }
        }

        private void SendMessage(string text)
        {
            // Empty method because we are just a chat listener
        }

        public void SetVoting(Shared.VOTING_MODE votingMode, int untilRapidFire = -1, List<IVotingElement> votingElements = null)
        {
            this.VotingMode = votingMode;

            if (this.VotingMode == Shared.VOTING_MODE.VOTING)
            {
                this.effectVoting.Clear();
                this.effectVoting.GenerateRandomEffects();
                this.lastChoice = VoteChoice.UNDETERMINED;

                if (Config.Instance().StreamCombineChatMessages)
                {
                    string messageToSend = "Voting has started! Type 1, 2 or 3 (or #1, #2, #3) to vote for one of the effects! ";

                    foreach (ChatVotingElement element in this.effectVoting.GetVotingElements())
                    {
                        string description = element.Effect.GetDisplayName(DisplayNameType.STREAM);
                        messageToSend += $"#{element.Id + 1}: {description}. ";
                    }

                    this.SendMessage(messageToSend);
                }
                else
                {
                    this.SendMessage("Voting has started! Type 1, 2 or 3 (or #1, #2, #3) to vote for one of the effects!");

                    foreach (ChatVotingElement element in this.effectVoting.GetVotingElements())
                    {
                        string description = element.Effect.GetDisplayName(DisplayNameType.STREAM);
                        this.SendMessage($"#{element.Id + 1}: {description}");
                    }
                }
            }
            else if (this.VotingMode == Shared.VOTING_MODE.RAPID_FIRE)
            {
                this.rapidFireVoters.Clear();
                this.SendMessage("ATTENTION, ALL GAMERS! RAPID-FIRE HAS BEGUN! VALID EFFECTS WILL BE ENABLED FOR 15 SECONDS!");
            }
            else
            {
                if (votingElements != null && votingElements.Count > 0)
                {
                    this.SendEffectVotingToGame(false);

                    string allEffects = string.Join(", ", votingElements.Select(e => e.GetEffect().GetDisplayName(DisplayNameType.STREAM)));

                    if (Config.Instance().StreamEnableRapidFire)
                    {
                        this.SendMessage($"Cooldown has started! ({untilRapidFire} until Rapid-Fire) - Enabled effects: {allEffects}");
                        if (untilRapidFire == 1)
                        {
                            this.SendMessage("Rapid-Fire is coming up! Get your cheats ready! - List of all effects: https://bit.ly/gta-sa-chaos-mod");
                        }
                    }
                    else
                    {
                        this.SendMessage($"Cooldown has started! - Enabled effects: {allEffects}");
                    }
                }
                else
                {
                    if (Config.Instance().StreamEnableRapidFire)
                    {
                        this.SendMessage($"Cooldown has started! ({untilRapidFire} until Rapid-Fire)");
                        if (untilRapidFire == 1)
                        {
                            this.SendMessage("Rapid-Fire is coming up! Get your cheats ready! - List of all effects: https://bit.ly/gta-sa-chaos-mod");
                        }
                    }
                    else
                    {
                        this.SendMessage($"Cooldown has started!");
                    }
                }
            }
        }

        private string RemoveSpecialCharacters(string text, bool noSpaces = true)
        {
            string regex = noSpaces ? @"[^A-Za-z0-9]" : @"[^A-Za-z0-9 ]";
            return Regex.Replace(text, regex, "");
        }

        private int TryParseUserChoice(string text)
        {
            try
            {
                return int.Parse(text) - 1;
            }
            catch
            {
                return -1;
            }
        }

        private class ChatEffectVoting
        {
            private readonly List<ChatVotingElement> votingElements = new();
            private readonly Dictionary<string, ChatVotingElement> voters = new();

            public bool IsEmpty() => this.votingElements.Count == 0;

            public void Clear()
            {
                this.votingElements.Clear();
                this.voters.Clear();
            }

            public List<ChatVotingElement> GetVotingElements() => this.votingElements;

            public int GetTotalVotes()
            {
                int votes = 0;
                foreach (ChatVotingElement element in this.votingElements)
                {
                    votes += element.Voters.Count;
                }

                return votes;
            }

            public bool ContainsEffect(AbstractEffect effect) => this.votingElements.Any(e => e.Effect.GetDisplayName(DisplayNameType.STREAM).Equals(effect.GetDisplayName(DisplayNameType.STREAM)));

            public void AddEffect(AbstractEffect effect) => this.votingElements.Add(new ChatVotingElement(this.votingElements.Count, effect));

            public void GetVotes(out string[] effects, out int[] votes, bool undetermined = false)
            {
                // We aren't posting effects to YouTube chat so we can't hide them ingame
                undetermined = false;

                ChatVotingElement[] votingElements = this.GetVotingElements().ToArray();

                effects = new string[]
                {
                    undetermined ? "???" : votingElements[0].Effect.GetDisplayName(),
                    undetermined ? "???" : votingElements[1].Effect.GetDisplayName(),
                    undetermined ? "???" : votingElements[2].Effect.GetDisplayName()
                };

                votes = new int[]
                {
                    votingElements[0].Voters.Count,
                    votingElements[1].Voters.Count,
                    votingElements[2].Voters.Count
                };
            }

            public void GenerateRandomEffects()
            {
                int possibleEffects = Math.Min(3, EffectDatabase.EnabledEffects.Count);
                int attempts = 0;
                while (this.votingElements.Count != possibleEffects)
                {
                    AbstractEffect effect = EffectDatabase.GetRandomEffect(true, 0, true);
                    if (effect.IsTwitchEnabled() && !this.ContainsEffect(effect))
                    {
                        this.AddEffect(effect);
                    }

                    if (attempts++ >= 10)
                    {
                        EffectDatabase.ResetEffectCooldowns();
                    }
                    else if (attempts++ >= 20)
                    {
                        break;
                    }
                }

                while (this.votingElements.Count < 3)
                {
                    AbstractEffect effect = EffectDatabase.GetRandomEffect(false, 0, true);
                    if (effect.IsTwitchEnabled() && !this.ContainsEffect(effect))
                    {
                        this.AddEffect(effect);
                    }
                }
            }

            public List<IVotingElement> GetMajorityVotes()
            {
                // If there are effects that have the same amount of votes, get a random one
                int maxVotes = 0;
                ChatVotingElement[] elements = this.votingElements.OrderByDescending(e =>
                {
                    if (e.Voters.Count > maxVotes)
                    {
                        maxVotes = e.Voters.Count;
                    }

                    return e.Voters.Count;
                }).Where(e => e.Voters.Count == maxVotes).ToArray();

                List<IVotingElement> votes = new();
                if (Config.Instance().StreamEnableMultipleEffects)
                {
                    votes.AddRange(elements);
                }
                else
                {
                    votes.Add(elements[new Random().Next(elements.Count())]);
                }

                return votes;
            }

            public List<IVotingElement> GetTrulyRandomVotes()
            {
                List<IVotingElement> votes = new();

                // Calculate total votes
                int totalVotes = 0;
                foreach (ChatVotingElement e in this.votingElements)
                {
                    totalVotes += e.Voters.Count;
                }

                if (totalVotes == 0)
                {
                    return votes;
                }

                /*
                 * 130 140 50 - total 320
                 * rN 180
                 * rN > 130 -> rN -= 130
                 * 180 - 130 = 50
                 * rN <= 140 -> Add, then break
                 * 
                 * 0 1 0 - total 1
                 * rN 1
                 * rN > 0 -> rN -= 0
                 * 1 - 0 = 1
                 * rN <= 1 -> Add, then break
                 * 
                 * 
                 * 40 0 20 - total 60
                 * rN 45
                 * rN > 40 -> rN -= 40
                 * 45 - 40 = 5
                 * rN > 0 -> rN -= 0
                 * 5 - 0 = 0
                 * rN <= 20 -> Add, then break
                 */

                // Calculate random number
                int randomNumber = new Random().Next(totalVotes) + 1;

                foreach (ChatVotingElement e in this.votingElements)
                {
                    if (randomNumber > e.Voters.Count)
                    {
                        randomNumber -= e.Voters.Count;
                        continue;
                    }

                    votes.Add(e);
                    break;
                }

                return votes;
            }

            public void TryAddVote(string username, int effectChoice)
            {
                foreach (ChatVotingElement e in this.votingElements)
                {
                    e.RemoveVoter(username);
                }

                this.votingElements[effectChoice].AddVoter(username);
                this.voters[username] = this.votingElements[effectChoice];

                foreach (ChatVotingElement element in this.votingElements)
                {
                    element.Percentage = (int)Math.Round((double)element.Voters.Count / this.GetTotalVotes() * 100);
                }
            }
        }

        public class ChatVotingElement : IVotingElement
        {
            public int Id { get; set; }

            public AbstractEffect Effect { get; set; }

            public HashSet<string> Voters { get; set; }

            public int Percentage { get; set; }

            public ChatVotingElement(int id, AbstractEffect effect)
            {
                this.Id = id;
                this.Effect = effect;
                this.Voters = new HashSet<string>();
            }

            public int GetId() => this.Id;

            public AbstractEffect GetEffect() => this.Effect;

            public int GetVotes() => this.Voters.Count;

            public int GetPercentage() => this.Percentage;

            public bool ContainsVoter(string username) => this.Voters.Contains(username);

            public void AddVoter(string username) => this.Voters.Add(username);

            public void RemoveVoter(string username) => this.Voters.Remove(username);
        }


        public event EventHandler<EventArgs> OnConnected;
        public event EventHandler<EventArgs> OnDisconnected;
        public event EventHandler<EventArgs> OnLoginError;
        public event EventHandler<RapidFireEventArgs> OnRapidFireEffect;

        public virtual void RapidFireEffect(RapidFireEventArgs e) =>
            OnRapidFireEffect?.Invoke(this, e);
    }
}
