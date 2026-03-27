using HarmonyLib;
using Mirror;
using System;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ChatX
{
    public partial class ChatX
    {
        private const string OocToken = "[OOC]";
        private const string OocPrefix = OocToken + " ";
        private const string OocToggleCommand = "/ooc";
        private static readonly FieldInfo ChatLastMessageField = AccessTools.Field(typeof(ChatBehaviour), "_lastMessage");
        private static readonly Type ErrorPromptManagerType = typeof(Player).Assembly.GetType("ErrorPromptTextManager");
        private static readonly FieldInfo ErrorPromptCurrentField = AccessTools.Field(ErrorPromptManagerType, "current");
        private static readonly MethodInfo ErrorPromptInitMethod = AccessTools.Method(ErrorPromptManagerType, "Init_ErrorPrompt");

        private static bool _oocModeEnabled;

        private enum OutgoingChatPreparationStatus
        {
            Unchanged,
            Transformed,
            Suppressed,
            Invalid
        }

        internal static void ResetOutgoingChatRuntimeState()
        {
            _oocModeEnabled = false;
        }

        internal static void RefreshOutgoingChatRuntimeState()
        {
            if (_oocModeEnabled && (!NetworkClient.active || !IsOocFeatureEnabled()))
                ResetOutgoingChatRuntimeState();
        }

        internal static bool ShouldInterceptOutgoingChat(string rawMessage)
        {
            return NetworkClient.active
                && IsChatSubmitKeyPressed()
                && !string.IsNullOrWhiteSpace(rawMessage);
        }

        private static bool IsOocFeatureEnabled()
        {
            return oocEnabled?.Value != false;
        }

        internal static bool TryHandleLocalOocToggle(ChatBehaviour chat, string rawMessage)
        {
            if (!IsOocFeatureEnabled() || !chat || !IsLocalOocToggleCommand(rawMessage))
                return false;

            _oocModeEnabled = !_oocModeEnabled;
            FinalizeLocalChatCommand(chat);
            PushLocalChatStatus(chat, _oocModeEnabled ? "OOC chat enabled." : "OOC chat disabled.");
            return true;
        }

        internal static int GetPendingOutgoingChatLength(string rawMessage)
        {
            if (string.IsNullOrWhiteSpace(rawMessage))
                return 0;

            if (IsOocFeatureEnabled() && IsLocalOocToggleCommand(rawMessage))
                return 0;

            var status = PrepareOutgoingChatMessage(rawMessage, out var transformedMessage, out _);
            return status switch
            {
                OutgoingChatPreparationStatus.Suppressed => 0,
                OutgoingChatPreparationStatus.Transformed => transformedMessage.Length,
                OutgoingChatPreparationStatus.Invalid => transformedMessage.Length,
                _ => rawMessage.Length
            };
        }

        private static OutgoingChatPreparationStatus PrepareOutgoingChatMessage(string rawMessage, out string transformedMessage, out string errorPrompt)
        {
            transformedMessage = rawMessage ?? string.Empty;
            errorPrompt = null;

            if (string.IsNullOrWhiteSpace(rawMessage))
                return OutgoingChatPreparationStatus.Unchanged;

            if (!IsOocFeatureEnabled())
                return OutgoingChatPreparationStatus.Unchanged;

            bool oneShotOoc = false;
            string body = rawMessage;
            string trimmedLeft = rawMessage.TrimStart();

            if (trimmedLeft.StartsWith("//", StringComparison.Ordinal))
            {
                oneShotOoc = true;
                int leadingWhitespace = rawMessage.Length - trimmedLeft.Length;
                int bodyStart = leadingWhitespace + 2;
                if (bodyStart < rawMessage.Length && rawMessage[bodyStart] == ' ')
                    bodyStart++;

                body = rawMessage[bodyStart..];
                if (string.IsNullOrWhiteSpace(body))
                {
                    transformedMessage = string.Empty;
                    return OutgoingChatPreparationStatus.Suppressed;
                }
            }

            bool slashCommand = !oneShotOoc && IsSlashCommand(rawMessage);
            bool shouldPrefixOoc = oneShotOoc || (_oocModeEnabled && !slashCommand);
            if (!shouldPrefixOoc)
                return OutgoingChatPreparationStatus.Unchanged;

            transformedMessage = HasOocPrefix(body) ? body : OocPrefix + body;
            int maxLength = GetMaxMessageLength();
            if (transformedMessage.Length > maxLength)
            {
                errorPrompt = $"Message exceeds the {maxLength}-character limit.";
                return OutgoingChatPreparationStatus.Invalid;
            }

            return string.Equals(transformedMessage, rawMessage, StringComparison.Ordinal)
                ? OutgoingChatPreparationStatus.Unchanged
                : OutgoingChatPreparationStatus.Transformed;
        }

        internal static void ShowOutgoingChatValidationError(ChatBehaviour chat, string errorPrompt, string rawMessage)
        {
            ShowErrorPrompt(errorPrompt);
            RestoreChatInput(chat, rawMessage);
        }

        internal static void RestoreLastTypedChatMessage(ChatBehaviour chat, string rawMessage)
        {
            if (!chat || ChatLastMessageField == null)
                return;

            try
            {
                ChatLastMessageField.SetValue(chat, rawMessage ?? string.Empty);
            }
            catch (Exception ex)
            {
                Log?.LogDebug($"ChatX could not restore raw chat history: {ex.Message}");
            }
        }

        private static bool HasOocPrefix(string message)
        {
            return IsOocFeatureEnabled()
                && TryFindLeadingOocToken(message, 0, out _, out _, out _, out _);
        }

        private static bool TryFindLeadingOocToken(string message, int startIndex, out int tokenStart, out int tokenLength, out int trailingSpaceStart, out int trailingSpaceLength)
        {
            tokenStart = 0;
            tokenLength = 0;
            trailingSpaceStart = 0;
            trailingSpaceLength = 0;
            if (string.IsNullOrEmpty(message) || startIndex >= message.Length)
                return false;

            int i = startIndex;
            while (i < message.Length && char.IsWhiteSpace(message[i]))
                i++;

            while (i < message.Length)
            {
                if (!TryReadLeadingVisibleToken(message, i, out var visibleToken, out int visibleStart, out int visibleEnd, out int nextIndex))
                    return false;

                if (!IsLeadingPrefixToken(visibleToken))
                    return false;

                if (string.Equals(visibleToken, OocToken, StringComparison.OrdinalIgnoreCase))
                {
                    tokenStart = visibleStart;
                    tokenLength = visibleEnd - visibleStart;
                    if (nextIndex < message.Length && char.IsWhiteSpace(message[nextIndex]))
                    {
                        trailingSpaceStart = nextIndex;
                        trailingSpaceLength = 1;
                    }

                    return true;
                }

                i = nextIndex;
                while (i < message.Length && char.IsWhiteSpace(message[i]))
                    i++;
            }

            return false;
        }

        private static bool TryReadLeadingVisibleToken(string message, int startIndex, out string visibleToken, out int visibleStart, out int visibleEnd, out int nextIndex)
        {
            visibleToken = string.Empty;
            visibleStart = startIndex;
            visibleEnd = startIndex;
            nextIndex = startIndex;
            if (string.IsNullOrEmpty(message) || startIndex >= message.Length)
                return false;

            var visibleText = new StringBuilder();
            bool inTag = false;
            bool sawVisibleChar = false;
            int i = startIndex;

            while (i < message.Length)
            {
                char c = message[i];
                if (inTag)
                {
                    i++;
                    if (c == '>')
                        inTag = false;

                    continue;
                }

                if (c == '<')
                {
                    inTag = true;
                    i++;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    if (sawVisibleChar)
                        break;

                    i++;
                    continue;
                }

                if (!sawVisibleChar)
                    visibleStart = i;

                sawVisibleChar = true;
                visibleText.Append(c);
                i++;
                visibleEnd = i;
            }

            nextIndex = i;
            if (!sawVisibleChar)
                return false;

            visibleToken = visibleText.ToString().Trim();
            return visibleToken.Length > 0;
        }

        private static bool IsLeadingPrefixToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            token = token.Trim();
            if (token.Length < 3 || token.Length > 24)
                return false;

            for (int i = 0; i < token.Length; i++)
            {
                if (char.IsWhiteSpace(token[i]))
                    return false;
            }

            return (token[0] == '[' && token[^1] == ']')
                || (token[0] == '(' && token[^1] == ')');
        }

        private static bool IsLocalOocToggleCommand(string message)
        {
            return string.Equals(message?.Trim(), OocToggleCommand, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSlashCommand(string message)
        {
            return !string.IsNullOrWhiteSpace(message)
                && message.TrimStart().StartsWith("/", StringComparison.Ordinal);
        }

        private static void FinalizeLocalChatCommand(ChatBehaviour chat)
        {
            if (!chat)
                return;

            chat._focusedInChat = false;
            SetChatInputBuffer(chat, 0.25f);
            EventSystem.current?.SetSelectedGameObject(null);

            var input = ChatInputResolver.TryGet(chat._chatAssets);
            if (input != null)
            {
                input.SetText(string.Empty);
                input.Deactivate();
            }

            chat._textFadeTimer = Mathf.Max(chat._textFadeTimer, 1f);
            chat._logicFadeTimer = Mathf.Max(chat._logicFadeTimer, 1f);
            ApplyOpacityCap(chat._chatAssets, force: true);
        }

        private static void RestoreChatInput(ChatBehaviour chat, string rawMessage)
        {
            if (!chat)
                return;

            chat._focusedInChat = true;
            chat.Display_Chat(true);
            chat._textFadeTimer = Mathf.Max(chat._textFadeTimer, 1f);
            chat._logicFadeTimer = Mathf.Max(chat._logicFadeTimer, 1f);

            var input = ChatInputResolver.TryGet(chat._chatAssets);
            if (input != null)
            {
                input.SetText(rawMessage ?? string.Empty);
                EventSystem.current?.SetSelectedGameObject(input.Component.gameObject);
                input.Activate();
            }

            ApplyOpacityCap(chat._chatAssets, force: true);
        }

        private static void SetChatInputBuffer(ChatBehaviour chat, float value)
        {
            if (!chat || ChatInputBufferField == null)
                return;

            try
            {
                ChatInputBufferField.SetValue(chat, value);
            }
            catch (Exception ex)
            {
                Log?.LogDebug($"ChatX could not update chat input buffer: {ex.Message}");
            }
        }

        private static void PushLocalChatStatus(ChatBehaviour chat, string message)
        {
            if (!chat || string.IsNullOrWhiteSpace(message))
                return;

            chat.New_ChatMessage($"<color=#A7FC00>{message}</color>");
        }

        private static void ShowErrorPrompt(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            try
            {
                object current = ErrorPromptCurrentField?.GetValue(null);
                if (current != null && ErrorPromptInitMethod != null)
                {
                    ErrorPromptInitMethod.Invoke(current, [message]);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log?.LogDebug($"ChatX could not show error prompt: {ex.Message}");
            }

            var chat = ChatBehaviour._current;
            if (chat != null)
                chat.New_ChatMessage($"<color=#FF8080>{message}</color>");
        }
    }
}
