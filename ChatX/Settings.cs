using BepInEx.Configuration;
using Nessie.ATLYSS.EasySettings;
using System;
using System.Reflection;
using UnityEngine;

namespace ChatX
{
    public partial class ChatX
    {
        internal static ConfigEntry<float> chatOpacity;
        internal static ConfigEntry<bool> blockChat;
        internal static ConfigEntry<bool> blockGameFeed;
        internal static ConfigEntry<bool> chatPrefix;
        internal static ConfigEntry<bool> chatTimestamp;
        internal static ConfigEntry<bool> timestampFormat;
        internal static ConfigEntry<bool> asteriskItalic;
        internal static ConfigEntry<bool> oocEnabled;
        internal static ConfigEntry<bool> chatTallWindow;
        internal static ConfigEntry<KeyCode> clearChatKey;
        internal static ConfigEntry<KeyCode> toggleChatVisibilityKey;
        internal static ConfigEntry<bool> messageLimit;
        internal static ConfigEntry<bool> pushGameMessage;
        internal static ConfigEntry<bool> mentionUnderline;
        internal static ConfigEntry<bool> mentionPing;
        internal static ConfigEntry<string> mentionPingClip;
        internal static ConfigEntry<MentionClip> mentionPingClipEnum;
        internal static ConfigEntry<float> mentionPingVolume;
        internal static ConfigEntry<bool> transparentScrollbar;

        private static bool _settingsRegistered;
        private const string SettingsTabName = "Chat";
        private static readonly MethodInfo GetOrAddCustomTabMethod = typeof(Settings).GetMethod("GetOrAddCustomTab", BindingFlags.Public | BindingFlags.Static, null, [typeof(string)], null);
        private static bool _loggedMissingCustomTabApi;

        public enum MentionClip
        {
            LexiconBell,
            UIClick01,
            UIHover,
            Lockout
        }

        private static readonly string[] ClipNames =
        [
            "_lexiconBell",
            "_uiClick01",
            "_uiHover",
            "lockout"
        ];

        private static string ClipFromEnum(MentionClip e) => ClipNames[(int)e];

        private static MentionClip EnumFromClip(string s)
        {
            int i = Array.IndexOf(ClipNames, s);
            return i >= 0 ? (MentionClip)i : MentionClip.LexiconBell;
        }

        private void InitConfig()
        {
            ConfigEntry<T> B<T>(string k, T v, string d, AcceptableValueBase r = null)
                => Config.Bind(new ConfigDefinition("ChatX", k), v,
                               r == null ? new ConfigDescription(d)
                                         : new ConfigDescription(d, r));
            chatOpacity = B("Chat Opacity", 1f, "Sets the desired opacity",
                               new AcceptableValueRange<float>(0f, 1f));
            blockChat = B("Block Chat", false, "Completely blocks chat messages from appearing");
            blockGameFeed = B("Block Game Feed", false, "Completely blocks messages in the Game Feed");
            chatPrefix = B("Chat Prefix", true, "Show a (G)/(P)/(Z) prefix that inherits the channel color");
            chatTimestamp = B("Chat Timestamp", false, "Show a [HH:MM] timestamp before chat prefixes");
            timestampFormat = B("Timestamp 12h/24h", true, "Switch between 12h/24h time format");
            asteriskItalic = B("Asterisk Italic/Bold", true, "Enable *italic* and **bold** chat formatting");
            oocEnabled = B("Enable OOC", true, "Disable ChatX OOC shortcuts and OOC badge handling");
            chatTallWindow = B("Tall Chat Window", false, "Increase chat log height to show more lines");
            clearChatKey = B("Clear Chat", KeyCode.End, "Keybind that clears chat");
            toggleChatVisibilityKey = B("Toggle Chat Visibility", KeyCode.Home, "Keybind that toggles the chat opacity between visible and hidden");
            messageLimit = B("Extend Message Limit", true, "Toggle the max input length between 125 and 500 characters");
            pushGameMessage = B("Push game chat to feed", true, "When enabled, reroute gameplay system messages to the Game Feed instead of chat");
            mentionUnderline = B("Mention Underline", true, "Underlines your name when someone mentions you in chat");
            mentionPing = B("Mention Ping", true, "Play a sound when your name appears in chat");
            mentionPingClip = B("Selected Clip", "_lexiconBell", "Audio clip to use for mention ping");
            mentionPingClipEnum = B("Mention Ping Clip", MentionClip.LexiconBell, "Select the mention ping clip from a list");
            mentionPingVolume = B("Volume", 0.5f, "Volume for the ping sound",
                               new AcceptableValueRange<float>(0f, 1f));
            transparentScrollbar = B("Transparent Scrollbar", false, "Hide the chat scrollbar while keeping it functional.");
            chatTallWindow.SettingChanged += OnChatTallWindowChanged;
            transparentScrollbar.SettingChanged += OnTransparentScrollbarChanged;
            mentionPing.SettingChanged += OnMentionPingChanged;
            mentionPingClipEnum.SettingChanged += OnMentionPingClipChanged;
            oocEnabled.SettingChanged += OnOocEnabledChanged;
            mentionPingClipEnum.Value = EnumFromClip(mentionPingClip.Value);
            mentionPingVolume.SettingChanged += OnMentionPingVolumeChanged;
        }

