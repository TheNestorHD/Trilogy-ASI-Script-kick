// Copyright (c) 2019 Lordmau5
using GTAChaos.Effects;
using GTAChaos.Utils;
using GTAChaos.Utils.Stream;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTAChaos.Forms
{
    public partial class Form1 : Form
    {
        private readonly string configPath = Path.Combine(Directory.GetCurrentDirectory(), "config.cfg");

        private readonly Stopwatch stopwatch;
        private readonly Dictionary<string, EffectTreeNode> idToEffectNodeMap = new();
        private IStreamConnection stream;

        private TabPage tabMultiStreamSettings;
        private TabPage tabDebugSettings;
        private CheckBox checkBoxMultiStreamEnabled;
        private CheckBox checkBoxDebugChatCommands;
        private Label labelMultiStreamStatus;
        private Label labelDebugChatStatus;
        private readonly List<MultiStreamSourceControls> multiStreamSourceControls = new List<MultiStreamSourceControls>();

        private sealed class MultiStreamSourceControls
        {
            public CheckBox Enabled;
            public ComboBox Platform;
            public TextBox Channel;
            public TextBox ClientID;
            public TextBox AccessToken;
            public CheckBox UseForChat;
            public CheckBox UseForVoting;
        }

        private int elapsedCount;
        private int timesUntilRapidFire;
        private bool experimentalModeEnabled = true;

#if DEBUG
        private readonly bool debug = true;
#else

        private readonly bool debug = false;
#endif

        public Form1()
        {
            this.InitializeComponent();

            if (!this.debug)
            {
                this.gameToolStripMenuItem.Visible = false;
                this.tabs.TabPages.Remove(this.tabExperimental);
                this.experimentalModeEnabled = false;
            }

            this.SetPollsTabVisible(false);

            this.stopwatch = new Stopwatch();

            EffectDatabase.PopulateEffects("san_andreas");
            EffectDurationOverrides.Initialize();
            this.PopulateEffectTreeList();

            this.PopulateMainCooldowns();
            this.PopulatePresets();

            this.tabs.TabPages.Remove(this.tabStream);

            this.PopulateVotingTimes();
            this.PopulateVotingCooldowns();

            this.PopulateStreamModes();

            this.TryLoadConfig();

            this.InitializeMultiStreamAndDebugTabs();

            this.UpdateProgramText();

            this.timesUntilRapidFire = new Random().Next(10, 15);

            this.numericUpDownEffectCooldown.Maximum = EffectDatabase.Effects.Count;

            WebsocketHandler.INSTANCE.CreateWebsocketServer();
            WebsocketHandler.INSTANCE.OnSocketMessage += this.OnSocketMessage;
        }

        private void Form1_Shown(object sender, EventArgs e)
        {
            if (Config.Instance().CheckForUpdatesAtLaunch)
            {
                UpdateChecker.CheckForUpdate();
            }
        }

        private void UpdateProgramText() => this.Text = $"GTA Trilogy Chaos Mod v{Shared.GetVersionString(this.debug)} (Port: {Config.Instance().WebsocketPort})";

        private void OnSocketMessage(object sender, SocketMessageEventArgs e)
        {
            try
            {
                JObject json = JObject.Parse(e.Data);

                string type = Convert.ToString(json["type"]);
                string state = Convert.ToString(json["state"]);

                if (type == "ChaosMod" && state == "auto_start")
                {
                    if (Shared.Sync == null || Shared.Sync.IsHost)
                    {
                        this.Invoke(new Action(() => this.DoAutostart()));
                    }
                }
            }
            catch (Exception) { }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            this.SaveConfig();
            DebugChatCommandHandler.CommandReceived -= this.OnDebugChatCommandReceived;
        }

        private void TryLoadConfig()
        {
            try
            {
                JsonSerializer serializer = new();

                using StreamReader streamReader = new(this.configPath);
                using JsonReader reader = new JsonTextReader(streamReader);

                Config.SetInstance(serializer.Deserialize<Config>(reader));
                RandomHandler.SetSeed(Config.Instance().Seed);
            }
            catch (Exception) { }

            this.LoadPreset(Config.Instance().EnabledEffects);
            this.UpdateInterface();
        }

        private void SaveConfig()
        {
            try
            {
                Config.Instance().EnabledEffects.Clear();

                foreach (WeightedRandomBag<AbstractEffect>.Entry entry in EffectDatabase.Effects.Get())
                {
                    bool enabled = EffectDatabase.EnabledEffects.Contains(entry);
                    Config.Instance().EnabledEffects.Add(entry.item.GetID(), enabled);
                }

                JsonSerializer serializer = new();

                using StreamWriter sw = new(this.configPath);
                using JsonTextWriter writer = new(sw);

                serializer.Serialize(writer, Config.Instance());
            }
            catch (Exception) { }
        }

        private void UpdateInterface()
        {
            bool found = false;
            foreach (MainCooldownComboBoxItem item in this.comboBoxMainCooldown.Items)
            {
                if (item.Time == Config.Instance().MainCooldown)
                {
                    this.comboBoxMainCooldown.SelectedItem = item;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                this.comboBoxMainCooldown.SelectedIndex = 3;
                Config.Instance().MainCooldown = 1000 * 60;
            }

            this.checkBoxAutoStart.Checked = Config.Instance().AutoStart;

            this.checkBoxStreamAllowOnlyEnabledEffects.Checked = Config.Instance().StreamAllowOnlyEnabledEffectsRapidFire;

            found = false;
            foreach (VotingTimeComboBoxItem item in this.comboBoxVotingTime.Items)
            {
                if (item.VotingTime == Config.Instance().StreamVotingTime)
                {
                    this.comboBoxVotingTime.SelectedItem = item;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                this.comboBoxVotingTime.SelectedIndex = 0;
                Config.Instance().StreamVotingTime = 1000 * 30;
            }

            found = false;
            foreach (VotingCooldownComboBoxItem item in this.comboBoxVotingCooldown.Items)
            {
                if (item.VotingCooldown == Config.Instance().StreamVotingCooldown)
                {
                    this.comboBoxVotingCooldown.SelectedItem = item;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                this.comboBoxVotingCooldown.SelectedIndex = 1;
                Config.Instance().StreamVotingCooldown = 1000 * 60;
            }

            this.checkBoxSettingsCheckForUpdatesAtLaunch.Checked = Config.Instance().CheckForUpdatesAtLaunch;

            this.textBoxStreamAccessToken.Text = Config.Instance().StreamAccessToken;
            this.textBoxStreamClientID.Text = Config.Instance().StreamClientID;

            this.checkBoxShowLastEffectsMain.Checked = Config.Instance().MainShowLastEffects;
            this.checkBoxShowLastEffectsStream.Checked = Config.Instance().StreamShowLastEffects;
            this.checkBoxStream3TimesCooldown.Checked = Config.Instance().Stream3TimesCooldown;
            this.checkBoxStreamCombineVotingMessages.Checked = Config.Instance().StreamCombineChatMessages;
            this.checkBoxStreamEnableMultipleEffects.Checked = Config.Instance().StreamEnableMultipleEffects;
            this.checkBoxStreamEnableRapidFire.Checked = Config.Instance().StreamEnableRapidFire;
            this.checkBoxStreamHideVotingEffectsIngame.Checked = Config.Instance().StreamHideVotingEffectsIngame;

            this.checkBoxStreamMajorityVotes.Checked = Config.Instance().StreamMajorityVotes;
            this.checkBoxStreamEnableMultipleEffects.Enabled = Config.Instance().StreamMajorityVotes;

            this.checkBoxTwitchUsePolls.Checked = Config.Instance().TwitchUsePolls;
            this.checkBoxTwitchPollsPostMessages.Checked = Config.Instance().TwitchPollsPostMessages;
            this.numericUpDownTwitchPollsBitsCost.Value = Config.Instance().TwitchPollsBitsCost;
            this.numericUpDownTwitchPollsChannelPointsCost.Value = Config.Instance().TwitchPollsChannelPointsCost;

            this.textBoxSyncServer.Text = Config.Instance().SyncServer;
            this.textBoxSyncChannel.Text = Config.Instance().SyncChannel;
            this.textBoxSyncUsername.Text = Config.Instance().SyncUsername;

            this.checkBoxExperimental_RunEffectOnAutoStart.Checked = Config.Instance().Experimental_RunEffectOnAutoStart;
            this.textBoxExperimentalEffectName.Text = Config.Instance().Experimental_EffectName;
            this.numericUpDownEffectCooldown.Value = Math.Min(Config.Instance().EffectsCooldownNotActivating, this.numericUpDownEffectCooldown.Maximum);

            this.numericWebsocketPort.Value = Config.Instance().WebsocketPort;

            this.textBoxSeed.Text = Config.Instance().Seed;

            foreach (StreamModeComboBoxItem item in this.comboBoxSettingsStreamMode.Items)
            {
                if (item.StreamMode == Config.Instance().StreamMode)
                {
                    this.comboBoxSettingsStreamMode.SelectedItem = item;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                this.comboBoxSettingsStreamMode.SelectedIndex = 1;
                Config.Instance().StreamMode = StreamHandler.STREAM_MODE.TWITCH;
            }

            this.UpdateStreamTab();

            /*
             * Update selections
             */
        }

        public void AddEffectToListBox(AbstractEffect effect)
        {
            string description = "Invalid";
            if (effect != null)
            {
                description = effect.GetDisplayName(DisplayNameType.UI);
                if (!string.IsNullOrEmpty(effect.Word))
                {
                    description += $" ({effect.Word})";
                }
            }

            ListBox listBox = Shared.IsStreamMode ? this.listLastEffectsStream : this.listLastEffectsMain;
            listBox.Items.Insert(0, description);
            int maxHeight = (int)Math.Floor((float)listBox.Height / listBox.ItemHeight) - 1;
            if (listBox.Items.Count > maxHeight)
            {
                listBox.Items.RemoveAt(maxHeight);
            }
        }

        private void CallEffect(AbstractEffect effect = null)
        {
            if (effect == null)
            {
                effect = EffectDatabase.GetRandomEffect(true);
                if (effect == null)
                {
                    effect = EffectDatabase.GetRandomEffect(false);
                }

                if (effect == null)
                {
                    return;
                }

                if (Shared.Sync != null && effect is not RapidFireEffect)
                {
                    Shared.Sync.SendEffect(effect);
                }
                else
                {
                    effect = EffectDatabase.RunEffect(effect);
                    effect?.ResetSubtext();

                    if (effect != null)
                    {
                        this.AddEffectToListBox(effect);
                    }
                }
            }
            else
            {
                if (Shared.Sync != null && effect is not RapidFireEffect)
                {
                    Shared.Sync.SendEffect(effect);
                }
                else
                {
                    EffectDatabase.RunEffect(effect);
                    this.AddEffectToListBox(effect);
                }
            }
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            // Developer testing mode freezes the automatic counter in both normal
            // and stream modes. Chat commands can still trigger individual effects.
            if (Config.Instance().DebugChatCommandsEnabled)
            {
                this.stopwatch.Stop();
                return;
            }

            if (Shared.IsStreamMode)
            {
                this.TickStream();
            }
            else
            {
                this.TickMain();
            }
        }

        private void TickMain()
        {
            if (!Shared.TimerEnabled)
            {
                return;
            }

            int value = Math.Max(1, (int)this.stopwatch.ElapsedMilliseconds);

            // Hack to fix Windows' broken-ass progress bar handling
            this.progressBarMain.Value = Math.Min(value, this.progressBarMain.Maximum);
            this.progressBarMain.Value = Math.Min(value - 1, this.progressBarMain.Maximum);

            int remaining = (int)Math.Max(0, Config.Instance().MainCooldown - this.stopwatch.ElapsedMilliseconds);

            WebsocketHandler.INSTANCE.SendTimeToGame(remaining, Config.Instance().MainCooldown);

            if (this.stopwatch.ElapsedMilliseconds - this.elapsedCount > 100)
            {
                Shared.Sync?.SendTimeUpdate(remaining, Config.Instance().MainCooldown);

                this.elapsedCount = (int)this.stopwatch.ElapsedMilliseconds;
            }

            if (this.stopwatch.ElapsedMilliseconds >= Config.Instance().MainCooldown)
            {
                this.progressBarMain.Value = 0;
                this.CallEffect();
                this.elapsedCount = 0;
                this.stopwatch.Restart();
            }
        }

        private void TickStream()
        {
            if (!Shared.TimerEnabled)
            {
                return;
            }

            if (Shared.StreamVotingMode == Shared.VOTING_MODE.VOTING)
            {
                if (this.progressBarStream.Maximum != Config.Instance().StreamVotingTime)
                {
                    this.progressBarStream.Maximum = Config.Instance().StreamVotingTime;
                }

                // Hack to fix Windows' broken-ass progress bar handling
                int value = Math.Max(1, (int)this.stopwatch.ElapsedMilliseconds);
                this.progressBarStream.Value = Math.Max(this.progressBarStream.Maximum - value, 0);
                this.progressBarStream.Value = Math.Max(this.progressBarStream.Maximum - value - 1, 0);

                int remaining = (int)Math.Max(0, Config.Instance().StreamVotingTime - this.stopwatch.ElapsedMilliseconds);

                WebsocketHandler.INSTANCE.SendTimeToGame(remaining, Config.Instance().StreamVotingTime, "Voting");

                if (this.stopwatch.ElapsedMilliseconds - this.elapsedCount > 100)
                {
                    Shared.Sync?.SendTimeUpdate(remaining, Config.Instance().StreamVotingTime);

                    this.stream?.SendEffectVotingToGame(Config.Instance().StreamHideVotingEffectsIngame);

                    this.elapsedCount = (int)this.stopwatch.ElapsedMilliseconds;
                }

                bool didFinish;

                if (Config.Instance().TwitchUsePolls && this.stream != null && StreamHandler.IsTwitchMode())
                {
                    didFinish = this.stream.GetRemaining() == 0;

                    if (this.stopwatch.ElapsedMilliseconds >= Config.Instance().StreamVotingTime)
                    {
                        long millisecondsOver = this.stopwatch.ElapsedMilliseconds - Config.Instance().StreamVotingTime;
                        int waitLeft = Math.Max(0, 15000 - decimal.ToInt32(millisecondsOver));
                        this.labelStreamCurrentMode.Text = $"Current Mode: Waiting For Poll... ({Math.Ceiling((float)waitLeft / 1000)}s left)";

                        if (waitLeft == 0)
                        {
                            this.labelStreamCurrentMode.Text = "Current Mode: Cooldown (Poll Failed)";

                            WebsocketHandler.INSTANCE.SendTimeToGame(0);
                            Shared.Sync?.SendTimeUpdate(0, Config.Instance().StreamVotingCooldown);
                            this.elapsedCount = 0;

                            this.progressBarStream.Value = 0;
                            this.progressBarStream.Maximum = Config.Instance().StreamVotingCooldown;

                            this.stopwatch.Restart();
                            Shared.StreamVotingMode = Shared.VOTING_MODE.COOLDOWN;

                            this.stream?.SetVoting(Shared.VOTING_MODE.ERROR, this.timesUntilRapidFire, null);

                            return;
                        }
                    }
                }
                else
                {
                    didFinish = this.stopwatch.ElapsedMilliseconds >= Config.Instance().StreamVotingTime;
                }

                if (didFinish)
                {
                    WebsocketHandler.INSTANCE.SendTimeToGame(0);
                    Shared.Sync?.SendTimeUpdate(0, Config.Instance().StreamVotingCooldown);
                    this.elapsedCount = 0;

                    this.progressBarStream.Value = 0;
                    this.progressBarStream.Maximum = Config.Instance().StreamVotingCooldown;

                    this.stopwatch.Restart();
                    Shared.StreamVotingMode = Shared.VOTING_MODE.COOLDOWN;

                    this.labelStreamCurrentMode.Text = "Current Mode: Cooldown";

                    if (this.stream != null)
                    {
                        List<IVotingElement> elements = this.stream.GetVotedEffects();

                        bool zeroVotes = true;
                        foreach (IVotingElement e in elements)
                        {
                            if (e.GetVotes() > 0)
                            {
                                zeroVotes = false;
                            }
                        }

                        if (!zeroVotes)
                        {
                            foreach (IVotingElement e in elements)
                            {
                                float multiplier = e.GetEffect().GetMultiplier();
                                e.GetEffect().SetMultiplier(multiplier / elements.Count);
                                this.CallEffect(e.GetEffect());
                                e.GetEffect().SetMultiplier(multiplier);
                            }
                        }
                        else
                        {
                            int randomEffectID = RandomHandler.Next(elements.Count);
                            AbstractEffect effect = elements[randomEffectID].GetEffect();

                            effect.ResetSubtext();
                            this.CallEffect(effect);
                        }

                        this.stream.SendEffectVotingToGame(false);
                        this.stream.SetVoting(Shared.VOTING_MODE.COOLDOWN, this.timesUntilRapidFire, elements);
                    }
                }
            }
            else if (Shared.StreamVotingMode == Shared.VOTING_MODE.RAPID_FIRE)
            {
                if (this.progressBarStream.Maximum != 1000 * 10)
                {
                    this.progressBarStream.Maximum = 1000 * 10;
                }

                // Hack to fix Windows' broken-ass progress bar handling
                int value = Math.Max(1, (int)this.stopwatch.ElapsedMilliseconds);
                this.progressBarStream.Value = Math.Max(this.progressBarStream.Maximum - value, 0);
                this.progressBarStream.Value = Math.Max(this.progressBarStream.Maximum - value - 1, 0);

                int remaining = (int)Math.Max(0, (1000 * 10) - this.stopwatch.ElapsedMilliseconds);

                WebsocketHandler.INSTANCE.SendTimeToGame(remaining, 10000, "Rapid-Fire");

                if (this.stopwatch.ElapsedMilliseconds - this.elapsedCount > 100)
                {
                    Shared.Sync?.SendTimeUpdate(remaining, 10000);

                    this.elapsedCount = (int)this.stopwatch.ElapsedMilliseconds;
                }

                if (this.stopwatch.ElapsedMilliseconds >= 1000 * 10) // Set 10 seconds
                {
                    WebsocketHandler.INSTANCE.SendTimeToGame(0);
                    Shared.Sync?.SendTimeUpdate(0, Config.Instance().StreamVotingCooldown);
                    this.elapsedCount = 0;

                    this.progressBarStream.Value = 0;
                    this.progressBarStream.Maximum = Config.Instance().StreamVotingCooldown;

                    this.stopwatch.Restart();
                    Shared.StreamVotingMode = Shared.VOTING_MODE.COOLDOWN;

                    this.labelStreamCurrentMode.Text = "Current Mode: Cooldown";

                    this.stream?.SetVoting(Shared.VOTING_MODE.COOLDOWN, this.timesUntilRapidFire);
                }
            }
            else if (Shared.StreamVotingMode == Shared.VOTING_MODE.COOLDOWN)
            {
                if (this.progressBarStream.Maximum != Config.Instance().StreamVotingCooldown)
                {
                    this.progressBarStream.Maximum = Config.Instance().StreamVotingCooldown;
                }

                // Hack to fix Windows' broken-ass progress bar handling
                int value = Math.Max(1, (int)this.stopwatch.ElapsedMilliseconds);
                this.progressBarStream.Value = Math.Min(value + 1, this.progressBarStream.Maximum);
                this.progressBarStream.Value = Math.Min(value, this.progressBarStream.Maximum);

                int remaining = (int)Math.Max(0, Config.Instance().StreamVotingCooldown - this.stopwatch.ElapsedMilliseconds);

                WebsocketHandler.INSTANCE.SendTimeToGame(remaining, Config.Instance().StreamVotingCooldown, "Cooldown");

                if (this.stopwatch.ElapsedMilliseconds - this.elapsedCount > 100)
                {
                    Shared.Sync?.SendTimeUpdate(remaining, Config.Instance().StreamVotingCooldown);

                    this.elapsedCount = (int)this.stopwatch.ElapsedMilliseconds;
                }

                if (this.stopwatch.ElapsedMilliseconds >= Config.Instance().StreamVotingCooldown)
                {
                    this.elapsedCount = 0;

                    if (Config.Instance().StreamEnableRapidFire)
                    {
                        this.timesUntilRapidFire--;
                    }

                    if (this.timesUntilRapidFire == 0 && Config.Instance().StreamEnableRapidFire)
                    {
                        this.progressBarStream.Value = this.progressBarStream.Maximum = 1000 * 10;

                        this.timesUntilRapidFire = new Random().Next(10, 15);

                        Shared.StreamVotingMode = Shared.VOTING_MODE.RAPID_FIRE;
                        this.labelStreamCurrentMode.Text = "Current Mode: Rapid-Fire";

                        this.stream?.SetVoting(Shared.VOTING_MODE.RAPID_FIRE, this.timesUntilRapidFire);
                    }
                    else
                    {
                        this.progressBarStream.Value = this.progressBarStream.Maximum = Config.Instance().StreamVotingTime;

                        Shared.StreamVotingMode = Shared.VOTING_MODE.VOTING;
                        this.labelStreamCurrentMode.Text = "Current Mode: Voting";

                        this.stream?.SetVoting(Shared.VOTING_MODE.VOTING, this.timesUntilRapidFire);
                    }

                    this.stopwatch.Restart();
                }
            }
        }

        private void PopulateEffectTreeList()
        {
            this.enabledEffectsView.Nodes.Clear();
            this.idToEffectNodeMap.Clear();

            // Add Categories
            foreach (Category cat in Category.Categories)
            {
                if (cat.GetEffectCount() > 0)
                {
                    this.enabledEffectsView.Nodes.Add(new CategoryTreeNode(cat));
                }
            }

            // Add Effects
            foreach (WeightedRandomBag<AbstractEffect>.Entry entry in EffectDatabase.Effects.Get())
            {
                AbstractEffect effect = entry.item;

                if (this.idToEffectNodeMap.ContainsKey(effect.GetID()))
                {
                    MessageBox.Show($"Tried adding effect with ID that was already present: '{effect.GetID()}'");
                }

                TreeNode node = this.enabledEffectsView.Nodes.Find(effect.Category.Name, false).FirstOrDefault();

                string Description = effect.GetDisplayName(DisplayNameType.UI);

                EffectTreeNode addedNode = new(effect, Description)
                {
                    Checked = true,
                };
                node.Nodes.Add(addedNode);
                this.idToEffectNodeMap[effect.GetID()] = addedNode;
            }
        }

        private void PopulatePresets()
        {
            foreach (CategoryTreeNode node in this.enabledEffectsView.Nodes)
            {
                node.Checked = true;
                this.CheckAllChildNodes(node, true);
                node.UpdateCategory();
            }
        }

        private void PopulateMainCooldowns()
        {
            this.comboBoxMainCooldown.Items.Add(new MainCooldownComboBoxItem("10 seconds", 1000 * 10));
            this.comboBoxMainCooldown.Items.Add(new MainCooldownComboBoxItem("20 seconds", 1000 * 20));
            this.comboBoxMainCooldown.Items.Add(new MainCooldownComboBoxItem("30 seconds", 1000 * 30));
            this.comboBoxMainCooldown.Items.Add(new MainCooldownComboBoxItem("1 minute", 1000 * 60));
            this.comboBoxMainCooldown.Items.Add(new MainCooldownComboBoxItem("2 minutes", 1000 * 60 * 2));
            this.comboBoxMainCooldown.Items.Add(new MainCooldownComboBoxItem("3 minutes", 1000 * 60 * 3));
            this.comboBoxMainCooldown.Items.Add(new MainCooldownComboBoxItem("5 minutes", 1000 * 60 * 5));
            this.comboBoxMainCooldown.Items.Add(new MainCooldownComboBoxItem("10 minutes", 1000 * 60 * 10));

            if (this.debug)
            {
                this.comboBoxMainCooldown.Items.Add(new MainCooldownComboBoxItem("DEBUG - 1 second", 1000));
                this.comboBoxMainCooldown.Items.Add(new MainCooldownComboBoxItem("DEBUG - 500ms", 500));
                this.comboBoxMainCooldown.Items.Add(new MainCooldownComboBoxItem("DEBUG - 50ms", 50));
            }

            this.comboBoxMainCooldown.SelectedIndex = 3;

            Config.Instance().MainCooldown = 1000 * 60;
        }

        private class MainCooldownComboBoxItem
        {
            public readonly string Text;
            public readonly int Time;

            public MainCooldownComboBoxItem(string text, int time)
            {
                this.Text = text;
                this.Time = time;
            }

            public override string ToString() => this.Text;
        }

        private void MainCooldownComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            MainCooldownComboBoxItem item = (MainCooldownComboBoxItem)this.comboBoxMainCooldown.SelectedItem;
            Config.Instance().MainCooldown = item.Time;

            if (!Shared.TimerEnabled)
            {
                this.progressBarMain.Value = 0;
                this.progressBarMain.Maximum = Config.Instance().MainCooldown;
                this.elapsedCount = 0;
                this.stopwatch.Reset();
            }
        }

        private void PopulateVotingTimes()
        {
            this.comboBoxVotingTime.Items.Add(new VotingTimeComboBoxItem("30 seconds", 1000 * 30));
            this.comboBoxVotingTime.Items.Add(new VotingTimeComboBoxItem("1 minute", 1000 * 60));

            if (this.debug)
            {
                this.comboBoxVotingTime.Items.Add(new VotingTimeComboBoxItem("25ms", 25));
            }

            this.comboBoxVotingTime.SelectedIndex = 0;

            Config.Instance().StreamVotingTime = 1000 * 30;
        }

        private class VotingTimeComboBoxItem
        {
            public readonly int VotingTime;
            public readonly string Text;

            public VotingTimeComboBoxItem(string text, int votingTime)
            {
                this.Text = text;
                this.VotingTime = votingTime;
            }

            public override string ToString() => this.Text;
        }

        private void ComboBoxVotingTime_SelectedIndexChanged(object sender, EventArgs e)
        {
            VotingTimeComboBoxItem item = (VotingTimeComboBoxItem)this.comboBoxVotingTime.SelectedItem;
            Config.Instance().StreamVotingTime = item.VotingTime;
        }

        private void PopulateVotingCooldowns()
        {
            this.comboBoxVotingCooldown.Items.Add(new VotingCooldownComboBoxItem("30 seconds", 1000 * 30));
            this.comboBoxVotingCooldown.Items.Add(new VotingCooldownComboBoxItem("1 minute", 1000 * 60));
            this.comboBoxVotingCooldown.Items.Add(new VotingCooldownComboBoxItem("2 minutes", 1000 * 60 * 2));
            this.comboBoxVotingCooldown.Items.Add(new VotingCooldownComboBoxItem("3 minutes", 1000 * 60 * 3));
            this.comboBoxVotingCooldown.Items.Add(new VotingCooldownComboBoxItem("5 minutes", 1000 * 60 * 5));
            this.comboBoxVotingCooldown.Items.Add(new VotingCooldownComboBoxItem("10 minutes", 1000 * 60 * 10));

            if (this.debug)
            {
                this.comboBoxVotingCooldown.Items.Add(new VotingCooldownComboBoxItem("5 seconds", 1000 * 5));
                this.comboBoxVotingCooldown.Items.Add(new VotingCooldownComboBoxItem("1 second", 1000));
                this.comboBoxVotingCooldown.Items.Add(new VotingCooldownComboBoxItem("25ms", 25));
            }

            this.comboBoxVotingCooldown.SelectedIndex = 1;

            Config.Instance().StreamVotingCooldown = 1000 * 60;
        }

        private class VotingCooldownComboBoxItem
        {
            public readonly int VotingCooldown;
            public readonly string Text;

            public VotingCooldownComboBoxItem(string text, int votingCooldown)
            {
                this.Text = text;
                this.VotingCooldown = votingCooldown;
            }

            public override string ToString() => this.Text;
        }

        private void ComboBoxVotingCooldown_SelectedIndexChanged(object sender, EventArgs e)
        {
            VotingCooldownComboBoxItem item = (VotingCooldownComboBoxItem)this.comboBoxVotingCooldown.SelectedItem;
            Config.Instance().StreamVotingCooldown = item.VotingCooldown;
        }

        private class StreamModeComboBoxItem
        {
            public readonly StreamHandler.STREAM_MODE StreamMode;
            public readonly string Text;

            public StreamModeComboBoxItem(string text, StreamHandler.STREAM_MODE streamMode)
            {
                this.Text = text;
                this.StreamMode = streamMode;
            }

            public override string ToString() => this.Text;
        }

        private void PopulateStreamModes()
        {
            this.comboBoxSettingsStreamMode.Items.Add(new StreamModeComboBoxItem("Twitch", StreamHandler.STREAM_MODE.TWITCH));
            this.comboBoxSettingsStreamMode.Items.Add(new StreamModeComboBoxItem("YouTube", StreamHandler.STREAM_MODE.YOUTUBE));
            this.comboBoxSettingsStreamMode.Items.Add(new StreamModeComboBoxItem("Kick", StreamHandler.STREAM_MODE.KICK));

            this.comboBoxSettingsStreamMode.SelectedIndex = 0;
        }

        private void DoAutostart()
        {
            if (!this.checkBoxAutoStart.Checked)
            {
                return;
            }

            this.elapsedCount = 0;
            this.stopwatch.Reset();
            this.SetEnabled(true);
            EffectDatabase.ResetEffectCooldowns();

            if (Config.Instance().Experimental_RunEffectOnAutoStart && !Shared.IsStreamMode)
            {
                this.CallEffect();
            }
        }

        private void SetEnabled(bool enabled)
        {
            Shared.TimerEnabled = enabled;
            if (Shared.TimerEnabled && !Config.Instance().DebugChatCommandsEnabled)
            {
                this.stopwatch.Start();
            }
            else
            {
                this.stopwatch.Stop();
            }

            this.buttonMainToggle.Enabled = true;
            (Shared.IsStreamMode ? this.buttonStreamToggle : this.buttonMainToggle).Text = Shared.TimerEnabled ? "Stop / Pause" : "Start / Resume";
            this.comboBoxMainCooldown.Enabled =
                this.buttonSwitchMode.Enabled =
                this.buttonResetMain.Enabled =
                this.buttonResetStream.Enabled = !Shared.TimerEnabled;

            this.comboBoxVotingTime.Enabled =
                this.comboBoxVotingCooldown.Enabled =
                this.textBoxSeed.Enabled = !Shared.TimerEnabled;
        }

        private void ButtonMainToggle_Click(object sender, EventArgs e) => this.SetEnabled(!Shared.TimerEnabled);

        private void CheckAllChildNodes(TreeNode treeNode, bool nodeChecked)
        {
            foreach (TreeNode node in treeNode.Nodes)
            {
                node.Checked = nodeChecked;
                if (node is EffectTreeNode etn)
                {
                    EffectDatabase.SetEffectEnabled(etn.Effect, etn.Checked);
                }

                if (node.Nodes.Count > 0)
                {
                    // If the current node has child nodes, call the CheckAllChildsNodes method recursively.
                    this.CheckAllChildNodes(node, nodeChecked);
                }
            }
        }

        private void EnabledEffectsView_AfterCheck(object sender, TreeViewEventArgs e)
        {
            if (e.Action != TreeViewAction.Unknown)
            {
                if (e.Node is EffectTreeNode etn)
                {
                    EffectDatabase.SetEffectEnabled(etn.Effect, etn.Checked);
                }

                if (e.Node.Nodes.Count > 0)
                {
                    this.CheckAllChildNodes(e.Node, e.Node.Checked);
                }

                foreach (CategoryTreeNode node in this.enabledEffectsView.Nodes)
                {
                    node.UpdateCategory();
                }
            }
        }

        private void LoadPreset(Dictionary<string, bool> enabledEffects)
        {
            this.PopulatePresets();

            foreach (KeyValuePair<string, bool> pair in enabledEffects)
            {
                if (this.idToEffectNodeMap.TryGetValue(pair.Key, out EffectTreeNode node))
                {
                    node.Checked = pair.Value;
                    EffectDatabase.SetEffectEnabled(node.Effect, pair.Value);
                }
            }

            foreach (CategoryTreeNode node in this.enabledEffectsView.Nodes)
            {
                node.UpdateCategory();
            }
        }

        private class CategoryTreeNode : TreeNode
        {
            private readonly Category category;

            public CategoryTreeNode(Category _category)
            {
                this.category = _category;
                this.Name = this.Text = this.category.Name;
            }

            public void UpdateCategory()
            {
                bool newChecked = true;
                int enabled = 0;
                foreach (TreeNode node in this.Nodes)
                {
                    if (node.Checked)
                    {
                        enabled++;
                    }
                    else
                    {
                        newChecked = false;
                    }
                }

                this.Checked = newChecked;
                this.Text = this.Name + $" ({enabled}/{this.Nodes.Count})";
            }
        }

        private class EffectTreeNode : TreeNode
        {
            public readonly AbstractEffect Effect;

            public EffectTreeNode(AbstractEffect effect, string description)
            {
                this.Effect = effect;

                this.Name = this.Text = description;
            }
        }

        private void ExitToolStripMenuItem_Click(object sender, EventArgs e) => this.Close();

        private void LoadPresetToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenFileDialog dialog = new()
            {
                Filter = "Preset File|*.cfg",
                Title = "Load Preset"
            };
            dialog.ShowDialog();

            if (dialog.FileName != "")
            {
                string content = System.IO.File.ReadAllText(dialog.FileName);
                string[] enabledEffects = content.Split(',');

                Dictionary<string, bool> effectsMap = new();
                foreach (WeightedRandomBag<AbstractEffect>.Entry entry in EffectDatabase.Effects.Get())
                {
                    effectsMap.Add(entry.item.GetID(), false);
                }

                foreach (string effect in enabledEffects)
                {
                    effectsMap[effect] = true;
                }

                this.LoadPreset(effectsMap);
            }

            dialog.Dispose();
        }

        private void SavePresetToolStripMenuItem_Click(object sender, EventArgs e)
        {
            List<string> enabledEffects = new();
            foreach (EffectTreeNode node in this.idToEffectNodeMap.Values)
            {
                if (node.Checked)
                {
                    enabledEffects.Add(node.Effect.GetID());
                }
            }

            string joined = string.Join(",", enabledEffects);

            SaveFileDialog dialog = new()
            {
                Filter = "Preset File|*.cfg",
                Title = "Save Preset"
            };
            dialog.ShowDialog();

            if (dialog.FileName != "")
            {
                System.IO.File.WriteAllText(dialog.FileName, joined);
            }

            dialog.Dispose();
        }

        private async void ButtonConnectStream_Click(object sender, EventArgs e)
        {
            if (this.stream?.IsConnected() == true || this.buttonConnectStream.Text == "Disconnect")
            {
                this.stream?.Kill();
                this.stream = null;

                this.buttonSwitchMode.Enabled = true;

                this.comboBoxVotingTime.Enabled = true;
                this.comboBoxVotingCooldown.Enabled = true;

                this.textBoxStreamAccessToken.Enabled = true;
                this.textBoxStreamClientID.Enabled = true;

                this.buttonStreamToggle.Enabled = false;

                this.checkBoxTwitchUsePolls.Enabled = true;

                this.buttonConnectStream.Text = "Connect to Stream";

                if (!this.tabs.TabPages.Contains(this.tabEffects))
                {
                    this.tabs.TabPages.Insert(this.tabs.TabPages.IndexOf(this.tabStream), this.tabEffects);
                }

                this.SetEnabled(false);

                return;
            }

            // Use profile-based MultiStream when enabled; otherwise preserve legacy single-source mode.
            bool useMultiStream = Config.Instance().MultiStreamEnabled;
            bool hasValidMultiStreamSource = Config.Instance().MultiStreamSources != null &&
                Config.Instance().MultiStreamSources.Any(source =>
                    source != null && source.Enabled && (source.UseForChat || source.UseForVoting) &&
                    !string.IsNullOrWhiteSpace(source.Channel) &&
                    (!string.Equals(source.Platform, "Twitch", StringComparison.OrdinalIgnoreCase) ||
                     (!string.IsNullOrWhiteSpace(source.ClientID) && !string.IsNullOrWhiteSpace(source.AccessToken))));

            if ((useMultiStream && hasValidMultiStreamSource) ||
                (!useMultiStream && !string.IsNullOrEmpty(Config.Instance().StreamClientID)))
            {
                // Legacy Twitch mode needs credentials; MultiStream validates each Twitch profile separately.
                if (!useMultiStream && StreamHandler.IsTwitchMode() && string.IsNullOrEmpty(Config.Instance().StreamAccessToken))
                {
                    return;
                }

                this.buttonSwitchMode.Enabled = false;

                this.buttonConnectStream.Enabled = false;
                this.textBoxStreamAccessToken.Enabled = false;
                this.textBoxStreamClientID.Enabled = false;

                this.comboBoxSettingsStreamMode.Enabled = false;

                this.stream = useMultiStream
                    ? new MultiStreamConnection(Config.Instance().MultiStreamSources)
                    : StreamHandler.GetStreamConnection();

                this.stream.OnRapidFireEffect += (_sender, rapidFireArgs) => this.Invoke(new Action(() =>
                {
                    if (Shared.StreamVotingMode == Shared.VOTING_MODE.RAPID_FIRE)
                    {
                        if (Shared.Sync != null)
                        {
                            Shared.Sync.SendEffect(rapidFireArgs.Effect, 1000 * 15);
                        }
                        else
                        {
                            EffectDatabase.ShouldCooldown = false;

                            Task _ = rapidFireArgs.Effect.RunEffect(-1, 1000 * 15);
                            this.AddEffectToListBox(rapidFireArgs.Effect);

                            EffectDatabase.ShouldCooldown = true;
                        }
                    }
                }));

                this.stream.OnLoginError += (_sender, _e) =>
                {
                    string errorTitle = "Stream Login Error";
                    string errorMessage = "There was an error trying to log in to the account. Invalid Access Token?";

                    if (_sender is MultiStreamConnection multiStreamConnection)
                    {
                        errorTitle = "MultiStream Connection Error";
                        errorMessage = string.IsNullOrWhiteSpace(multiStreamConnection.LastError)
                            ? "No se pudo conectar ninguna fuente MultiStream. Revisá los canales y credenciales."
                            : multiStreamConnection.LastError;
                    }
                    else if (_sender is KickChatConnection kickConnection)
                    {
                        errorTitle = "Kick Chat Connection Error";
                        string details = string.IsNullOrWhiteSpace(kickConnection.LastError)
                            ? "Kick did not provide additional connection details."
                            : kickConnection.LastError;

                        errorMessage = "Could not connect to the public Kick chatroom.\n\n" +
                            "Details: " + details + "\n\n" +
                            "Kick mode does not require an Access Token. Enter only the channel name, " +
                            "without @ or a URL.";
                    }

                    MessageBox.Show(errorMessage, errorTitle);
                    this.Invoke(new Action(() =>
                    {
                        this.buttonSwitchMode.Enabled = true;

                        this.buttonConnectStream.Enabled = true;
                        this.textBoxStreamAccessToken.Enabled = true;
                        this.textBoxStreamClientID.Enabled = true;

                        this.comboBoxSettingsStreamMode.Enabled = true;
                    }));
                    this.stream?.Kill();
                    this.stream = null;
                };

                this.stream.OnConnected += (_sender, _e) => this.Invoke(new Action(() =>
                {
                    this.buttonConnectStream.Enabled = true;
                    this.buttonStreamToggle.Enabled = true;

                    this.buttonConnectStream.Text = "Disconnect";

                    this.textBoxStreamAccessToken.Enabled = false;
                    this.textBoxStreamClientID.Enabled = false;

                    this.checkBoxTwitchUsePolls.Enabled = false;

                    this.comboBoxSettingsStreamMode.Enabled = false;

                    if (this.stream is MultiStreamConnection multiStream)
                    {
                        this.labelMultiStreamStatus.Text = multiStream.GetStatusSummary();
                    }
                }));

                this.stream.OnDisconnected += (_sender, _e) => this.Invoke(new Action(() =>
                {
                    this.stream = null;

                    this.buttonSwitchMode.Enabled = true;

                    this.comboBoxVotingTime.Enabled = true;
                    this.comboBoxVotingCooldown.Enabled = true;

                    this.textBoxStreamAccessToken.Enabled = true;
                    this.textBoxStreamClientID.Enabled = true;

                    this.buttonStreamToggle.Enabled = false;

                    this.checkBoxTwitchUsePolls.Enabled = true;

                    this.comboBoxSettingsStreamMode.Enabled = true;

                    this.buttonConnectStream.Text = "Connect to Stream";

                    if (!this.tabs.TabPages.Contains(this.tabEffects))
                    {
                        this.tabs.TabPages.Insert(this.tabs.TabPages.IndexOf(this.tabStream), this.tabEffects);
                    }

                    this.SetEnabled(false);
                }));

                await this.stream.TryConnect();
            }
        }

        private void UpdateStreamConnectButtonState()
        {
            bool enabled;
            if (Config.Instance().MultiStreamEnabled)
            {
                enabled = Config.Instance().MultiStreamSources != null &&
                    Config.Instance().MultiStreamSources.Any(source =>
                        source != null && source.Enabled && (source.UseForChat || source.UseForVoting) &&
                        !string.IsNullOrWhiteSpace(source.Channel) &&
                        (!string.Equals(source.Platform, "Twitch", StringComparison.OrdinalIgnoreCase) ||
                         (!string.IsNullOrWhiteSpace(source.ClientID) && !string.IsNullOrWhiteSpace(source.AccessToken))));
            }
            else
            {
                enabled = !string.IsNullOrEmpty(Config.Instance().StreamClientID) &&
                    (!string.IsNullOrEmpty(Config.Instance().StreamAccessToken) || !StreamHandler.IsTwitchMode());
            }

            this.buttonConnectStream.Enabled = enabled;
        }

        private void TextBoxOAuth_TextChanged(object sender, EventArgs e)
        {
            Config.Instance().StreamAccessToken = this.textBoxStreamAccessToken.Text;

            this.UpdateStreamConnectButtonState();
        }

        private void SwitchMode(bool isStreamMode)
        {
            if (!isStreamMode)
            {
                this.buttonSwitchMode.Text = "Stream";

                if (!this.tabs.TabPages.Contains(this.tabMain))
                {
                    this.tabs.TabPages.Insert(0, this.tabMain);
                }

                this.tabs.SelectedIndex = 0;
                this.tabs.TabPages.Remove(this.tabStream);

                this.listLastEffectsMain.Items.Clear();
                this.progressBarMain.Value = 0;

                this.elapsedCount = 0;

                this.stopwatch.Reset();
                this.SetEnabled(false);
            }
            else
            {
                this.buttonSwitchMode.Text = "Main";

                if (!this.tabs.TabPages.Contains(this.tabStream))
                {
                    this.tabs.TabPages.Insert(0, this.tabStream);
                }

                this.tabs.SelectedIndex = 0;
                this.tabs.TabPages.Remove(this.tabMain);

                this.listLastEffectsStream.Items.Clear();
                this.progressBarStream.Value = 0;

                this.elapsedCount = 0;

                this.stopwatch.Reset();
                this.SetEnabled(false);
            }

            this.UpdatePollTabVisibility();

            EffectDatabase.ResetEffectCooldowns();
        }

        private void ButtonSwitchMode_Click(object sender, EventArgs e)
        {
            Shared.IsStreamMode = !Shared.IsStreamMode;
            this.SwitchMode(Shared.IsStreamMode);
        }

        private void ButtonStreamToggle_Click(object sender, EventArgs e) => this.SetEnabled(!Shared.TimerEnabled);

        private void TextBoxSeed_TextChanged(object sender, EventArgs e)
        {
            Config.Instance().Seed = this.textBoxSeed.Text;
            RandomHandler.SetSeed(Config.Instance().Seed);
        }

        private void ButtonResetMain_Click(object sender, EventArgs e)
        {
            this.SetEnabled(false);
            RandomHandler.SetSeed(Config.Instance().Seed);
            this.stopwatch.Reset();
            this.elapsedCount = 0;
            this.progressBarMain.Value = 0;
            this.buttonMainToggle.Enabled = true;
            this.buttonMainToggle.Text = "Start / Resume";

            EffectDatabase.ResetEffectCooldowns();
        }

        private void CheckBoxShowLastEffectsMain_CheckedChanged(object sender, EventArgs e)
        {
            Config.Instance().MainShowLastEffects
                = this.listLastEffectsMain.Visible
                = this.checkBoxShowLastEffectsMain.Checked;
        }

        private void CheckBoxShowLastEffectsStream_CheckedChanged(object sender, EventArgs e)
        {
            Config.Instance().StreamShowLastEffects
                = this.listLastEffectsStream.Visible
                = this.checkBoxShowLastEffectsStream.Checked;
        }

        private void CheckBoxStreamAllowOnlyEnabledEffects_CheckedChanged(object sender, EventArgs e) => Config.Instance().StreamAllowOnlyEnabledEffectsRapidFire = this.checkBoxStreamAllowOnlyEnabledEffects.Checked;

        private void ButtonResetStream_Click(object sender, EventArgs e)
        {
            this.SetEnabled(false);
            RandomHandler.SetSeed(Config.Instance().Seed);
            this.stopwatch.Reset();
            this.elapsedCount = 0;
            this.labelStreamCurrentMode.Text = "Current Mode: Cooldown";
            Shared.StreamVotingMode = Shared.VOTING_MODE.COOLDOWN;
            this.timesUntilRapidFire = new Random().Next(10, 15);
            this.progressBarStream.Value = 0;
            this.buttonStreamToggle.Enabled = this.stream?.IsConnected() == true;
            this.buttonStreamToggle.Text = "Start / Resume";

            EffectDatabase.ResetEffectCooldowns();
        }

        private void CheckBoxStream3TimesCooldown_CheckedChanged(object sender, EventArgs e) => Config.Instance().Stream3TimesCooldown = this.checkBoxStream3TimesCooldown.Checked;

        private void ButtonEffectsToggleAll_Click(object sender, EventArgs e)
        {
            bool oneEnabled = false;
            foreach (CategoryTreeNode node in this.enabledEffectsView.Nodes)
            {
                if (node.Checked)
                {
                    oneEnabled = true;
                    break;
                }

                foreach (TreeNode child in node.Nodes)
                {
                    if (child.Checked)
                    {
                        oneEnabled = true;
                        break;
                    }
                }
            }

            foreach (CategoryTreeNode node in this.enabledEffectsView.Nodes)
            {
                node.Checked = !oneEnabled;
                this.CheckAllChildNodes(node, !oneEnabled);
                node.UpdateCategory();
            }
        }

        private void ViceCityToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Shared.SelectedGame = "vice_city";
            Config.Instance().EnabledEffects.Clear();
            EffectDatabase.PopulateEffects("vice_city");
            this.PopulateEffectTreeList();

            foreach (CategoryTreeNode node in this.enabledEffectsView.Nodes)
            {
                node.Checked = false;
                this.CheckAllChildNodes(node, false);
                node.UpdateCategory();
            }

            foreach (CategoryTreeNode node in this.enabledEffectsView.Nodes)
            {
                node.UpdateCategory();
            }
        }

        private void SanAndreasToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Shared.SelectedGame = "san_andreas";
            Config.Instance().EnabledEffects.Clear();
            EffectDatabase.PopulateEffects("san_andreas");
            this.PopulateEffectTreeList();

            foreach (CategoryTreeNode node in this.enabledEffectsView.Nodes)
            {
                node.Checked = false;
                this.CheckAllChildNodes(node, false);
                node.UpdateCategory();
            }

            foreach (CategoryTreeNode node in this.enabledEffectsView.Nodes)
            {
                node.UpdateCategory();
            }
        }

        private void CheckBoxStreamCombineVotingMessages_CheckedChanged(object sender, EventArgs e) => Config.Instance().StreamCombineChatMessages = this.checkBoxStreamCombineVotingMessages.Checked;

        private void SetPollsTabVisible(bool visible)
        {
            bool contains = this.tabs.TabPages.Contains(this.tabPolls);

            if (visible && !contains)
            {
                this.tabs.TabPages.Insert(2, this.tabPolls);
            }

            if (!visible && contains)
            {
                this.tabs.TabPages.Remove(this.tabPolls);
            }
        }

        private void UpdatePollTabVisibility()
        {
            this.SetPollsTabVisible(false);

            if (Shared.IsStreamMode)
            {
                if (StreamHandler.IsTwitchMode() && Config.Instance().TwitchUsePolls)
                {
                    this.SetPollsTabVisible(true);
                }
            }
        }

        private void CheckBoxTwitchUsePolls_CheckedChanged(object sender, EventArgs e)
        {
            Config.Instance().TwitchUsePolls = this.checkBoxTwitchUsePolls.Checked;
            this.UpdatePollTabVisibility();
        }

        private void CheckBoxStreamEnableMultipleEffects_CheckedChanged(object sender, EventArgs e) => Config.Instance().StreamEnableMultipleEffects = this.checkBoxStreamEnableMultipleEffects.Checked;

        private void NumericUpDownTwitchPollsBitsCost_ValueChanged(object sender, EventArgs e) => Config.Instance().TwitchPollsBitsCost = decimal.ToInt32(this.numericUpDownTwitchPollsBitsCost.Value);

        private void NumericUpDownTwitchPollsChannelPointsCost_ValueChanged(object sender, EventArgs e) => Config.Instance().TwitchPollsChannelPointsCost = decimal.ToInt32(this.numericUpDownTwitchPollsChannelPointsCost.Value);

        private void CheckBoxTwitchPollsPostMessages_CheckedChanged(object sender, EventArgs e) => Config.Instance().TwitchPollsPostMessages = this.checkBoxTwitchPollsPostMessages.Checked;

        private void EnabledEffectsView_NodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node is EffectTreeNode node && e.Clicks == 2 && this.experimentalModeEnabled)
            {
                EffectDatabase.ShouldCooldown = false;
                this.CallEffect(node.Effect);
                EffectDatabase.ShouldCooldown = true;
            }
        }

        private string FilterSyncCharacters(string text) => Regex.Replace(text, "[^A-Za-z0-9]", "");

        private void UpdateButtonState()
        {
            this.buttonSyncConnect.Enabled
                = !string.IsNullOrEmpty(this.textBoxSyncServer.Text)
                && !string.IsNullOrEmpty(this.textBoxSyncChannel.Text)
                && !string.IsNullOrEmpty(this.textBoxSyncUsername.Text);
        }

        private void TextBoxSyncServer_TextChanged(object sender, EventArgs e)
        {
            Config.Instance().SyncServer = this.textBoxSyncServer.Text;
            this.UpdateButtonState();
        }
        private void TextBoxSyncChannel_TextChanged(object sender, EventArgs e)
        {
            this.textBoxSyncChannel.Text = this.FilterSyncCharacters(this.textBoxSyncChannel.Text);
            Config.Instance().SyncChannel = this.textBoxSyncChannel.Text;
            this.UpdateButtonState();
        }

        private void TextBoxSyncUsername_TextChanged(object sender, EventArgs e)
        {
            this.textBoxSyncUsername.Text = this.FilterSyncCharacters(this.textBoxSyncUsername.Text);
            Config.Instance().SyncUsername = this.textBoxSyncUsername.Text;
            this.UpdateButtonState();
        }

        private void UpdateSyncConnectionState(int state)
        {
            this.Invoke(new Action(() =>
            {
                if (state == 0) // Not connected
                {
                    this.textBoxSyncServer.Enabled
                        = this.textBoxSyncChannel.Enabled
                        = this.textBoxSyncUsername.Enabled
                        = this.buttonSyncConnect.Enabled
                        = true;

                    this.buttonSyncConnect.Text = "Connect";

                    this.buttonSwitchMode.Enabled = true;
                    this.buttonMainToggle.Enabled = true;
                    this.buttonResetMain.Enabled = true;
                    this.comboBoxMainCooldown.Enabled = true;
                    this.enabledEffectsView.Enabled = true;
                    this.textBoxSeed.Enabled = true;
                    this.checkBoxAutoStart.Enabled = true;
                    this.buttonEffectsToggleAll.Enabled = true;
                    this.numericUpDownEffectCooldown.Enabled = true;
                    this.buttonExperimentalRunEffect.Enabled = true;

                    Shared.Sync = null;
                }
                else if (state == 1) // Connecting...
                {
                    this.textBoxSyncServer.Enabled
                        = this.textBoxSyncChannel.Enabled
                        = this.textBoxSyncUsername.Enabled
                        = this.buttonSyncConnect.Enabled
                        = false;

                    this.buttonSyncConnect.Text = "Connecting...";
                }
                else if (state == 2) // Connected
                {
                    this.textBoxSyncServer.Enabled
                        = this.textBoxSyncChannel.Enabled
                        = this.textBoxSyncUsername.Enabled
                        = false;

                    this.buttonSyncConnect.Enabled = true;
                    this.buttonSyncConnect.Text = "Disconnect";
                }
            }));
        }

        private void ShowMessageBox(string text, string caption) => this.Invoke(new Action(() => MessageBox.Show(this, text, caption)));

        private void AddToSyncChatHistory(string message)
        {
            this.Invoke(new Action(() =>
            {
                this.listBoxSyncChat.Items.Add(message);
                this.listBoxSyncChat.TopIndex = this.listBoxSyncChat.Items.Count - 1;
            }));
        }

        private void ClearSyncChatHistory() => this.Invoke(new Action(() => this.listBoxSyncChat.Items.Clear()));

        private void ButtonSyncConnect_Click(object sender, EventArgs e)
        {
            Shared.Sync?.Disconnect();

            if (this.buttonSyncConnect.Text == "Disconnect")
            {
                this.UpdateSyncConnectionState(0);
                return;
            }

            this.ClearSyncChatHistory();

            Shared.Sync = new Sync(
                this.textBoxSyncServer.Text,
                this.textBoxSyncChannel.Text,
                this.textBoxSyncUsername.Text
            );

            this.UpdateSyncConnectionState(1);

            Shared.Sync.OnConnectionFailed += (_sender, args) =>
            {
                this.ShowMessageBox("Connection failed - is the server running?", "Error");
                this.UpdateSyncConnectionState(0);
            };

            Shared.Sync.OnUsernameInUse += (_sender, args) =>
            {
                this.ShowMessageBox("Username already in use!", "Error");
                this.UpdateSyncConnectionState(0);
            };

            Shared.Sync.OnConnectionSuccessful += (_sender, args) =>
            {
                this.ShowMessageBox("Successfully connected!", "Connected");
                this.AddToSyncChatHistory($"Successfully connected to channel: {this.textBoxSyncChannel.Text}");
                this.UpdateSyncConnectionState(2);

                this.Invoke(new Action(() =>
                {
                    if (!args.IsHost)
                    {
                        this.SwitchMode(false);

                        this.buttonSwitchMode.Enabled = false;
                        this.buttonMainToggle.Enabled = false;
                        this.buttonResetMain.Enabled = false;
                        this.comboBoxMainCooldown.Enabled = false;
                        this.enabledEffectsView.Enabled = false;
                        this.buttonResetMain.Enabled = false;
                        this.textBoxSeed.Enabled = false;
                        this.checkBoxAutoStart.Enabled = false;
                        this.buttonEffectsToggleAll.Enabled = false;
                        this.numericUpDownEffectCooldown.Enabled = false;
                        this.buttonExperimentalRunEffect.Enabled = false;
                    }

                    this.labelSyncHost.Text = $"Host: {args.HostUsername}";
                    if (args.IsHost)
                    {
                        this.labelSyncHost.Text += " (You!)";
                    }
                }));
            };

            Shared.Sync.OnHostLeftChannel += (_sender, args) =>
            {
                this.ShowMessageBox("Host has left the channel; Disconnected.", "Host Left");
                this.AddToSyncChatHistory("Host has left the channel; Disconnected.");
                this.UpdateSyncConnectionState(0);
            };

            Shared.Sync.OnVersionMismatch += (_sender, args) =>
            {
                this.ShowMessageBox($"Channel is v{args.Version} but you have v{Shared.Version}; Disconnected.", "Version Mismatch");
                this.AddToSyncChatHistory($"Channel is v{args.Version} but you have v{Shared.Version}; Disconnected.");
                this.UpdateSyncConnectionState(0);
            };

            Shared.Sync.OnUserJoined += (_sender, args) => this.AddToSyncChatHistory($"{args.Username} joined!");

            Shared.Sync.OnUserLeft += (_sender, args) => this.AddToSyncChatHistory($"{args.Username} left!");

            Shared.Sync.OnChatMessage += (_sender, args) => this.AddToSyncChatHistory($"{args.Username}: {args.Message}");

            Shared.Sync.OnTimeUpdate += (_sender, args) =>
            {
                if (!Shared.Sync.IsHost)
                {
                    WebsocketHandler.INSTANCE.SendTimeToGame(args.Remaining, args.Total);
                }
            };

            Shared.Sync.OnEffect += (_sender, args) =>
            {
                AbstractEffect effect = EffectDatabase.GetByWord(args.Word);
                if (effect != null)
                {
                    if (string.IsNullOrEmpty(args.Subtext) || args.Subtext == "N/A")
                    {
                        effect.ResetSubtext();
                    }
                    else
                    {
                        effect.SetSubtext(args.Subtext);
                    }

                    EffectDatabase.RunEffect(effect, args.Seed, args.Duration);

                    this.AddEffectToListBox(effect);
                }
            };

            Shared.Sync.OnVotes += (_sender, args) =>
            {
                string[] effects = args.Effects;
                int[] votes = args.Votes;

                WebsocketHandler.INSTANCE.SendVotes(effects, votes, args.LastChoice);
            };

            Shared.Sync.Connect();
        }

        private void TextBoxSyncChat_TextChanged(object sender, EventArgs e) => this.buttonSyncSend.Enabled = Shared.Sync != null && !string.IsNullOrEmpty(this.textBoxSyncChat.Text);

        private void ButtonSyncSend_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(this.textBoxSyncChat.Text))
            {
                Shared.Sync?.SendChatMessage(this.textBoxSyncChat.Text);
                this.textBoxSyncChat.Text = "";
            }
        }

        private void TextBoxSyncChat_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !string.IsNullOrEmpty(this.textBoxSyncChat.Text))
            {
                Shared.Sync?.SendChatMessage(this.textBoxSyncChat.Text);
                this.textBoxSyncChat.Text = "";
            }
        }

        private void CheckBoxExperimental_EnableEffectOnAutoStart_CheckedChanged(object sender, EventArgs e) => Config.Instance().Experimental_RunEffectOnAutoStart = this.checkBoxExperimental_RunEffectOnAutoStart.Checked;

        private void ExperimentalToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!this.tabs.TabPages.Contains(this.tabExperimental))
            {
                this.tabs.TabPages.Add(this.tabExperimental);
            }

            this.experimentalToolStripMenuItem.Visible = false;
            this.experimentalModeEnabled = true;
        }

        private void ButtonExperimentalRunEffect_Click(object sender, EventArgs e)
        {
            string textInput = this.textBoxExperimentalEffectName.Text;
            if (string.IsNullOrEmpty(textInput))
            {
                return;
            }

            // Try and get an effect by it's ID
            AbstractEffect effect = EffectDatabase.GetByID(textInput);
            // Try and get an effect by it's ID with "effect_" at the start
            if (effect == null)
            {
                effect = EffectDatabase.GetByID($"effect_{textInput}");
            }
            // Try and get an effect by it's "cheat" word
            if (effect == null)
            {
                effect = EffectDatabase.GetByWord(textInput);
            }

            if (effect != null)
            {
                EffectDatabase.ShouldCooldown = false;
                this.CallEffect(effect);
                EffectDatabase.ShouldCooldown = true;
                return;
            }

            int duration = Config.GetEffectDuration();
            WebsocketHandler.INSTANCE.SendEffectToGame(this.textBoxExperimentalEffectName.Text, new
            {
                seed = RandomHandler.Next(9999999)
            }, duration);
        }

        private void TextBoxExperimentalEffectName_TextChanged(object sender, EventArgs e) => Config.Instance().Experimental_EffectName = this.textBoxExperimentalEffectName.Text;

        private void CheckBoxAutoStart_CheckedChanged(object sender, EventArgs e) => Config.Instance().AutoStart = this.checkBoxAutoStart.Checked;

        private void CheckBoxStreamEnableRapidFire_CheckedChanged(object sender, EventArgs e) => Config.Instance().StreamEnableRapidFire = this.checkBoxStreamEnableRapidFire.Checked;

        private void CheckBoxStreamMajorityVotes_CheckedChanged(object sender, EventArgs e)
        {
            Config.Instance().StreamMajorityVotes = this.checkBoxStreamMajorityVotes.Checked;

            this.checkBoxStreamEnableMultipleEffects.Enabled = Config.Instance().StreamMajorityVotes;
        }

        private void LinkLabelTwitchGetAccessToken_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) => System.Diagnostics.Process.Start("https://chaos.lord.moe/");

        private void TextBoxStreamClientID_TextChanged(object sender, EventArgs e)
        {
            Config.Instance().StreamClientID = this.textBoxStreamClientID.Text;

            this.UpdateStreamConnectButtonState();
        }

        private void NumericUpDownEffectCooldown_ValueChanged(object sender, EventArgs e) => Config.Instance().EffectsCooldownNotActivating = (int)this.numericUpDownEffectCooldown.Value;

        private void checkBoxStreamHideVotingEffectsIngame_CheckedChanged(object sender, EventArgs e) => Config.Instance().StreamHideVotingEffectsIngame = this.checkBoxStreamHideVotingEffectsIngame.Checked;

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e) => MessageBox.Show(this, $"Version: v{Shared.Version}\nTotal Effects: {EffectDatabase.Effects.Count}", "About");

        private void checkForUpdateToolStripMenuItem_Click(object sender, EventArgs e) => UpdateChecker.CheckForUpdate(false);

        private void numericWebsocketPort_ValueChanged(object sender, EventArgs e) => Config.Instance().WebsocketPort = (int)this.numericWebsocketPort.Value;

        private void buttonRestartWebsocket_Click(object sender, EventArgs e)
        {
            WebsocketHandler.INSTANCE.RestartWebsocketServer();
            this.UpdateProgramText();
        }

        private void checkBoxSettingsCheckForUpdatesAtLaunch_CheckedChanged(object sender, EventArgs e) => Config.Instance().CheckForUpdatesAtLaunch = this.checkBoxSettingsCheckForUpdatesAtLaunch.Checked;

        private void comboBoxSettingsStreamMode_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Stream mode change
            StreamModeComboBoxItem item = (StreamModeComboBoxItem)this.comboBoxSettingsStreamMode.SelectedItem;

            Config.Instance().StreamMode = item.StreamMode;

            this.UpdateStreamTab();
        }

        private void UpdateStreamTab()
        {
            switch (StreamHandler.GetStreamMode())
            {
                case StreamHandler.STREAM_MODE.TWITCH:
                    {
                        this.labelStreamClientID.Text = "Client ID:";
                        this.textBoxStreamClientID.PasswordChar = '*';

                        this.labelStreamAccessToken.Visible = true;
                        this.textBoxStreamAccessToken.Visible = true;

                        this.SetPollsTabVisible(true);
                        break;
                    }
                case StreamHandler.STREAM_MODE.YOUTUBE:
                    {
                        this.labelStreamClientID.Text = "Livestream ID:";
                        this.textBoxStreamClientID.PasswordChar = (char)0;

                        this.labelStreamAccessToken.Visible = false;
                        this.textBoxStreamAccessToken.Visible = false;

                        this.SetPollsTabVisible(false);
                        break;
                    }
                case StreamHandler.STREAM_MODE.KICK:
                    {
                        this.labelStreamClientID.Text = "Kick channel:";
                        this.textBoxStreamClientID.PasswordChar = (char)0;

                        this.labelStreamAccessToken.Visible = false;
                        this.textBoxStreamAccessToken.Visible = false;

                        this.SetPollsTabVisible(false);
                        break;
                    }
            }
        }


        // -------------------- MultiStream configuration UI --------------------

        private void InitializeMultiStreamAndDebugTabs()
        {
            this.EnsureMultiStreamProfiles();
            this.CreateMultiStreamSettingsTab();
            this.CreateDebugSettingsTab();

            DebugChatCommandHandler.CommandReceived += this.OnDebugChatCommandReceived;
        }

        private void EnsureMultiStreamProfiles()
        {
            if (Config.Instance().MultiStreamSources == null)
                Config.Instance().MultiStreamSources = new List<Config.MultiStreamSourceConfig>();

            while (Config.Instance().MultiStreamSources.Count < 3)
                Config.Instance().MultiStreamSources.Add(new Config.MultiStreamSourceConfig());

            for (int i = 0; i < Config.Instance().MultiStreamSources.Count; i++)
            {
                if (Config.Instance().MultiStreamSources[i] == null)
                    Config.Instance().MultiStreamSources[i] = new Config.MultiStreamSourceConfig();
            }

            if (Config.Instance().MultiStreamSources.Count > 3)
                Config.Instance().MultiStreamSources = Config.Instance().MultiStreamSources.Take(3).ToList();
        }

        private void CreateMultiStreamSettingsTab()
        {
            this.tabMultiStreamSettings = new TabPage("MultiStream");
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(12)
            };

            this.checkBoxMultiStreamEnabled = new CheckBox
            {
                AutoSize = true,
                Text = "Activar configuración MultiStream (hasta 3 fuentes)",
                Checked = Config.Instance().MultiStreamEnabled,
                Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Bold),
                Margin = new Padding(3, 3, 3, 10)
            };
            this.checkBoxMultiStreamEnabled.CheckedChanged += (s, e) =>
            {
                Config.Instance().MultiStreamEnabled = this.checkBoxMultiStreamEnabled.Checked;
                this.UpdateMultiStreamStatus();
                this.UpdateStreamConnectButtonState();
            };
            panel.Controls.Add(this.checkBoxMultiStreamEnabled);

            var explanation = new Label
            {
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(650, 0),
                Text = "Configurá hasta tres fuentes. Cada una puede habilitarse para el chat y/o para las votaciones. Los cambios se guardan en config.cfg al cerrar la GUI.",
                Margin = new Padding(3, 0, 3, 10)
            };
            panel.Controls.Add(explanation);

            for (int i = 0; i < 3; i++)
            {
                Config.MultiStreamSourceConfig config = Config.Instance().MultiStreamSources[i];
                var controls = new MultiStreamSourceControls();
                var group = new GroupBox
                {
                    Text = "Fuente " + (i + 1),
                    Width = 650,
                    Height = 190,
                    Padding = new Padding(8),
                    Margin = new Padding(3, 3, 3, 8)
                };
                var contents = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    AutoScroll = true,
                    Padding = new Padding(4)
                };

                var topRow = new FlowLayoutPanel
                {
                    Width = 615,
                    Height = 30,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false
                };
                controls.Enabled = new CheckBox { Text = "Usar", AutoSize = true, Checked = config.Enabled, Margin = new Padding(3, 5, 12, 3) };
                controls.Platform = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 115, Margin = new Padding(3) };
                controls.Platform.Items.AddRange(new object[] { "Twitch", "YouTube", "Kick" });
                string platform = string.IsNullOrWhiteSpace(config.Platform) ? "Twitch" : config.Platform;
                controls.Platform.SelectedItem = controls.Platform.Items.Contains(platform) ? platform : "Twitch";
                controls.UseForChat = new CheckBox { Text = "Chat", AutoSize = true, Checked = config.UseForChat, Margin = new Padding(12, 5, 6, 3) };
                controls.UseForVoting = new CheckBox { Text = "Votaciones", AutoSize = true, Checked = config.UseForVoting, Margin = new Padding(6, 5, 3, 3) };
                topRow.Controls.Add(controls.Enabled);
                topRow.Controls.Add(controls.Platform);
                topRow.Controls.Add(controls.UseForChat);
                topRow.Controls.Add(controls.UseForVoting);
                contents.Controls.Add(topRow);

                controls.Channel = new TextBox { Width = 490, Text = config.Channel ?? "", Margin = new Padding(3) };
                controls.ClientID = new TextBox { Width = 490, Text = config.ClientID ?? "", Margin = new Padding(3) };
                controls.AccessToken = new TextBox { Width = 490, Text = config.AccessToken ?? "", PasswordChar = '*', Margin = new Padding(3) };

                contents.Controls.Add(this.CreateMultiStreamTextRow("Canal / ID:", controls.Channel));
                contents.Controls.Add(this.CreateMultiStreamTextRow("Client ID:", controls.ClientID));
                contents.Controls.Add(this.CreateMultiStreamTextRow("Token:", controls.AccessToken));

                group.Controls.Add(contents);
                panel.Controls.Add(group);
                this.multiStreamSourceControls.Add(controls);

                int sourceIndex = i;
                controls.Enabled.CheckedChanged += (s, e) => this.UpdateMultiStreamSourceConfig(sourceIndex);
                controls.Platform.SelectedIndexChanged += (s, e) => this.UpdateMultiStreamSourceConfig(sourceIndex);
                controls.UseForChat.CheckedChanged += (s, e) => this.UpdateMultiStreamSourceConfig(sourceIndex);
                controls.UseForVoting.CheckedChanged += (s, e) => this.UpdateMultiStreamSourceConfig(sourceIndex);
                controls.Channel.TextChanged += (s, e) => this.UpdateMultiStreamSourceConfig(sourceIndex);
                controls.ClientID.TextChanged += (s, e) => this.UpdateMultiStreamSourceConfig(sourceIndex);
                controls.AccessToken.TextChanged += (s, e) => this.UpdateMultiStreamSourceConfig(sourceIndex);
            }

            this.labelMultiStreamStatus = new Label
            {
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(650, 0),
                Text = "MultiStream guarda aquí la configuración de las fuentes.",
                Margin = new Padding(3, 4, 3, 12)
            };
            panel.Controls.Add(this.labelMultiStreamStatus);

            var saveButton = new Button
            {
                Text = "Guardar configuración ahora",
                AutoSize = true,
                Margin = new Padding(3, 3, 3, 12)
            };
            saveButton.Click += (s, e) =>
            {
                this.SaveConfig();
                this.labelMultiStreamStatus.Text = "Configuración guardada en config.cfg.";
            };
            panel.Controls.Add(saveButton);

            this.tabMultiStreamSettings.Controls.Add(panel);
            this.tabs.TabPages.Add(this.tabMultiStreamSettings);
            this.UpdateMultiStreamStatus();
        }

        private Control CreateMultiStreamTextRow(string label, TextBox textBox)
        {
            var row = new FlowLayoutPanel
            {
                Width = 615,
                Height = 31,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0)
            };
            row.Controls.Add(new Label
            {
                Text = label,
                Width = 105,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Margin = new Padding(3, 5, 3, 3)
            });
            row.Controls.Add(textBox);
            return row;
        }

        private void UpdateMultiStreamSourceConfig(int index)
        {
            if (index < 0 || index >= this.multiStreamSourceControls.Count ||
                index >= Config.Instance().MultiStreamSources.Count)
                return;

            MultiStreamSourceControls controls = this.multiStreamSourceControls[index];
            Config.MultiStreamSourceConfig config = Config.Instance().MultiStreamSources[index];
            config.Enabled = controls.Enabled.Checked;
            config.Platform = Convert.ToString(controls.Platform.SelectedItem) ?? "Twitch";
            config.Channel = controls.Channel.Text.Trim();
            config.ClientID = controls.ClientID.Text.Trim();
            config.AccessToken = controls.AccessToken.Text;
            config.UseForChat = controls.UseForChat.Checked;
            config.UseForVoting = controls.UseForVoting.Checked;
            this.UpdateMultiStreamStatus();
            this.UpdateStreamConnectButtonState();
        }

        private void UpdateMultiStreamStatus()
        {
            if (this.labelMultiStreamStatus == null)
                return;

            int enabled = Config.Instance().MultiStreamSources == null
                ? 0
                : Config.Instance().MultiStreamSources.Count(source => source != null && source.Enabled);
            this.labelMultiStreamStatus.Text = !Config.Instance().MultiStreamEnabled
                ? "MultiStream está desactivado. Los perfiles quedan guardados, pero no se conectan."
                : enabled == 0
                    ? "Activaste MultiStream, pero todavía no hay fuentes habilitadas."
                    : enabled + " fuente(s) configurada(s). La conexión simultánea se integrará en la siguiente etapa; por ahora sigue disponible el conector individual actual.";
        }

        // -------------------- Debug chat commands --------------------

        private void CreateDebugSettingsTab()
        {
            this.tabDebugSettings = new TabPage("Debug");
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(16)
            };
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Bold),
                Text = "Comandos de depuración por chat",
                Margin = new Padding(3, 3, 3, 12)
            });
            this.checkBoxDebugChatCommands = new CheckBox
            {
                AutoSize = true,
                Text = "Activar !chaos <id_del_efecto>",
                Checked = Config.Instance().DebugChatCommandsEnabled,
                Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Bold),
                Margin = new Padding(3, 3, 3, 10)
            };
            this.checkBoxDebugChatCommands.CheckedChanged += this.CheckBoxDebugChatCommands_CheckedChanged;
            panel.Controls.Add(this.checkBoxDebugChatCommands);
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(650, 0),
                Text = "Con esta opción activa, el contador automático se congela. En el chat conectado escribí !chaos seguido del ID exacto del efecto, por ejemplo: !chaos effect_blackout. Los comandos no se cuentan como votos. Desactivá la opción para reanudar el temporizador.",
                Margin = new Padding(3, 3, 3, 12)
            });
            this.labelDebugChatStatus = new Label
            {
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(650, 0),
                Text = Config.Instance().DebugChatCommandsEnabled
                    ? "Modo de depuración activo: el temporizador está pausado."
                    : "Modo de depuración desactivado.",
                Margin = new Padding(3, 3, 3, 12)
            };
            panel.Controls.Add(this.labelDebugChatStatus);
            this.tabDebugSettings.Controls.Add(panel);
            this.tabs.TabPages.Add(this.tabDebugSettings);
        }

        private void CheckBoxDebugChatCommands_CheckedChanged(object sender, EventArgs e)
        {
            bool enabled = this.checkBoxDebugChatCommands.Checked;
            Config.Instance().DebugChatCommandsEnabled = enabled;

            if (enabled)
            {
                this.stopwatch.Stop();
                this.labelDebugChatStatus.Text = "Modo de depuración activo: el temporizador está pausado.";
            }
            else
            {
                if (Shared.TimerEnabled)
                    this.stopwatch.Start();
                this.labelDebugChatStatus.Text = "Modo de depuración desactivado.";
            }
        }

        private void OnDebugChatCommandReceived(object sender, DebugChatCommandEventArgs e)
        {
            if (this.IsDisposed || !this.IsHandleCreated)
                return;

            try
            {
                this.BeginInvoke(new Action(() => this.RunDebugChatCommand(e)));
            }
            catch (InvalidOperationException)
            {
                // The window may close while a platform callback is arriving.
            }
        }

        private void RunDebugChatCommand(DebugChatCommandEventArgs args)
        {
            if (this.IsDisposed || !Config.Instance().DebugChatCommandsEnabled)
                return;

            if (!args.HasEffectId)
            {
                this.labelDebugChatStatus.Text = "Formato incorrecto. Usá !chaos <id_del_efecto>.";
                return;
            }

            AbstractEffect effect = EffectDatabase.GetByID(args.EffectId);
            if (effect == null && !args.EffectId.StartsWith("effect_", StringComparison.OrdinalIgnoreCase))
                effect = EffectDatabase.GetByID("effect_" + args.EffectId);

            if (effect == null)
            {
                this.labelDebugChatStatus.Text = "ID no encontrado: " + args.EffectId;
                return;
            }

            bool previousCooldownState = EffectDatabase.ShouldCooldown;
            try
            {
                EffectDatabase.ShouldCooldown = false;
                effect.ResetSubtext();
                effect = EffectDatabase.RunEffect(effect);
                if (effect != null)
                    this.AddEffectToListBox(effect);

                this.labelDebugChatStatus.Text = effect == null
                    ? "No se pudo ejecutar el efecto solicitado."
                    : "Ejecutado: " + effect.GetDisplayName(DisplayNameType.UI) +
                      " (comando de " + args.Username + " en " + args.Platform + ").";
            }
            catch (Exception ex)
            {
                this.labelDebugChatStatus.Text = "Error al ejecutar " + args.EffectId + ": " + ex.GetBaseException().Message;
            }
            finally
            {
                EffectDatabase.ShouldCooldown = previousCooldownState;
            }
        }

        private void buttonExperimentalClearActiveEffects_Click(object sender, EventArgs e)
        {
            AbstractEffect effect = EffectDatabase.GetByID("clear_active_effects");

            if (effect != null)
            {
                EffectDatabase.ShouldCooldown = false;
                this.CallEffect(effect);
                EffectDatabase.ShouldCooldown = true;
                return;
            }

            int duration = Config.GetEffectDuration();
            WebsocketHandler.INSTANCE.SendEffectToGame(this.textBoxExperimentalEffectName.Text, new
            {
                seed = RandomHandler.Next(9999999)
            }, duration);
        }
    }
}
