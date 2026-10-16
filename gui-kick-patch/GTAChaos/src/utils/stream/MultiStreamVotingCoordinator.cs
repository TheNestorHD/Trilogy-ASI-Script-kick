using GTAChaos.Effects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GTAChaos.Utils
{
    /// <summary>
    /// Shared plain-text chat event used by MultiStream, voting and the future in-game renderer.
    /// Rich platform media is deliberately not represented.
    /// </summary>
    public sealed class StreamChatMessageEventArgs : EventArgs
    {
        public string SourceKey { get; private set; }
        public string Platform { get; private set; }
        public string Channel { get; private set; }
        public string Username { get; private set; }
        public string Message { get; private set; }

        public StreamChatMessageEventArgs(string sourceKey, string platform, string channel, string username, string message)
        {
            this.SourceKey = sourceKey ?? string.Empty;
            this.Platform = platform ?? "Unknown";
            this.Channel = channel ?? string.Empty;
            this.Username = username ?? string.Empty;
            this.Message = ChatTextSanitizer.NormalizeWhitespace(message);
        }
    }

    public static class StreamChatMessageBus
    {
        public static event EventHandler<StreamChatMessageEventArgs> MessageReceived;

        public static void Publish(string sourceKey, string platform, string channel, string username, string message)
        {
            string plainText = ChatTextSanitizer.NormalizeWhitespace(message);
            if (string.IsNullOrWhiteSpace(plainText) || string.IsNullOrWhiteSpace(username))
                return;

            MessageReceived?.Invoke(null, new StreamChatMessageEventArgs(
                sourceKey, platform, channel, username, plainText));
        }
    }

    /// <summary>
    /// One voting round shared by all source adapters. Each account is keyed by source + username,
    /// and a source can update its own vote without affecting other platforms.
    /// </summary>
    public static class MultiStreamVotingCoordinator
    {
        private static readonly object Sync = new object();
        private static readonly List<MultiStreamVotingElement> Options = new List<MultiStreamVotingElement>();
        private static readonly Dictionary<string, MultiStreamVotingElement> VoterChoices =
            new Dictionary<string, MultiStreamVotingElement>(StringComparer.OrdinalIgnoreCase);
        private static bool active;
        private static bool roundActive;
        private static VoteChoice lastChoice = VoteChoice.UNDETERMINED;

        public static bool IsActive
        {
            get { lock (Sync) return active; }
        }

        public static bool IsRoundActive
        {
            get { lock (Sync) return roundActive; }
        }

        public static void Activate()
        {
            lock (Sync)
            {
                active = true;
            }
        }

        public static void Deactivate()
        {
            lock (Sync)
            {
                active = false;
                roundActive = false;
                Options.Clear();
                VoterChoices.Clear();
                lastChoice = VoteChoice.UNDETERMINED;
            }
        }

        public static void SetVotingMode(Shared.VOTING_MODE mode)
        {
            lock (Sync)
            {
                if (!active)
                    return;

                if (mode == Shared.VOTING_MODE.VOTING)
                {
                    if (!roundActive)
                    {
                        GenerateOptions();
                        VoterChoices.Clear();
                        lastChoice = VoteChoice.UNDETERMINED;
                        roundActive = true;
                    }
                }
                else
                {
                    roundActive = false;
                }
            }
        }

        public static List<IVotingElement> GetOptions()
        {
            lock (Sync)
                return Options.Cast<IVotingElement>().ToList();
        }

        public static bool TryProcessVote(string sourceKey, string username, string message)
        {
            int choice = ParseVote(message);
            if (choice < 0 || choice > 2 || string.IsNullOrWhiteSpace(username))
                return false;

            lock (Sync)
            {
                if (!active || !roundActive || Options.Count != 3)
                    return false;

                string identity = (sourceKey ?? "unknown") + "|" + username.Trim().ToLowerInvariant();
                MultiStreamVotingElement previous;
                if (VoterChoices.TryGetValue(identity, out previous))
                    previous.RemoveVoter(identity);

                MultiStreamVotingElement selected = Options[choice];
                selected.AddVoter(identity);
                VoterChoices[identity] = selected;

                int total = Options.Sum(option => option.GetVotes());
                foreach (MultiStreamVotingElement option in Options)
                    option.Percentage = total == 0 ? 0 : (int)Math.Round((double)option.GetVotes() / total * 100);

                return true;
            }
        }

        public static List<IVotingElement> GetVotedEffects()
        {
            lock (Sync)
            {
                if (Options.Count == 0)
                    GenerateOptions();

                if (Options.Count == 0)
                    return new List<IVotingElement>();

                int totalVotes = Options.Sum(option => option.GetVotes());
                List<MultiStreamVotingElement> winners;

                if (totalVotes == 0)
                {
                    // The form's no-vote path chooses one of the returned candidates at random.
                    winners = Options.ToList();
                }
                else if (Config.Instance().StreamMajorityVotes)
                {
                    int maxVotes = Options.Max(option => option.GetVotes());
                    winners = Options.Where(option => option.GetVotes() == maxVotes).ToList();
                    if (!Config.Instance().StreamEnableMultipleEffects && winners.Count > 1)
                        winners = new List<MultiStreamVotingElement> { winners[new Random().Next(winners.Count)] };
                }
                else
                {
                    int pick = new Random().Next(totalVotes);
                    MultiStreamVotingElement winner = Options[Options.Count - 1];
                    foreach (MultiStreamVotingElement option in Options)
                    {
                        if (pick < option.GetVotes())
                        {
                            winner = option;
                            break;
                        }
                        pick -= option.GetVotes();
                    }
                    winners = new List<MultiStreamVotingElement> { winner };
                }

                lastChoice = VoteChoice.NONE;
                foreach (MultiStreamVotingElement winner in winners)
                {
                    winner.GetEffect().SetSubtext(totalVotes == 0 ? "0%" : winner.GetPercentage() + "%");
                    lastChoice |= ChoiceForIndex(winner.GetId());
                }

                return winners.Cast<IVotingElement>().ToList();
            }
        }

        public static void SendEffectVotingToGame(bool undetermined = true)
        {
            string[] realEffects;
            int[] votes;
            VoteChoice selected;
            lock (Sync)
            {
                if (Options.Count == 0)
                    return;

                realEffects = Options.Select(option => option.GetEffect().GetDisplayName()).ToArray();
                votes = Options.Select(option => option.GetVotes()).ToArray();
                selected = undetermined ? VoteChoice.UNDETERMINED : lastChoice;
            }

            WebsocketHandler.INSTANCE.SendOverlayVotes(realEffects, votes, selected);

            bool concealForObs = WebsocketHandler.INSTANCE.IsObsVotingOverlayActive;
            bool concealForSetting = undetermined && Config.Instance().StreamHideVotingEffectsIngame;
            string[] gameEffects = (concealForObs || concealForSetting)
                ? realEffects.Select(_ => "???").ToArray()
                : realEffects;

            if (Shared.Sync != null)
                Shared.Sync.SendVotes(gameEffects, votes, selected, !undetermined);
            else
                WebsocketHandler.INSTANCE.SendVotes(gameEffects, votes, selected);
        }

        private static int ParseVote(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return -1;

            // Match the legacy platform readers: punctuation and spacing around
            // a single choice are ignored, but text such as "vote 1" is not a vote.
            string normalized = Regex.Replace(message.Trim(), @"[^A-Za-z0-9]", string.Empty);

            return normalized.Length == 1 && normalized[0] >= '1' && normalized[0] <= '3'
                ? normalized[0] - '1'
                : -1;
        }

        private static VoteChoice ChoiceForIndex(int index)
        {
            switch (index)
            {
                case 0: return VoteChoice.FIRST;
                case 1: return VoteChoice.SECOND;
                case 2: return VoteChoice.THIRD;
                default: return VoteChoice.NONE;
            }
        }

        private static void GenerateOptions()
        {
            Options.Clear();
            int attempts = 0;
            int desired = Math.Min(3, Math.Max(1, EffectDatabase.EnabledEffects.Count));

            while (Options.Count < desired && attempts < 200)
            {
                AbstractEffect effect = EffectDatabase.GetRandomEffect(true, 0, true);
                if (effect != null && effect.IsTwitchEnabled() &&
                    !Options.Any(option => option.GetEffect().GetID() == effect.GetID()))
                {
                    Options.Add(new MultiStreamVotingElement(Options.Count, effect));
                }

                attempts++;
                if (attempts == 40 || attempts == 80 || attempts == 120)
                    EffectDatabase.ResetEffectCooldowns();
            }

            attempts = 0;
            while (Options.Count < 3 && attempts < 300)
            {
                AbstractEffect effect = EffectDatabase.GetRandomEffect(false, 0, true);
                if (effect != null && effect.IsTwitchEnabled() &&
                    !Options.Any(option => option.GetEffect().GetID() == effect.GetID()))
                {
                    Options.Add(new MultiStreamVotingElement(Options.Count, effect));
                }
                attempts++;
            }

            // Keep the protocol at three options even if a restricted effect list is exhausted.
            while (Options.Count > 0 && Options.Count < 3)
            {
                AbstractEffect effect = Options[Options.Count % Options.Count].GetEffect();
                Options.Add(new MultiStreamVotingElement(Options.Count, effect));
            }
        }

        private sealed class MultiStreamVotingElement : IVotingElement
        {
            private readonly HashSet<string> voters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public int Id { get; private set; }
            private readonly AbstractEffect effect;
            public int Percentage { get; set; }

            public MultiStreamVotingElement(int id, AbstractEffect effect)
            {
                this.Id = id;
                this.effect = effect;
            }

            public int GetId() => this.Id;
            public AbstractEffect GetEffect() => this.effect;
            public int GetVotes() => this.voters.Count;
            public int GetPercentage() => this.Percentage;
            public void AddVoter(string identity) => this.voters.Add(identity);
            public void RemoveVoter(string identity) => this.voters.Remove(identity);
        }
    }
}
