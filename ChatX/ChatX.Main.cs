using HarmonyLib;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Nessie.ATLYSS.EasySettings;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;
using static ChatBehaviour;
using System.Reflection;
using System.Text;
using System;
using System.Globalization;
namespace ChatX
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public partial class ChatX : BaseUnityPlugin
    {
        // -------------------------
        // Plugin bootstrap & config
        // -------------------------
        internal static ConfigEntry<float> chatOpacity;
        internal static ConfigEntry<bool> blockChat;
        internal static ConfigEntry<bool> blockGameFeed;
        internal static ConfigEntry<bool> chatPrefix;
        internal static ConfigEntry<bool> chatTimestamp;
        internal static ConfigEntry<bool> timestampFormat;
        internal static ConfigEntry<bool> asteriskItalic;
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
        internal static AudioClip _mentionAudioClip;
        internal static ManualLogSource Log;
        private static bool _chatHidden; // Tracks whether the chat is hidden via the Home toggle.
        private static bool _settingsRegistered;
        private const string SettingsTabName = "Chat";
        private static readonly MethodInfo GetOrAddCustomTabMethod = typeof(Settings).GetMethod("GetOrAddCustomTab", BindingFlags.Public | BindingFlags.Static, null, [typeof(string)], null);
        private static readonly MethodInfo GetMaxMessageLengthMethod = AccessTools.Method(typeof(ChatX), nameof(GetMaxMessageLength));
        private static readonly MethodInfo StringLengthGetter = AccessTools.PropertyGetter(typeof(string), nameof(string.Length));
        private static bool _loggedMissingCustomTabApi;
        private static bool _loggedMissingChatInputField;
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
        private sealed class ChatInputHandle
        {
            public readonly InputField Ui;
            public readonly TMP_InputField Tmp;
            public readonly RectTransform RectTransform;

            public ChatInputHandle(InputField ui, TMP_InputField tmp)
            {
                Ui = ui;
                Tmp = tmp;
                var component = ui != null ? (Component)ui : tmp;
                RectTransform = component != null ? component.GetComponent<RectTransform>() : null;
            }

            public static ChatInputHandle From(object value)
            {
                if (value is InputField ui) return new ChatInputHandle(ui, null);
                if (value is TMP_InputField tmp) return new ChatInputHandle(null, tmp);
                return null;
            }

            public Component Component => Ui != null ? Ui : Tmp;
            public bool IsFocused => Ui != null ? Ui.isFocused : Tmp != null && Tmp.isFocused;
            public string Text => Ui != null ? Ui.text : Tmp != null ? Tmp.text : string.Empty;
            public int CharacterLimit
            {
                get => Ui != null ? Ui.characterLimit : Tmp != null ? Tmp.characterLimit : 0;
                set
                {
                    if (Ui != null) Ui.characterLimit = value;
                    else if (Tmp != null) Tmp.characterLimit = value;
                }
            }

            public void Deactivate()
            {
                if (Ui != null) Ui.DeactivateInputField();
                else if (Tmp != null) Tmp.DeactivateInputField();
            }

            public void Activate()
            {
                if (Ui != null)
                {
                    Ui.Select();
                    Ui.ActivateInputField();
                }
                else if (Tmp != null)
                {
                    Tmp.Select();
                    Tmp.ActivateInputField();
                }
            }

            public void SetText(string value)
            {
                if (Ui != null) Ui.text = value ?? string.Empty;
                else if (Tmp != null) Tmp.text = value ?? string.Empty;
            }

            public void AddListeners(UnityAction<string> onValueChanged, UnityAction<string> onEndEdit)
            {
                if (Ui != null)
                {
                    Ui.onValueChanged.AddListener(onValueChanged);
                    Ui.onEndEdit.AddListener(onEndEdit);
                }
                else if (Tmp != null)
                {
                    Tmp.onValueChanged.AddListener(onValueChanged);
                    Tmp.onEndEdit.AddListener(onEndEdit);
                }
            }

            public void RemoveListeners(UnityAction<string> onValueChanged, UnityAction<string> onEndEdit)
            {
                if (Ui != null)
                {
                    Ui.onValueChanged.RemoveListener(onValueChanged);
                    Ui.onEndEdit.RemoveListener(onEndEdit);
                }
                else if (Tmp != null)
                {
                    Tmp.onValueChanged.RemoveListener(onValueChanged);
                    Tmp.onEndEdit.RemoveListener(onEndEdit);
                }
            }
        }

        private static class ChatInputResolver
        {
            private static FieldInfo _chatInputField;
            private static bool _chatInputFieldResolved;

            internal static ChatInputHandle TryGet(ChatBehaviourAssets assets)
            {
                if (!assets) return null;
                var field = ResolveChatInputField();
                if (field == null) return null;
                return ChatInputHandle.From(field.GetValue(assets));
            }

            internal static ChatInputHandle TryGetCurrent()
            {
                return TryGet(Player._mainPlayer?._chatBehaviour?._chatAssets)
                    ?? TryGet(ChatBehaviourAssets._current);
            }

            private static FieldInfo ResolveChatInputField()
            {
                if (_chatInputFieldResolved) return _chatInputField;
                _chatInputFieldResolved = true;

                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                _chatInputField = typeof(ChatBehaviourAssets).GetField("_chatInput", flags);
                if (_chatInputField != null && !IsSupportedInputType(_chatInputField.FieldType))
                    _chatInputField = null;

                if (_chatInputField == null && !_loggedMissingChatInputField)
                {
                    _loggedMissingChatInputField = true;
                    Log?.LogWarning("ChatX could not resolve ChatBehaviourAssets._chatInput. Chat input-dependent features will stay disabled.");
                }

                return _chatInputField;
            }

            private static bool IsSupportedInputType(Type type)
            {
                return typeof(InputField).IsAssignableFrom(type)
                    || typeof(TMP_InputField).IsAssignableFrom(type);
            }

        }

        private static bool IsChatSubmitKeyPressed()
        {
            return Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
        }

        private static Transform FindDescendantTransformByName(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name))
                return null;

            var queue = new Queue<Transform>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current == null)
                    continue;
                if (current.name == name)
                    return current;
                for (int i = 0; i < current.childCount; i++)
                    queue.Enqueue(current.GetChild(i));
            }

            return null;
        }

        private static T FindDescendantComponentByName<T>(Transform root, string name) where T : Component
        {
            var transform = FindDescendantTransformByName(root, name);
            return transform != null ? transform.GetComponent<T>() : null;
        }

        private void Awake()
        {
            // Wire up config, Harmony patches, and settings menu entries before the game finishes loading.
            InitConfig();
            Log = Logger;
            ResetOutgoingChatRuntimeState();
            var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            harmony.PatchAll();
            Settings.OnInitialized.AddListener(AddSettings);
            Settings.OnApplySettings.AddListener(OnSettingsApplied);
            Logger.LogInfo($"{MyPluginInfo.PLUGIN_GUID} loaded!");
        }

        private void OnDestroy()
        {
            Settings.OnInitialized.RemoveListener(AddSettings);
            Settings.OnApplySettings.RemoveListener(OnSettingsApplied);

            if (chatTallWindow != null) chatTallWindow.SettingChanged -= OnChatTallWindowChanged;
            if (transparentScrollbar != null) transparentScrollbar.SettingChanged -= OnTransparentScrollbarChanged;
            if (mentionPing != null) mentionPing.SettingChanged -= OnMentionPingChanged;
            if (mentionPingClipEnum != null) mentionPingClipEnum.SettingChanged -= OnMentionPingClipChanged;
            if (mentionPingVolume != null) mentionPingVolume.SettingChanged -= OnMentionPingVolumeChanged;

            _settingsRegistered = false;
            _chatHidden = false;
            _loggedMissingCustomTabApi = false;
            _loggedMissingChatInputField = false;
            ResetUiRuntimeState();
            ResetMentionRuntimeState();
            ResetOutgoingChatRuntimeState();
            ResetLinkPromptRuntimeState();
            ChatInputCharacterLimiter.ResetCounter();
        }

        private void Update()
        {
            RefreshOutgoingChatRuntimeState();
            if (PollLinkPromptHotkeys())
                return;
            TryHandleHotkeys();
        }
        private static string ClipFromEnum(MentionClip e) => ClipNames[(int)e];
        private static MentionClip EnumFromClip(string s)
        {
            int i = Array.IndexOf(ClipNames, s);
            return i >= 0 ? (MentionClip)i : MentionClip.LexiconBell;
        }
        private void InitConfig()
        {
            // local binder to avoid repeating the same ceremony
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
            // one-time migration from old string to enum at startup
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

        private static void OnMentionPingClipChanged(object _, EventArgs __)
        {
            var desired = ClipFromEnum(mentionPingClipEnum.Value);
            if (mentionPingClip.Value != desired)
                mentionPingClip.Value = desired;

            ReloadMentionClip();
            TryPlayMentionPingThrottled(0.5f);
        }

        private static void OnMentionPingVolumeChanged(object _, EventArgs __) => TryPlayMentionPingThrottled(1f);

        private static IEnumerable<CodeInstruction> ReplaceMethodCalls(IEnumerable<CodeInstruction> instructions, MethodInfo source, MethodInfo target, int expectedMatches, string patchName)
        {
            var list = new List<CodeInstruction>(instructions);
            if (source == null || target == null)
            {
                Log?.LogWarning($"ChatX skipped {patchName}; required method references were missing.");
                return list;
            }

            var matches = new List<int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Calls(source))
                    matches.Add(i);
            }

            if (matches.Count != expectedMatches)
            {
                Log?.LogWarning($"ChatX skipped {patchName}; expected {expectedMatches} call(s) to {source.Name}, found {matches.Count}.");
                return list;
            }

            foreach (var index in matches)
            {
                list[index].opcode = OpCodes.Call;
                list[index].operand = target;
            }

            return list;
        }

        private static IEnumerable<CodeInstruction> ReplaceMaxLengthGuards(IEnumerable<CodeInstruction> instructions, int expectedMatches, string patchName)
        {
            var list = new List<CodeInstruction>(instructions);
            if (GetMaxMessageLengthMethod == null || StringLengthGetter == null)
            {
                Log?.LogWarning($"ChatX skipped {patchName}; max-length reflection targets were missing.");
                return list;
            }

            var matches = new List<int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (!IsIntegerLoad(list[i], 125))
                    continue;

                int previous = FindPreviousMeaningfulInstruction(list, i - 1);
                if (previous >= 0 && list[previous].Calls(StringLengthGetter))
                    matches.Add(i);
            }

            if (matches.Count != expectedMatches)
            {
                Log?.LogWarning($"ChatX skipped {patchName}; expected {expectedMatches} length guard(s), found {matches.Count}.");
                return list;
            }

            foreach (var index in matches)
            {
                list[index].opcode = OpCodes.Call;
                list[index].operand = GetMaxMessageLengthMethod;
            }

            return list;
        }

        private static bool IsIntegerLoad(CodeInstruction instruction, int value)
        {
            return (instruction.opcode == OpCodes.Ldc_I4_S && instruction.operand is sbyte shortValue && shortValue == value)
                || (instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is int intValue && intValue == value);
        }

        private static int FindPreviousMeaningfulInstruction(IList<CodeInstruction> instructions, int startIndex)
        {
            for (int i = startIndex; i >= 0; i--)
            {
                if (instructions[i].opcode != OpCodes.Nop)
                    return i;
            }

            return -1;
        }

        public static int GetMaxMessageLength() => messageLimit?.Value == true ? 500 : 125;
        // -------------------------
        // Clear Chat and Game Feed
        // -------------------------
        public static void ClearChatNow()
        {
            var cb = Player._mainPlayer?._chatBehaviour;
            var a = cb?._chatAssets;
            if (!cb || !a) return;
            cb._chatMessages?.Clear();
            if (a._chatText) a._chatText.text = string.Empty;
            ApplyOpacityCap(a);
        }
        public static void ClearGameFeedNow()
        {
            var cb = Player._mainPlayer?._chatBehaviour;
            var a = cb?._chatAssets;
            if (!cb || !a) return;
            cb._gameLogicMessages?.Clear();
            if (a._gameLogicText) a._gameLogicText.text = string.Empty;
            ApplyOpacityCap(a);
        }
        public static void ClearAllNow()
        {
            ClearChatNow();
            ClearGameFeedNow();
        }
        static void TryHandleHotkeys()
        {
            // Listen for global clear/toggle shortcuts while keeping out of the user's way when typing.
            var player = Player._mainPlayer;
            if (!player) return;
            var input = ChatInputResolver.TryGetCurrent();
            bool inputFocused = input?.IsFocused == true;
            var clearKey = clearChatKey?.Value ?? KeyCode.None;
            if (clearKey != KeyCode.None && !inputFocused && Input.GetKeyDown(clearKey))
                ClearAllNow();
            var toggleKey = toggleChatVisibilityKey?.Value ?? KeyCode.None;
            if (toggleKey != KeyCode.None && !inputFocused && Input.GetKeyDown(toggleKey))
            {
                _chatHidden = !_chatHidden;
                var assets = player?._chatBehaviour?._chatAssets ?? ChatBehaviourAssets._current;
                ApplyOpacityCap(assets, force: true);
            }
        }
        static bool ShouldPushGameFeed() => pushGameMessage?.Value == true;

        static string StripRichTextTags(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            var sb = new StringBuilder(message.Length);
            bool inTag = false;
            foreach (var ch in message)
            {
                if (ch == '<')
                {
                    inTag = true;
                    continue;
                }
                if (ch == '>' && inTag)
                {
                    inTag = false;
                    continue;
                }
                if (!inTag) sb.Append(ch);
            }
            return sb.ToString();
        }

        internal static void RouteWaypointAttuneMessage(ChatBehaviour chat, string message)
        {
            if (!ShouldPushGameFeed())
            {
                chat?.New_ChatMessage(message);
                return;
            }

            if (!chat || string.IsNullOrEmpty(message)) return;

            PushGameFeedMessage(chat, StripRichTextTags(message));
        }

        internal static void PushGameFeedMessage(ChatBehaviour chat, string message)
        {
            if (!chat || string.IsNullOrEmpty(message)) return;

            if (ShouldPushGameFeed())
                chat.Init_GameLogicMessage(message);
            else
                chat.New_ChatMessage(message);
        }

        internal static void PushTargetedGameFeedMessage(ChatBehaviour chat, string message)
        {
            if (!chat || string.IsNullOrEmpty(message)) return;

            if (ShouldPushGameFeed())
                chat.Target_GameLogicMessage(message);
            else
                chat.Target_RecieveMessage(message);
        }
        // -------------------------
        // Inject prefixes & italics for new messages
        // -------------------------
        [HarmonyPatch(typeof(ChatBehaviour), nameof(ChatBehaviour.New_ChatMessage))]
        // Massage incoming chat text before the game renders it.
        static class ChatBehaviour_New_ChatMessage_PrefixAndBlock
        {
            [HarmonyPrefix, HarmonyPriority(Priority.First)]
            static bool Prefix(ref string __0)
            {
                if (ChatX.blockChat?.Value == true) return false;
                __0 = ChatX.ProtectChatLinks(__0, out var protectedLinks);
                string oocBadge = ChatX.ExtractRenderedOocBadge(ref __0);
                // 1) *italic* formatting (if enabled)
                if (ChatX.asteriskItalic?.Value == true)
                    __0 = ChatX.ApplyAsteriskFormatting(__0);
                // 2) Channel prefix/OOC badge before the speaker header
                string prefix = string.Empty;
                if (ChatX.chatPrefix?.Value == true && !ChatX.LooksPrefixed(__0)
                    && ChatX.TryExtractChatChannel(__0, out var ch))
                {
                    prefix = ChatX.BuildMessagePrefix(ch);
                }
                string combinedPrefix = ChatX.CombinePrefixSegments(prefix, oocBadge);
                if (!string.IsNullOrEmpty(combinedPrefix))
                    __0 = ChatX.InsertPrefix(__0, combinedPrefix);
                // 3) Mentions last (underline + optional ping)
                ChatX.ApplyMentionEffects(ref __0);
                __0 = ChatX.RestoreProtectedChatLinks(__0, protectedLinks);
                return true;
            }
            [HarmonyPostfix, HarmonyPriority(Priority.Last)]
            static void Postfix(ChatBehaviour __instance)
            {
                if (!__instance) return;
                ChatX.EnsureChatLinkUi(__instance._chatAssets);
                ChatX.ApplyOpacityCap(__instance._chatAssets);
            }
        }
        // -------------------------
        // Block + clamp Game Feed messages
        // -------------------------
        [HarmonyPatch(typeof(ChatBehaviour), nameof(ChatBehaviour.Init_GameLogicMessage))]
        static class ChatBehaviour_Init_GameLogicMessage_Block
        {
            // Block before vanilla does anything
            [HarmonyPrefix, HarmonyPriority(Priority.First)]
            static bool Prefix()
            {
                return ChatX.blockGameFeed?.Value != true;
            }
        }
        // Route dungeon key system messages into the game feed
        [HarmonyPatch(typeof(PatternInstanceManager), "On_DungeonKeyChange")]
        static class PatternInstanceManager_OnDungeonKeyChange_Reroute
        {
            static readonly MethodInfo NewChatMessage = AccessTools.Method(typeof(ChatBehaviour), nameof(ChatBehaviour.New_ChatMessage));
            static readonly MethodInfo PushGameFeed = AccessTools.Method(typeof(ChatX), nameof(ChatX.PushGameFeedMessage));

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return ReplaceMethodCalls(instructions, NewChatMessage, PushGameFeed, expectedMatches: 2, patchName: "PatternInstanceManager.On_DungeonKeyChange reroute");
            }
        }

        [HarmonyPatch(typeof(PlayerQuesting), "Accept_Quest")]
        static class PlayerQuesting_AcceptQuest_RerouteObjectivePickup
        {
            static readonly MethodInfo NewChatMessage = AccessTools.Method(typeof(ChatBehaviour), nameof(ChatBehaviour.New_ChatMessage));
            static readonly MethodInfo PushGameFeed = AccessTools.Method(typeof(ChatX), nameof(ChatX.PushGameFeedMessage));

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return ReplaceMethodCalls(instructions, NewChatMessage, PushGameFeed, expectedMatches: 1, patchName: "PlayerQuesting.Accept_Quest reroute");
            }
        }

        [HarmonyPatch(typeof(WorldPortalWaypoint), "OnTriggerEnter")]
        static class WorldPortalWaypoint_OnTriggerEnter_RerouteAttuneChat
        {
            static readonly MethodInfo NewChatMessage = AccessTools.Method(typeof(ChatBehaviour), nameof(ChatBehaviour.New_ChatMessage));
            static readonly MethodInfo RouteAttune = AccessTools.Method(typeof(ChatX), nameof(ChatX.RouteWaypointAttuneMessage));

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return ReplaceMethodCalls(instructions, NewChatMessage, RouteAttune, expectedMatches: 1, patchName: "WorldPortalWaypoint.OnTriggerEnter reroute");
            }
        }

        [HarmonyPatch(typeof(NetTrigger))]
        static class NetTrigger_OnTriggerStay_RerouteDungeonKeyMessage
        {
            static MethodBase TargetMethod() => AccessTools.Method(typeof(NetTrigger), "OnTriggerStay");
            static readonly MethodInfo TargetReceive = AccessTools.Method(typeof(ChatBehaviour), nameof(ChatBehaviour.Target_RecieveMessage));
            static readonly MethodInfo RouteTarget = AccessTools.Method(typeof(ChatX), nameof(ChatX.PushTargetedGameFeedMessage));

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return ReplaceMethodCalls(instructions, TargetReceive, RouteTarget, expectedMatches: 1, patchName: "NetTrigger.OnTriggerStay reroute");
            }
        }

        [HarmonyPatch(typeof(ChatBehaviour), "UserCode_Target_RecieveMessage__String")]
        static class ChatBehaviour_TargetReceiveMessage_RerouteDungeonKey
        {
            private const string DoorLockedMessage = "The door is locked. It requires a Dungeon Key.";

            static bool Prefix(ChatBehaviour __instance, string _message)
            {
                if (!ShouldPushGameFeed()) return true;
                if (!__instance || string.IsNullOrEmpty(_message)) return true;
                if (!string.Equals(_message, DoorLockedMessage, StringComparison.Ordinal)) return true;

                PushGameFeedMessage(__instance, _message);
                return false;
            }
        }

        // -------------------------
        // Override chat opacity
        // -------------------------
        [HarmonyPatch(typeof(ChatBehaviour), nameof(ChatBehaviour.Display_Chat))]
        static class ChatBehaviourDisplayPatch
        {
            static void Postfix(ChatBehaviour __instance)
            {
                if (!__instance) return;
                ApplyOpacityCap(__instance._chatAssets);
            }
        }
        [HarmonyPatch(typeof(ChatBehaviour), "Client_HandleChatboxUpdate")]
        static class ChatBehaviour_CapOpacity_Postfix
        {
            [HarmonyPostfix]
            static void Postfix(ChatBehaviour __instance)
            {
                if (!__instance) return;
                ApplyOpacityCap(__instance._chatAssets);
            }
        }
        [HarmonyPatch(typeof(ChatBehaviour), "Client_HandleChatboxUpdate")]
        static class ChatBehaviour_HandleChatControls_PreOpen
        {
            [HarmonyPrefix, HarmonyPriority(Priority.First)]
            static void Prefix(ChatBehaviour __instance)
            {
                if (!__instance) return;
                if (!__instance.isLocalPlayer || !ReferenceEquals(ChatBehaviour._current, __instance))
                    return;

                if (!IsChatSubmitKeyPressed())
                    return;

                var assets = __instance._chatAssets;
                var input = ChatInputResolver.TryGet(assets);
                bool inputFocused = input?.IsFocused == true;
                var generalGroup = assets ? assets._generalCanvasGroup : null;
                bool chatInteractable = generalGroup && generalGroup.blocksRaycasts && generalGroup.interactable;
                bool chatTransparentAndDisabled = generalGroup && generalGroup.alpha <= 0.001f && !chatInteractable;

                if (__instance._focusedInChat || inputFocused || !chatTransparentAndDisabled)
                    return;

                pendingRestore = true;
                ResetChatFocus();
            }
        }

        // -------------------------
        // Adds a persistent character counter and syncs the game's input limits with our config.
        // -------------------------
        static class ChatInputCharacterLimiter
        {
            private static ChatInputHandle _trackedInput;
            private static Text _charCounter;
            private static TMP_Text _charCounterTMP;
            private static RectTransform _counterRect;
            private const float CounterOffsetX = 405f;
            private const float CounterVerticalSpacing = 38f;
            private const int CounterFontSize = 20;
            private static readonly Color CounterColorStart = new Color32(0x5A, 0xFF, 0x5A, 0xFF);
            private static readonly Color CounterColorEnd = new Color32(0xFF, 0x55, 0x55, 0xFF);
            private static ChatInputHandle TryGetInput() => ChatInputResolver.TryGetCurrent();
            public static void ApplyCharacterLimit()
            {
                var input = TryGetInput();
                if (input == null)
                {
                    ResetCounter();
                    ChatWindowResizer.ApplyAll();
                    return;
                }
                EnsureCounter(input);
                int desired = ChatX.GetMaxMessageLength();
                if (input.CharacterLimit != desired)
                    input.CharacterLimit = desired;
                UpdateCounter(input);
                ChatWindowResizer.ApplyAll();
            }
            public static System.Collections.IEnumerator ApplyWhenReady(ChatBehaviour chat)
            {
                float end = Time.time + 3f;
                while (Time.time < end)
                {
                    var input = ChatInputResolver.TryGet(chat?._chatAssets)
                             ?? ChatInputResolver.TryGet(ChatBehaviourAssets._current);
                    if (input != null)
                    {
                        EnsureCounter(input);
                        int desired = ChatX.GetMaxMessageLength();
                        if (input.CharacterLimit != desired)
                            input.CharacterLimit = desired;
                        UpdateCounter(input);
                        ChatWindowResizer.ApplyAll();
                        yield break;
                    }
                    yield return null;
                }
                ApplyCharacterLimit();
            }
            private static void EnsureCounter(ChatInputHandle input)
            {
                if (input == null || input.Component == null)
                    return;
                bool trackedDead = _trackedInput?.Component == null;
                if (trackedDead || _trackedInput == null || !ReferenceEquals(_trackedInput.Component, input.Component))
                {
                    if (_trackedInput != null && _trackedInput.Component != null)
                        _trackedInput.RemoveListeners(OnInputValueChanged, OnInputEndEdit);
                    _trackedInput = input;
                    _trackedInput.AddListeners(OnInputValueChanged, OnInputEndEdit);
                }
                var inputRect = input.RectTransform;
                RectTransform host = null;
                if (inputRect != null)
                {
                    host = inputRect.parent as RectTransform;
                    if (host != null && (host.GetComponent<Mask>() != null || host.GetComponent<RectMask2D>() != null))
                    {
                        var candidate = host.parent as RectTransform;
                        if (candidate != null)
                            host = candidate;
                    }
                }
                if (host == null)
                    host = inputRect ?? input.Component.transform as RectTransform;
                if (host == null)
                    return;
                if (!host.gameObject.activeInHierarchy)
                    return;

                EnsureCounterComponent(host, input);

                if (_counterRect == null)
                    return;
                if (!ReferenceEquals(_counterRect.parent, host))
                    _counterRect.SetParent(host, false);
                _counterRect.SetAsLastSibling();
                float templateHeight = 0f;
                float templateWidth = 0f;
                if (inputRect != null)
                {
                    templateHeight = Mathf.Abs(inputRect.rect.height);
                    templateWidth = Mathf.Abs(inputRect.rect.width);
                }
                if (templateHeight <= 1f)
                    templateHeight = 24f;
                if (templateWidth <= 1f)
                    templateWidth = 240f;
                _counterRect.anchorMin = new Vector2(1f, 0f);
                _counterRect.anchorMax = new Vector2(1f, 0f);
                _counterRect.pivot = new Vector2(1f, 1f);
                _counterRect.anchoredPosition = new Vector2(CounterOffsetX, -(templateHeight + CounterVerticalSpacing));
                _counterRect.sizeDelta = new Vector2(Mathf.Max(templateWidth * 0.35f, 110f), templateHeight);
                SetCounterText(string.Empty);
                SetCounterActive(false);
            }
            static bool IsLive(Behaviour b) => b && b.isActiveAndEnabled && b.gameObject.activeInHierarchy;
            private static void UpdateCounter(ChatInputHandle input)
            {
                if (input == null || input.Component == null) return;
                EnsureCounter(input);
                if ((_charCounter == null || _charCounter.Equals(null)) &&
                    (_charCounterTMP == null || _charCounterTMP.Equals(null)))
                    return;
                bool show = !string.IsNullOrEmpty(input.Text) && IsLive(input.Component as Behaviour);
                SetCounterActive(show);
                if (!show) { SetCounterText(string.Empty); return; }
                int max = ChatX.GetMaxMessageLength();
                int used = Mathf.Max(0, ChatX.GetPendingOutgoingChatLength(input.Text));
                float t = max > 0 ? Mathf.Clamp01(used / (float)max) : 0f;
                SetCounterColor(Color.Lerp(CounterColorStart, CounterColorEnd, t));
                SetCounterText((max - used).ToString());
            }
            private static void OnInputValueChanged(string _)
            {
                if (_trackedInput != null)
                    UpdateCounter(_trackedInput);
            }
            private static void OnInputEndEdit(string _)
            {
                if (_trackedInput != null)
                    UpdateCounter(_trackedInput);
                else
                    SetCounterActive(false);
            }
            public static void ResetCounter()
            {
                var counterObject = (_charCounter != null && !_charCounter.Equals(null))
                    ? _charCounter.gameObject
                    : (_charCounterTMP != null && !_charCounterTMP.Equals(null) ? _charCounterTMP.gameObject : null);

                if (_trackedInput != null)
                {
                    if (_trackedInput.Component != null)
                        _trackedInput.RemoveListeners(OnInputValueChanged, OnInputEndEdit);
                    _trackedInput = null;
                }
                SetCounterText(string.Empty);
                SetCounterActive(false);
                _charCounter = null;
                _charCounterTMP = null;
                _counterRect = null;

                if (counterObject != null)
                    UnityEngine.Object.Destroy(counterObject);
            }

            private static void EnsureCounterComponent(RectTransform host, ChatInputHandle input)
            {
                bool preferTmp = input.Tmp != null;
                bool counterMissing = (_charCounter == null || _charCounter.Equals(null))
                    && (_charCounterTMP == null || _charCounterTMP.Equals(null));

                if (counterMissing)
                {
                    _charCounter = null;
                    _charCounterTMP = null;
                    _counterRect = null;

                    var existing = host.Find("ChatX_CharCounter");
                    if (existing != null)
                    {
                        var existingTmp = existing.GetComponent<TMP_Text>();
                        if (existingTmp != null && !existingTmp.Equals(null))
                        {
                            _charCounterTMP = existingTmp;
                            _counterRect = existingTmp.rectTransform;
                            SetupTmpCounter(existingTmp, input.Tmp);
                            return;
                        }

                        var existingText = existing.GetComponent<Text>();
                        if (existingText != null && !existingText.Equals(null))
                        {
                            _charCounter = existingText;
                            _counterRect = existingText.rectTransform;
                            SetupUiCounter(existingText, input.Ui);
                            return;
                        }
                    }

                    if (preferTmp)
                        CreateTmpCounter(host, input.Tmp);
                    else
                        CreateUiCounter(host, input.Ui);
                }
                else if (_counterRect == null)
                {
                    if (_charCounter != null && !_charCounter.Equals(null))
                        _counterRect = _charCounter.rectTransform;
                    else if (_charCounterTMP != null && !_charCounterTMP.Equals(null))
                        _counterRect = _charCounterTMP.rectTransform;
                }
            }

            private static void CreateUiCounter(RectTransform host, InputField input)
            {
                var go = new GameObject("ChatX_CharCounter")
                {
                    layer = input != null ? input.gameObject.layer : 0
                };
                _counterRect = go.AddComponent<RectTransform>();
                _counterRect.SetParent(host, false);
                var textCmp = go.AddComponent<Text>();
                SetupUiCounter(textCmp, input);
                _charCounter = textCmp;
            }

            private static void CreateTmpCounter(RectTransform host, TMP_InputField input)
            {
                var go = new GameObject("ChatX_CharCounter")
                {
                    layer = input != null ? input.gameObject.layer : 0
                };
                _counterRect = go.AddComponent<RectTransform>();
                _counterRect.SetParent(host, false);
                var textCmp = go.AddComponent<TextMeshProUGUI>();
                SetupTmpCounter(textCmp, input);
                _charCounterTMP = textCmp;
            }

            private static void SetupUiCounter(Text counter, InputField input)
            {
                if (counter == null) return;
                var template = input != null ? input.textComponent : null;
                if (template != null)
                {
                    counter.font = template.font;
                    counter.fontStyle = template.fontStyle;
                    counter.material = template.material;
                    counter.lineSpacing = template.lineSpacing;
                }
                else
                {
                    counter.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                    counter.lineSpacing = 1f;
                }
                counter.fontSize = CounterFontSize;
                counter.color = CounterColorStart;
                counter.alignment = TextAnchor.MiddleRight;
                counter.horizontalOverflow = HorizontalWrapMode.Overflow;
                counter.verticalOverflow = VerticalWrapMode.Truncate;
                counter.raycastTarget = false;
            }

            private static void SetupTmpCounter(TMP_Text counter, TMP_InputField input)
            {
                if (counter == null) return;
                var template = input != null ? input.textComponent : null;
                if (template != null)
                {
                    counter.font = template.font;
                    counter.fontStyle = template.fontStyle;
                    counter.fontMaterial = template.fontMaterial;
                    counter.lineSpacing = template.lineSpacing;
                }
                else if (TMP_Settings.defaultFontAsset != null)
                {
                    counter.font = TMP_Settings.defaultFontAsset;
                }
                counter.fontSize = CounterFontSize;
                counter.color = CounterColorStart;
                counter.alignment = TextAlignmentOptions.MidlineRight;
                counter.enableWordWrapping = false;
                counter.overflowMode = TextOverflowModes.Overflow;
                counter.raycastTarget = false;
            }

            private static void SetCounterActive(bool active)
            {
                if (_charCounter != null && !_charCounter.Equals(null))
                    _charCounter.gameObject.SetActive(active);
                if (_charCounterTMP != null && !_charCounterTMP.Equals(null))
                    _charCounterTMP.gameObject.SetActive(active);
            }

            private static void SetCounterText(string text)
            {
                if (_charCounter != null && !_charCounter.Equals(null))
                    _charCounter.text = text ?? string.Empty;
                if (_charCounterTMP != null && !_charCounterTMP.Equals(null))
                    _charCounterTMP.text = text ?? string.Empty;
            }

            private static void SetCounterColor(Color color)
            {
                if (_charCounter != null && !_charCounter.Equals(null))
                    _charCounter.color = color;
                if (_charCounterTMP != null && !_charCounterTMP.Equals(null))
                    _charCounterTMP.color = color;
            }
        }
        // -------------------------
        // Lifecycle hooks for limit/resizer
        // -------------------------
        [HarmonyPatch(typeof(SettingsManager), nameof(SettingsManager.Load_SettingsData))]
        static class ChatInputLimit_OnLoad
        {
            static void Postfix()
            {
                ChatInputCharacterLimiter.ApplyCharacterLimit();
                ChatWindowResizer.ApplyAll();
            }
        }
        [HarmonyPatch(typeof(SettingsManager), nameof(SettingsManager.Save_SettingsData))]
        static class ChatInputLimit_OnSave
        {
            static void Postfix()
            {
                ChatInputCharacterLimiter.ApplyCharacterLimit();
                ChatWindowResizer.ApplyAll();
            }
        }
        [HarmonyPatch(typeof(ChatBehaviourAssets), "Start")]
        static class ChatInputLimit_OnChatAssetsStart
        {
            static void Postfix(ChatBehaviourAssets __instance)
            {
                ChatWindowResizer.Register(__instance);
                ChatX.EnsureChatLinkUi(__instance);
                var input = ChatInputResolver.TryGet(__instance);
                if (input != null)
                    input.CharacterLimit = ChatX.GetMaxMessageLength();
            }
        }
        [HarmonyPatch(typeof(ChatBehaviour), nameof(ChatBehaviour.OnStartAuthority))]
        static class ChatBehaviour_OnStartAuthority_Patch
        {
            static void Postfix(ChatBehaviour __instance)
            {
                __instance.StartCoroutine(ChatInputCharacterLimiter.ApplyWhenReady(__instance));
            }
        }
        [HarmonyPatch(typeof(InGameUI), "RefreshParams_LocatePlayer")]
        static class ApplyLimit_AfterPlayerLocated
        {
            static void Postfix()
            {
                ChatInputCharacterLimiter.ApplyCharacterLimit();
                ChatWindowResizer.ApplyAll();
                ChatX.EnsureChatLinkUi(ChatBehaviourAssets._current);
            }
        }
        // -------------------------
        // OOC preprocessing for outgoing chat
        // -------------------------
        [HarmonyPatch(typeof(ChatBehaviour), nameof(ChatBehaviour.Send_ChatMessage))]
        static class ChatBehaviour_SendMessage_Ooc
        {
            [HarmonyPrefix, HarmonyPriority(Priority.First)]
            static bool Prefix(ChatBehaviour __instance, ref string __0, out string __state)
            {
                __state = null;
                if (!ChatX.ShouldInterceptOutgoingChat(__0))
                    return true;

                if (ChatX.TryHandleLocalOocToggle(__instance, __0))
                    return false;

                var status = ChatX.PrepareOutgoingChatMessage(__0, out var transformedMessage, out var errorPrompt);
                if (status == OutgoingChatPreparationStatus.Suppressed)
                {
                    ChatX.FinalizeLocalChatCommand(__instance);
                    return false;
                }

                if (status == OutgoingChatPreparationStatus.Invalid)
                {
                    ChatX.ShowOutgoingChatValidationError(__instance, errorPrompt, __0);
                    return false;
                }

                if (status == OutgoingChatPreparationStatus.Transformed)
                {
                    __state = __0;
                    __0 = transformedMessage;
                }

                return true;
            }

            [HarmonyPostfix]
            static void Postfix(ChatBehaviour __instance, string __state)
            {
                if (!string.IsNullOrEmpty(__state))
                    ChatX.RestoreLastTypedChatMessage(__instance, __state);
            }
        }
        // -------------------------
        // Allow extended messages via Transpiler
        // -------------------------
        [HarmonyPatch(typeof(ChatBehaviour), nameof(ChatBehaviour.Send_ChatMessage))]
        static class ChatBehaviour_SendMessage_Transpiler
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instrs)
            {
                return ReplaceMaxLengthGuards(instrs, expectedMatches: 1, patchName: "ChatBehaviour.Send_ChatMessage max length");
            }
        }
        [HarmonyPatch(typeof(ChatBehaviour), "UserCode_Cmd_SendChatMessage__String__ChatChannel")]
        static class ChatBehaviour_CmdSendMessage_Transpiler
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instrs)
            {
                return ReplaceMaxLengthGuards(instrs, expectedMatches: 1, patchName: "ChatBehaviour.UserCode_Cmd_SendChatMessage__String__ChatChannel max length");
            }
        }
    }
}













