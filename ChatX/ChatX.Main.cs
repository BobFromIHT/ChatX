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
        internal static ManualLogSource Log;
        private static readonly MethodInfo GetMaxMessageLengthMethod = AccessTools.Method(typeof(ChatX), nameof(GetMaxMessageLength));
        private static readonly MethodInfo StringLengthGetter = AccessTools.PropertyGetter(typeof(string), nameof(string.Length));

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
            if (oocEnabled != null) oocEnabled.SettingChanged -= OnOocEnabledChanged;

            _settingsRegistered = false;
            _chatHidden = false;
            _loggedMissingCustomTabApi = false;
            ResetUiRuntimeState();
            ResetMentionRuntimeState();
            ResetOutgoingChatRuntimeState();
            ResetLinkPromptRuntimeState();
            ChatInputResolver.ResetRuntimeState();
            ChatInputCharacterLimiter.ResetCounter();
        }

        private void Update()
        {
            RefreshOutgoingChatRuntimeState();
            if (PollLinkPromptHotkeys())
                return;

            TryHandleHotkeys();
        }

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
