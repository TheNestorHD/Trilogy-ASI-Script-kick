using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
    /// Produces plain text for game overlays. TwitchLib exposes exact emote spans, so
    /// their text aliases can be removed without downloading/rendering any image assets.
    /// </summary>
    public static class ChatTextSanitizer
    {
        public static string RemoveTwitchEmoteText(string message, object emoteSet)
        {
            if (string.IsNullOrEmpty(message) || emoteSet == null)
                return NormalizeWhitespace(message);

            try
            {
                PropertyInfo emotesProperty = emoteSet.GetType().GetProperty("Emotes");
                IEnumerable emotes = emotesProperty == null ? null : emotesProperty.GetValue(emoteSet, null) as IEnumerable;
                if (emotes != null)
                {
                    var spans = new List<Tuple<int, int>>();
                    foreach (object emote in emotes)
                    {
                        if (emote == null) continue;
                        PropertyInfo startProperty = emote.GetType().GetProperty("StartIndex");
                        PropertyInfo endProperty = emote.GetType().GetProperty("EndIndex");
                        PropertyInfo nameProperty = emote.GetType().GetProperty("Name");
                        if (startProperty == null || endProperty == null) continue;

                        int start = Convert.ToInt32(startProperty.GetValue(emote, null));
                        int end = Convert.ToInt32(endProperty.GetValue(emote, null));
                        if (start < 0 || end < start || end >= message.Length) continue;

                        // Guard against a codepoint/UTF-16 offset mismatch when a
                        // message contains non-BMP Unicode before the emote.
                        string emoteName = nameProperty == null ? null : Convert.ToString(nameProperty.GetValue(emote, null));
                        string textAtSpan = message.Substring(start, end - start + 1);
                        if (string.IsNullOrEmpty(emoteName) || string.Equals(textAtSpan, emoteName, StringComparison.Ordinal))
                            spans.Add(Tuple.Create(start, end));
                    }

                    // Remove from right to left so earlier indices remain stable.
                    spans.Sort((left, right) => right.Item1.CompareTo(left.Item1));
                    foreach (Tuple<int, int> span in spans)
                        message = message.Remove(span.Item1, span.Item2 - span.Item1 + 1);
                }
            }
            catch
            {
                // A malformed emote span should not discard the entire chat message.
            }

            return NormalizeWhitespace(message);
        }

        public static string NormalizeWhitespace(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return string.Empty;

            return Regex.Replace(message.Replace('\r', ' ').Replace('\n', ' '), @"\s+", " ").Trim();
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
            if (!trimmed.StartsWith("!chaos", StringComparison.OrdinalIgnoreCase) ||
                (trimmed.Length > 6 && !char.IsWhiteSpace(trimmed[6])))
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
