using GTAChaos.Effects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GTAChaos.Utils
{
    /// <summary>
    /// Owns multiple platform readers while presenting one voting connection to Form1.
    /// Every source feeds normalized plain text into the shared vote coordinator.
    /// </summary>
    public sealed class MultiStreamConnection : IStreamConnection
    {
        private sealed class SourceEntry
        {
            public int Index;
            public string Key;
            public Config.MultiStreamSourceConfig Config;
            public IStreamConnection Connection;
            public bool Connected;
            public string Error = string.Empty;
            public readonly HashSet<string> RapidFireVoters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private readonly List<SourceEntry> sources = new List<SourceEntry>();
        private readonly HashSet<string> rapidFireVoterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object stateLock = new object();
        private Shared.VOTING_MODE currentVotingMode = Shared.VOTING_MODE.COOLDOWN;
        private bool stopping;
        private bool hadConnectedSource;

        public string LastError { get; private set; } = string.Empty;
        public event EventHandler<EventArgs> OnConnected;
        public event EventHandler<EventArgs> OnDisconnected;
        public event EventHandler<EventArgs> OnLoginError;
        public event EventHandler<RapidFireEventArgs> OnRapidFireEffect;
        public event EventHandler<StreamChatMessageEventArgs> ChatMessageReceived;

        public MultiStreamConnection(IEnumerable<Config.MultiStreamSourceConfig> profiles)
        {
            int index = 0;
            foreach (Config.MultiStreamSourceConfig profile in profiles ?? Enumerable.Empty<Config.MultiStreamSourceConfig>())
            {
                int profileIndex = index++;
                if (profile == null || !profile.Enabled || (!profile.UseForChat && !profile.UseForVoting))
                    continue;

                string platform = (profile.Platform ?? "Twitch").Trim();
                string channel = (profile.Channel ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(channel))
                    continue;

                string key = profileIndex + ":" + platform.ToLowerInvariant() + ":" + channel.ToLowerInvariant();
                IStreamConnection connection;
                switch (platform.ToLowerInvariant())
                {
                    case "twitch":
                        connection = new TwitchChatConnection(profile, key);
                        break;
                    case "youtube":
                        connection = new YouTubeChatConnection(profile, key);
                        break;
                    case "kick":
                        connection = new KickChatConnection(profile, key);
                        break;
                    default:
                        continue;
                }

                var entry = new SourceEntry
                {
                    Index = profileIndex,
                    Key = key,
                    Config = profile,
                    Connection = connection
                };
                this.sources.Add(entry);

                connection.OnConnected += (sender, args) => this.HandleSourceConnected(entry);
                connection.OnDisconnected += (sender, args) => this.HandleSourceDisconnected(entry);
                connection.OnLoginError += (sender, args) => entry.Error = this.GetSourceError(connection);
                connection.OnRapidFireEffect += (sender, args) =>
                {
                    if (profile.UseForVoting)
                        this.OnRapidFireEffect?.Invoke(this, args);
                };
            }

            StreamChatMessageBus.MessageReceived += this.HandleChatMessage;
            MultiStreamVotingCoordinator.Activate();
        }

        public int SourceCount => this.sources.Count;
        public int GetRemaining() => 0;
        public bool IsConnected() => this.sources.Any(source => source.Connection.IsConnected());

        public async Task<bool> TryConnect()
        {
            if (this.sources.Count == 0)
            {
                this.LastError = "No hay fuentes MultiStream habilitadas con plataforma y canal válidos.";
                this.OnLoginError?.Invoke(this, EventArgs.Empty);
                return false;
            }

            var attempts = this.sources.Select(async source =>
            {
                try
                {
                    bool result = await source.Connection.TryConnect();
                    if (!result && string.IsNullOrWhiteSpace(source.Error))
                        source.Error = this.GetSourceError(source.Connection);
                    return result;
                }
                catch (Exception ex)
                {
                    source.Error = ex.GetBaseException().Message;
                    return false;
                }
            }).ToArray();

            bool[] results = await Task.WhenAll(attempts);
            if (!results.Any(result => result))
            {
                this.LastError = string.Join(Environment.NewLine, this.sources
                    .Where(source => !string.IsNullOrWhiteSpace(source.Error))
                    .Select(source => source.Config.Platform + " (" + source.Config.Channel + "): " + source.Error)
                    .Distinct());
                if (string.IsNullOrWhiteSpace(this.LastError))
                    this.LastError = "No se pudo conectar ninguna fuente MultiStream.";
                this.Kill();
                this.OnLoginError?.Invoke(this, EventArgs.Empty);
                return false;
            }

            this.LastError = string.Join(Environment.NewLine, this.sources
                .Where(source => !string.IsNullOrWhiteSpace(source.Error))
                .Select(source => source.Config.Platform + " (" + source.Config.Channel + "): " + source.Error)
                .Distinct());
            return true;
        }

        public void Kill()
        {
            lock (this.stateLock)
                this.stopping = true;

            StreamChatMessageBus.MessageReceived -= this.HandleChatMessage;
            MultiStreamVotingCoordinator.Deactivate();
            foreach (SourceEntry source in this.sources)
            {
                try { source.Connection.Kill(); }
                catch { }
                source.Connected = false;
            }

            bool shouldNotify;
            lock (this.stateLock)
            {
                shouldNotify = this.hadConnectedSource;
                this.hadConnectedSource = false;
                this.stopping = false;
            }
            if (shouldNotify)
                this.OnDisconnected?.Invoke(this, EventArgs.Empty);
        }

        public void SetVoting(Shared.VOTING_MODE votingMode, int untilRapidFire = -1, List<IVotingElement> votingElements = null)
        {
            this.currentVotingMode = votingMode;
            MultiStreamVotingCoordinator.SetVotingMode(votingMode);
            this.rapidFireVoterIds.Clear();

            foreach (SourceEntry source in this.sources)
            {
                if (!source.Config.UseForVoting)
                    continue;
                source.Connection.SetVoting(votingMode, untilRapidFire, votingElements);
                source.RapidFireVoters.Clear();
            }
        }

        public List<IVotingElement> GetVotedEffects()
        {
            return MultiStreamVotingCoordinator.GetVotedEffects();
        }

        public void SendEffectVotingToGame(bool undetermined = true)
        {
            MultiStreamVotingCoordinator.SendEffectVotingToGame(undetermined);
        }

        public string GetStatusSummary()
        {
            if (this.sources.Count == 0)
                return "No hay fuentes configuradas.";
            return string.Join(Environment.NewLine, this.sources.Select(source =>
                source.Config.Platform + " / " + source.Config.Channel + ": " +
                (source.Connection.IsConnected() ? "conectado" :
                 !string.IsNullOrWhiteSpace(source.Error) ? "error: " + source.Error : "conectando / desconectado")));
        }

        private void HandleChatMessage(object sender, StreamChatMessageEventArgs args)
        {
            SourceEntry source = this.sources.FirstOrDefault(item => string.Equals(item.Key, args.SourceKey, StringComparison.OrdinalIgnoreCase));
            if (source == null)
                return;

            if (source.Config.UseForChat)
                this.ChatMessageReceived?.Invoke(this, args);

            if (!source.Config.UseForVoting)
                return;

            if (this.currentVotingMode == Shared.VOTING_MODE.VOTING)
            {
                if (MultiStreamVotingCoordinator.TryProcessVote(source.Key, args.Username, args.Message))
                    this.SendEffectVotingToGame();
                return;
            }

            if (this.currentVotingMode != Shared.VOTING_MODE.RAPID_FIRE)
                return;

            string identity = source.Key + "|" + args.Username.Trim().ToLowerInvariant();
            if (source.RapidFireVoters.Contains(identity) || this.rapidFireVoterIds.Contains(identity))
                return;

            string normalized = System.Text.RegularExpressions.Regex.Replace(args.Message ?? string.Empty, @"[^A-Za-z0-9]", "");
            AbstractEffect effect = EffectDatabase.GetByWord(normalized, Config.Instance().StreamAllowOnlyEnabledEffectsRapidFire);
            if (effect == null || !effect.IsRapidFire())
                return;

            source.RapidFireVoters.Add(identity);
            this.rapidFireVoterIds.Add(identity);
            this.OnRapidFireEffect?.Invoke(this, new RapidFireEventArgs
            {
                Effect = effect.SetSubtext(args.Username)
            });
        }

        private void HandleSourceConnected(SourceEntry source)
        {
            source.Connected = true;
            bool notify = false;
            lock (this.stateLock)
            {
                if (!this.stopping && !this.hadConnectedSource)
                {
                    this.hadConnectedSource = true;
                    notify = true;
                }
            }
            if (notify)
                this.OnConnected?.Invoke(this, EventArgs.Empty);
        }

        private void HandleSourceDisconnected(SourceEntry source)
        {
            source.Connected = false;
            bool notify = false;
            lock (this.stateLock)
            {
                if (!this.stopping && this.hadConnectedSource &&
                    !this.sources.Any(item => !ReferenceEquals(item, source) && item.Connected))
                {
                    this.hadConnectedSource = false;
                    notify = true;
                }
            }
            if (notify)
                this.OnDisconnected?.Invoke(this, EventArgs.Empty);
        }

        private static string GetSourceError(IStreamConnection connection)
        {
            KickChatConnection kick = connection as KickChatConnection;
            return kick != null && !string.IsNullOrWhiteSpace(kick.LastError)
                ? kick.LastError
                : "No se pudo conectar a esta fuente.";
        }
    }
}
