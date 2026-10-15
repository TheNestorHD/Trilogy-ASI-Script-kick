// Copyright (c) 2019 Lordmau5
using GTAChaos.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace GTAChaos.Effects
{
    /// <summary>
    /// Loads per-effect display-name and duration overrides from effect_durations.json.
    /// The name is the custom display name used by the game and stream overlay. A null
    /// duration keeps the effect's normal duration; numeric values are expressed in seconds.
    /// </summary>
    public sealed class EffectDurationSetting
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("duration_seconds")]
        public int? DurationSeconds { get; set; }
    }

    public static class EffectDurationOverrides
    {
        private static readonly object Sync = new();
        private static readonly Dictionary<string, EffectDurationSetting> Settings =
            new(StringComparer.OrdinalIgnoreCase);
        private static bool initialized;

        private static string SettingsPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "effect_durations.json");

        public static void Initialize()
        {
            lock (Sync)
            {
                if (initialized)
                    return;

                string path = SettingsPath;
                bool needsSave = !File.Exists(path);

                if (!needsSave)
                {
                    try
                    {
                        Dictionary<string, EffectDurationSetting> loaded =
                            JsonConvert.DeserializeObject<Dictionary<string, EffectDurationSetting>>(
                                File.ReadAllText(path));

                        if (loaded != null)
                        {
                            foreach (KeyValuePair<string, EffectDurationSetting> entry in loaded)
                            {
                                if (!string.IsNullOrWhiteSpace(entry.Key))
                                    Settings[entry.Key] = entry.Value ?? new EffectDurationSetting();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Never overwrite a user's file when it contains invalid JSON.
                        Debug.WriteLine("[EffectDurations] Could not read " + path + ": " + ex.Message);
                        initialized = true;
                        return;
                    }
                }

                foreach (WeightedRandomBag<AbstractEffect>.Entry entry in EffectDatabase.Effects.Get())
                {
                    AbstractEffect effect = entry.item;
                    if (effect == null || string.IsNullOrWhiteSpace(effect.GetID()))
                        continue;

                    string id = effect.GetID();
                    string name = effect.GetDisplayName(DisplayNameType.UI);

                    if (!Settings.TryGetValue(id, out EffectDurationSetting setting))
                    {
                        setting = new EffectDurationSetting();
                        Settings[id] = setting;
                        needsSave = true;
                    }

                    // Populate missing names for new effects, but never overwrite a user's
                    // custom name when the GUI starts.
                    if (string.IsNullOrWhiteSpace(setting.Name))
                    {
                        setting.Name = name;
                        needsSave = true;
                    }

                    if (setting.DurationSeconds.HasValue && setting.DurationSeconds.Value < 1)
                    {
                        setting.DurationSeconds = null;
                        needsSave = true;
                    }
                }

                if (needsSave)
                {
                    try
                    {
                        File.WriteAllText(path,
                            JsonConvert.SerializeObject(Settings, Formatting.Indented) +
                            Environment.NewLine);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[EffectDurations] Could not write " + path + ": " + ex.Message);
                    }
                }

                initialized = true;
            }
        }

        public static string GetCustomDisplayName(string effectId, string fallbackName)
        {
            Initialize();

            lock (Sync)
            {
                if (Settings.TryGetValue(effectId ?? string.Empty, out EffectDurationSetting setting) &&
                    !string.IsNullOrWhiteSpace(setting.Name))
                {
                    return setting.Name.Trim();
                }
            }

            return fallbackName;
        }

        public static int? GetDurationMilliseconds(string effectId)
        {
            Initialize();

            lock (Sync)
            {
                if (Settings.TryGetValue(effectId ?? string.Empty, out EffectDurationSetting setting) &&
                    setting.DurationSeconds.HasValue &&
                    setting.DurationSeconds.Value >= 1)
                {
                    long milliseconds = (long)setting.DurationSeconds.Value * 1000L;
                    return (int)Math.Min(milliseconds, int.MaxValue);
                }
            }

            return null;
        }
    }

    public enum DisplayNameType
    {
        GAME,
        UI,
        STREAM
    }

    public abstract class AbstractEffect
    {
        public readonly Category Category;
        private readonly Dictionary<DisplayNameType, string> DisplayNames = new();
        public readonly string Word;
        public readonly int Duration;
        private float Multiplier;
        private string Subtext = "";
        private int rapidFire = 1;
        private bool streamEnabled = true;

        public AbstractEffect(Category category, string displayName, string word, int duration = -1, float multiplier = 3.0f)
        {
            this.Category = category;
            this.SetDisplayNames(displayName);
            this.Word = word;
            this.Duration = duration;
            this.Multiplier = multiplier;

            category.AddEffectToCategory(this);
        }

        public bool IsID(string id) => this.GetID().Equals(id) || this.GetID().Equals($"effect_{id}");

        public abstract string GetID();

        protected void SetDisplayNames(string displayName)
        {
            foreach (object nameType in Enum.GetValues(typeof(DisplayNameType)))
            {
                this.SetDisplayName((DisplayNameType)nameType, displayName);
            }
        }

        public AbstractEffect SetDisplayName(DisplayNameType type, string displayName)
        {
            this.DisplayNames[type] = displayName;
            return this;
        }

        public virtual string GetDisplayName(DisplayNameType type = DisplayNameType.GAME)
        {
            return EffectDurationOverrides.GetCustomDisplayName(
                this.GetID(),
                this.GetOriginalDisplayName(type));
        }

        /// <summary>
        /// Returns the built-in name without applying effect_durations.json overrides.
        /// Used while initializing the JSON defaults to avoid a recursive name lookup.
        /// </summary>
        public string GetOriginalDisplayName(DisplayNameType type = DisplayNameType.GAME) =>
            this.DisplayNames.ContainsKey(type) ? this.DisplayNames[type] : this.DisplayNames[DisplayNameType.GAME];

        public string GetSubtext() => this.Subtext;

        public AbstractEffect SetSubtext(string subtext)
        {
            this.Subtext = subtext;
            return this;
        }

        public AbstractEffect ResetSubtext()
        {
            this.Subtext = "";
            return this;
        }

        public AbstractEffect DisableRapidFire()
        {
            this.rapidFire = 0;
            return this;
        }

        public AbstractEffect DisableStream()
        {
            this.streamEnabled = false;
            return this;
        }

        public AbstractEffect SetMultiplier(float multiplier)
        {
            this.Multiplier = multiplier;
            return this;
        }

        public float GetMultiplier() => this.Multiplier;

        public bool IsRapidFire() => this.rapidFire == 1;

        public bool IsTwitchEnabled() => this.streamEnabled;

        public virtual Task RunEffect(int seed = -1, int _duration = -1) => Task.CompletedTask;

        public virtual bool IsCooldownable() => true;

        public int GetDuration(int duration = -1)
        {
            if (duration == -1)
            {
                int? customDuration = EffectDurationOverrides.GetDurationMilliseconds(this.GetID());

                if (customDuration.HasValue)
                {
                    // A JSON override is the final desired duration; do not apply the
                    // effect's default multiplier to a value the user explicitly chose.
                    duration = customDuration.Value;
                }
                else if (this.Duration != -1)
                {
                    duration = this.Duration;
                }
                else
                {
                    duration = Config.GetEffectDuration();
                    duration = (int)Math.Round(duration * this.Multiplier);
                }
            }

            // Cap the final duration, after JSON overrides, multipliers and fixed durations.
            if (Shared.IsStreamMode && duration > 0)
            {
                duration = Math.Min(duration, 1000 * 30);
            }

            return duration;
        }

        public bool GetRapidFire(bool overrideCheck = false)
        {
            if (overrideCheck)
            {
                return this.rapidFire == 1;
            }

            bool rapidFire = false;
            if (Shared.IsStreamMode)
            {
                if (Shared.StreamVotingMode == Shared.VOTING_MODE.RAPID_FIRE)
                {
                    rapidFire = this.rapidFire == 1;
                }
            }

            return rapidFire;
        }

        public virtual string GetSoundID() => "";
    }
}
