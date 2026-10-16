using System;
using System.Text.RegularExpressions;

namespace GTAChaos.Utils
{
    public sealed class DebugChatCommandEventArgs : EventArgs
    {
        public string Platform { get; private set; }
        public string Username { get; private set; }
        public string EffectId { get; private set; }
        public bool HasEffectId { get; private set; }

        public DebugChatCommandEventArgs(string platform, string username, string effectId, bool hasEffectId)
        {
            this.Platform = platform ?? "Unknown";
            this.Username = username ?? "Unknown";
            this.EffectId = effectId ?? string.Empty;
            this.HasEffectId = hasEffectId;
        }
    }

    /// <summary>
    /// Opt-in, read-only chat command parser for local effect testing.
    /// It never sends messages back to a platform.
    /// </summary>
    public static class DebugChatCommandHandler
    {
        private static readonly Regex CommandPattern = new Regex(
            @"^!chaos(?:\s+(?<id>[A-Za-z0-9_.:-]+))?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static event EventHandler<DebugChatCommandEventArgs> CommandReceived;

        /// <summary>
        /// Returns true when an enabled !chaos command was consumed, including malformed
        /// !chaos commands. Consumed commands must not also count as votes.
        /// </summary>
        public static bool TryHandle(string platform, string username, string message)
        {
            if (!Config.Instance().DebugChatCommandsEnabled || string.IsNullOrWhiteSpace(message))
                return false;

            string trimmed = message.Trim();
            if (!trimmed.Equals("!chaos", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("!chaos ", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("!chaos\t", StringComparison.OrdinalIgnoreCase))
                return false;

            Match match = CommandPattern.Match(trimmed);
            bool hasEffectId = match.Success && match.Groups["id"].Success;
            string effectId = hasEffectId ? match.Groups["id"].Value : string.Empty;

            CommandReceived?.Invoke(null, new DebugChatCommandEventArgs(
                platform, username, effectId, hasEffectId));
            return true;
        }
    }
}
