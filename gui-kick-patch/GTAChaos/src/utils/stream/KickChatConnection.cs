using GTAChaos.Effects;
using Newtonsoft.Json.Linq;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Windows.Forms;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        // Read Kick's rendered chat through the browser session instead of opening
        // a separate raw Pusher connection. This uses the same page the user sees.
        private readonly string channelSlug;
        private volatile bool isConnected;
        private TaskCompletionSource<bool> subscriptionReady;
        private KickChatBrowserForm browserForm;
        private bool suppressBrowserClose;

        /// <summary>Diagnostic for the most recent browser/chat connection attempt.</summary>
        public string LastError { get; private set; } = string.Empty;

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

            this.subscriptionReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                this.browserForm = new KickChatBrowserForm(this.channelSlug);
                this.browserForm.MessageReceived += this.HandleBrowserMessage;
                this.browserForm.BrowserFailed += this.HandleBrowserFailure;
                this.browserForm.FormClosed += this.HandleBrowserClosed;

                Form owner = Form.ActiveForm;
                if (owner != null && owner != this.browserForm)
                    this.browserForm.Show(owner);
                else
                    this.browserForm.Show();
            }
            catch (Exception ex)
            {
                this.LastError = "Could not open the embedded Kick browser. " + ex.GetBaseException().Message;
                this.Kill();
                OnLoginError?.Invoke(this, EventArgs.Empty);
                return false;
            }

            Task completed = await Task.WhenAny(
                this.subscriptionReady.Task,
                Task.Delay(TimeSpan.FromSeconds(60)));

            if (completed != this.subscriptionReady.Task || !this.subscriptionReady.Task.Result)
            {
                if (string.IsNullOrWhiteSpace(this.LastError))
                {
                    this.LastError =
                        "The embedded Kick page did not expose a readable chat panel within 60 seconds. " +
                        "Check that the channel page loaded and that the channel is live.";
                }

                this.Kill();
                OnLoginError?.Invoke(this, EventArgs.Empty);
                return false;
            }

            this.LastError = string.Empty;
            return true;
        }

        public bool IsConnected() => this.isConnected;

        private void HandleBrowserMessage(JObject message)
        {
            string type = message?["type"]?.ToString() ?? string.Empty;

            if (type == "chat-ready")
            {
                bool wasConnected = this.isConnected;
                this.isConnected = true;
                this.LastError = string.Empty;
                this.subscriptionReady?.TrySetResult(true);
                if (!wasConnected)
                    OnConnected?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (type == "chat-error")
            {
                string detail = message["message"]?.ToString();
                this.HandleBrowserFailure(string.IsNullOrWhiteSpace(detail)
                    ? "Kick's rendered chat container could not be found."
                    : detail);
                return;
            }

            if (type != "chat-message")
                return;

            string username = message["username"]?.ToString();
            string content = message["content"]?.ToString();
            string id = message["id"]?.ToString();

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(content))
                return;

            JObject payload = new JObject();
            payload["id"] = string.IsNullOrWhiteSpace(id)
                ? Guid.NewGuid().ToString("N")
                : id;
            payload["sender"] = new JObject(new JProperty("username", username));
            payload["content"] = content;

            JObject envelope = new JObject(new JProperty("data", payload));
            this.ProcessChatEvent(envelope);
        }

        private void HandleBrowserFailure(string detail)
        {
            this.LastError = "Embedded Kick browser: " + detail;
            Debug.WriteLine(this.LastError);

            if (!this.isConnected)
                this.subscriptionReady?.TrySetResult(false);
        }

        private void HandleBrowserClosed(object sender, FormClosedEventArgs e)
        {
            if (ReferenceEquals(sender, this.browserForm))
                this.browserForm = null;

            if (this.suppressBrowserClose)
                return;

            bool wasConnected = this.isConnected;
            this.isConnected = false;
            this.LastError = "The embedded Kick chat window was closed.";
            this.subscriptionReady?.TrySetResult(false);

            if (wasConnected)
                OnDisconnected?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Visible browser window for Kick. New message DOM nodes are normalized into
        /// the same chat event shape consumed by the existing voting code.
        /// </summary>
        private sealed class KickChatBrowserForm : Form
        {
            private readonly string channelSlug;
            private readonly WebView2 webView;
            private bool reportedNavigationFailure;

            public event Action<JObject> MessageReceived;
            public event Action<string> BrowserFailed;

            public KickChatBrowserForm(string channelSlug)
            {
                this.channelSlug = channelSlug;
                this.Text = "Kick Chat Reader — " + channelSlug;
                this.StartPosition = FormStartPosition.CenterScreen;
                this.Size = new System.Drawing.Size(1100, 820);
                this.MinimumSize = new System.Drawing.Size(760, 520);
                this.ShowInTaskbar = true;

                this.webView = new WebView2
                {
                    Dock = DockStyle.Fill
                };
                this.Controls.Add(this.webView);
                this.Shown += this.KickChatBrowserForm_Shown;
            }

            private async void KickChatBrowserForm_Shown(object sender, EventArgs e)
            {
                try
                {
                    await this.webView.EnsureCoreWebView2Async();

                    this.webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    this.webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    this.webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                    this.webView.CoreWebView2.IsMuted = true;
                    this.webView.CoreWebView2.WebMessageReceived += this.CoreWebView2_WebMessageReceived;
                    this.webView.CoreWebView2.NavigationCompleted += this.CoreWebView2_NavigationCompleted;

                    await this.webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ChatObserverScript);
                    this.webView.CoreWebView2.Navigate("https://kick.com/" + Uri.EscapeDataString(this.channelSlug));
                }
                catch (Exception ex)
                {
                    this.ShowBrowserError(
                        "No se pudo iniciar el navegador integrado. " +
                        "Verificá que Microsoft Edge WebView2 Runtime esté instalado. " +
                        ex.GetBaseException().Message);
                }
            }

            private void CoreWebView2_NavigationCompleted(
                object sender,
                CoreWebView2NavigationCompletedEventArgs e)
            {
                if (!e.IsSuccess && !this.reportedNavigationFailure)
                {
                    this.reportedNavigationFailure = true;
                    this.BrowserFailed?.Invoke("Kick page navigation failed: " + e.WebErrorStatus + ".");
                }
            }

            private void CoreWebView2_WebMessageReceived(
                object sender,
                CoreWebView2WebMessageReceivedEventArgs e)
            {
                try
                {
                    JObject message = JObject.Parse(e.WebMessageAsJson);
                    this.MessageReceived?.Invoke(message);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Could not parse message from Kick browser: " + ex.Message);
                }
            }

            private void ShowBrowserError(string message)
            {
                this.webView.Visible = false;
                var errorLabel = new Label
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(24),
                    TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                    AutoSize = false,
                    Text = message
                };
                this.Controls.Add(errorLabel);
                errorLabel.BringToFront();
                this.BrowserFailed?.Invoke(message);
            }

            // Kick changes its React markup occasionally. The first selector is used
            // by an existing open-source chat reader; the others are fallbacks.
            // We observe only newly added message nodes, so old chat history is not
            // replayed as votes when the connection starts.
            private const string ChatObserverScript = @"