        private void AddSettings()
        {
            if (_settingsRegistered)
                return;

            _settingsRegistered = true;

            var tab = ResolveSettingsTab();
            void T(string label, ConfigEntry<bool> e) => tab.AddToggle(label, e);
            void S(string label, ConfigEntry<float> e) => tab.AddAdvancedSlider(label, e);
            void D<T>(string label, ConfigEntry<T> e) where T : Enum => tab.AddDropdown(label, e);
            void K(string label, ConfigEntry<KeyCode> e) => tab.AddKeyButton(label, e);
            tab.AddHeader("ChatX Client Settings");
            S("Chat Opacity", chatOpacity);
            T("Transparent Scrollbar", transparentScrollbar);
            T("Chat Prefix", chatPrefix);
            T("Chat Timestamp", chatTimestamp);
            T("Timestamp Format (12h/24h)", timestampFormat);
            T("Asterisk Italic/Bold", asteriskItalic);
            T("Enable OOC", oocEnabled);
            T("Tall Chat Window", chatTallWindow);
            T("Push game chat to feed", pushGameMessage);
            T("Block Chat", blockChat);
            T("Block Game Feed", blockGameFeed);
            K("Clear Chat and Feed", clearChatKey);
            K("Toggle Chat Visibility", toggleChatVisibilityKey);
            tab.AddHeader("Mention Options");
            T("Mention Underline", mentionUnderline);
            T("Mention Ping", mentionPing);
            S("Mention Ping Volume", mentionPingVolume);
            D("Mention Ping Clip", mentionPingClipEnum);
            tab.AddHeader("Both Host + Client");
            T("Message Limit 125/500", messageLimit);
        }

        private static SettingsTab ResolveSettingsTab()
        {
            try
            {
                if (GetOrAddCustomTabMethod?.Invoke(null, [SettingsTabName]) is SettingsTab tab)
                    return tab;
            }
            catch (TargetInvocationException ex)
            {
                Log?.LogWarning($"ChatX failed to create custom settings tab '{SettingsTabName}': {ex.InnerException?.Message ?? ex.Message}");
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"ChatX failed to resolve custom settings tab '{SettingsTabName}': {ex.Message}");
            }

            if (GetOrAddCustomTabMethod == null && !_loggedMissingCustomTabApi)
            {
                _loggedMissingCustomTabApi = true;
                Log?.LogWarning("ChatX did not find EasySettings.GetOrAddCustomTab. Falling back to the default Mods tab.");
            }

            return Settings.ModTab;
        }

        private void OnSettingsApplied()
        {
            Config.Save();
            ChatInputCharacterLimiter.ApplyCharacterLimit();
            ChatWindowResizer.ApplyAll();
        }

        private static void OnChatTallWindowChanged(object _, EventArgs __) => ChatWindowResizer.ApplyAll();
        private static void OnTransparentScrollbarChanged(object _, EventArgs __) => ApplyOpacityCap(ChatBehaviourAssets._current, force: true);
        private static void OnMentionPingChanged(object _, EventArgs __) => ReloadMentionClip();

        private static void OnOocEnabledChanged(object _, EventArgs __)
        {
            if (oocEnabled?.Value != true)
                ResetOutgoingChatRuntimeState();
        }

        private static void OnMentionPingClipChanged(object _, EventArgs __)
        {
            var desired = ClipFromEnum(mentionPingClipEnum.Value);
            if (mentionPingClip.Value != desired)
                mentionPingClip.Value = desired;

            ReloadMentionClip();
            TryPlayMentionPingThrottled(0.5f);
        }

        private static void OnMentionPingVolumeChanged(object _, EventArgs __) => TryPlayMentionPingThrottled(1f);
    }
}