(() => {
    if (window.__gtaChaosKickDomReaderInstalled) return;
    window.__gtaChaosKickDomReaderInstalled = true;

    const send = (payload) => {
        try { window.chrome.webview.postMessage(payload); } catch (_) {}
    };
    const containerSelectors = [
        '#chatroom > div:nth-child(2) > div:first-child',
        '#chatroom [data-testid=""chatroom""]',
        '#channel-chatroom',
        '[data-testid=""chatroom""]',
        '#chatroom'
    ];
    const seenContent = new WeakSet();
    let messageSequence = 0;
    let attempts = 0;

    const findContainer = () => {
        for (const selector of containerSelectors) {
            try {
                const element = document.querySelector(selector);
                if (element) return element;
            } catch (_) {}
        }
        return null;
    };

    const processTree = (root, container) => {
        if (!root || root.nodeType !== 1) return;
        const contents = [];
        if (root.matches && root.matches('.chat-entry-content')) contents.push(root);
        if (root.querySelectorAll) {
            root.querySelectorAll('.chat-entry-content').forEach(element => contents.push(element));
        }

        for (const contentElement of contents) {
            if (seenContent.has(contentElement)) continue;

            let authorElement = null;
            let messageRoot = null;
            let parent = contentElement.parentElement;
            let depth = 0;

            while (parent && parent !== container && depth < 10) {
                const candidate =
                    parent.querySelector('.chat-message-identity .chat-entry-username') ||
                    parent.querySelector('.chat-entry-username');
                if (candidate && (candidate.innerText || candidate.textContent || '').trim()) {
                    authorElement = candidate;
                    messageRoot = parent;
                    break;
                }
                parent = parent.parentElement;
                depth++;
            }

            if (!authorElement || !messageRoot) continue;

            const username = (authorElement.innerText || authorElement.textContent || '').trim();
            const content = (contentElement.innerText || contentElement.textContent || '').trim();
            if (!username || !content) continue;

            seenContent.add(contentElement);
            const idRoot = messageRoot.closest('[data-message-id], [data-id]') || messageRoot;
            const id = idRoot.getAttribute('data-message-id') ||
                       idRoot.getAttribute('data-id') ||
                       ('kick-dom-' + Date.now() + '-' + (++messageSequence));

            send({ type: 'chat-message', id, username, content });
        }
    };

    const scanAddedNode = (node, container) => {
        if (!node || node.nodeType !== 1) return;
        processTree(node, container);

        // Some React updates insert the username and text in separate steps.
        // Scan their small common ancestors, but never the whole chat container,
        // otherwise old messages could be re-counted as new votes.
        let parent = node.parentElement;
        let depth = 0;
        while (parent && parent !== container && depth < 6) {
            processTree(parent, container);
            parent = parent.parentElement;
            depth++;
        }
    };

    const attachObserver = () => {
        const container = findContainer();
        if (!container) {
            attempts++;
            if (attempts < 90) {
                window.setTimeout(attachObserver, 1000);
            } else {
                send({
                    type: 'chat-error',
                    message: 'Kick loaded, but its chat panel was not found. The page layout may have changed or the channel chat is unavailable.'
                });
            }
            return;
        }

        const observer = new MutationObserver(mutations => {
            for (const mutation of mutations) {
                for (const node of mutation.addedNodes) scanAddedNode(node, container);
            }
        });
        observer.observe(container, { childList: true, subtree: true });
        send({ type: 'chat-ready' });
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', attachObserver, { once: true });
    } else {
        attachObserver();
    }
})();
";
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
            KickChatBrowserForm form = this.browserForm;
            bool hadSession = form != null || this.isConnected;

            this.browserForm = null;
            this.isConnected = false;
            this.subscriptionReady?.TrySetResult(false);

            if (form != null && !form.IsDisposed)
            {
                this.suppressBrowserClose = true;
                try { form.Close(); }
                catch (Exception ex) { Debug.WriteLine("Could not close Kick browser window: " + ex.Message); }
                finally { this.suppressBrowserClose = false; }
            }

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
