using HarmonyLib;
using Mirror;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ChatX
{
    public partial class ChatX
    {
        private const string OocPrefix = "[OOC] ";
        private const string OocToggleCommand = "/ooc";
        private static readonly Regex UrlRegex = new(@"\b(?:https?://|www\.)[^\s<]+", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly FieldInfo ChatLastMessageField = AccessTools.Field(typeof(ChatBehaviour), "_lastMessage");
        private static readonly Type ErrorPromptManagerType = typeof(Player).Assembly.GetType("ErrorPromptTextManager");
        private static readonly FieldInfo ErrorPromptCurrentField = AccessTools.Field(ErrorPromptManagerType, "current");
        private static readonly MethodInfo ErrorPromptInitMethod = AccessTools.Method(ErrorPromptManagerType, "Init_ErrorPrompt");
        private static readonly Type PartyUiManagerType = typeof(Player).Assembly.GetType("PartyUIManager");
        private static readonly FieldInfo PartyUiCurrentField = AccessTools.Field(PartyUiManagerType, "_current");
        private static readonly FieldInfo PartyInviteElementField = AccessTools.Field(PartyUiManagerType, "_partyInviteElement");
        private static readonly FieldInfo PartyAcceptInviteButtonField = AccessTools.Field(PartyUiManagerType, "_acceptInviteButton");
        private static readonly FieldInfo PartyDeclineInviteButtonField = AccessTools.Field(PartyUiManagerType, "_declineInviteButton");
        private static readonly FieldInfo PartyInvitePromptField = AccessTools.Field(PartyUiManagerType, "_invitedByPrompt");
        private static readonly FieldInfo PartyPanelGroupField = AccessTools.Field(PartyUiManagerType, "_partyPanelGroup");
        private static readonly FieldInfo MenuElementRectField = AccessTools.Field(typeof(MenuElement), "menuRect");
        private static readonly FieldInfo MenuElementCanvasGroupField = AccessTools.Field(typeof(MenuElement), "_canvasGroup");

        private static bool _oocModeEnabled;
        private static GameObject _chatLinkPromptRoot;
        private static MenuElement _chatLinkPromptElement;
        private static CanvasGroup _chatLinkPromptCanvasGroup;
        private static TextMeshProUGUI _chatLinkPromptText;
        private static Text _chatLinkPromptTextLegacy;
        private static Button _chatLinkOpenButton;
        private static Button _chatLinkCancelButton;
        private static string _pendingChatLinkUrl;
        private static bool _chatLinkPromptOpen;
        private const string ChatLinkOverlayName = "ChatX_LinkClickOverlay";

        private enum OutgoingChatPreparationStatus
        {
            Unchanged,
            Transformed,
            Suppressed,
            Invalid
        }

        private enum PromptButtonIconKind
        {
            Confirm,
            Cancel
        }

        private sealed class ProtectedChatLink
        {
            public string Token;
            public string DisplayText;
            public string CanonicalUrl;
        }

        internal static void ResetOutgoingChatRuntimeState()
        {
            _oocModeEnabled = false;
        }

        internal static void ResetLinkPromptRuntimeState()
        {
            _pendingChatLinkUrl = null;
            _chatLinkPromptOpen = false;
            _chatLinkPromptElement = null;
            _chatLinkPromptText = null;
            _chatLinkPromptTextLegacy = null;
            _chatLinkOpenButton = null;
            _chatLinkCancelButton = null;
            _chatLinkPromptCanvasGroup = null;

            if (_chatLinkPromptRoot != null && !_chatLinkPromptRoot.Equals(null))
                UnityEngine.Object.Destroy(_chatLinkPromptRoot);

            _chatLinkPromptRoot = null;
        }

        internal static bool PollLinkPromptHotkeys()
        {
            if (!_chatLinkPromptOpen)
                return false;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                CancelPendingChatLink();
                return true;
            }

            if (IsChatSubmitKeyPressed())
            {
                OpenPendingChatLink();
                return true;
            }

            return false;
        }

        internal static void EnsureChatLinkUi(ChatBehaviourAssets assets)
        {
            if (!assets || assets._chatText == null)
                return;

            var scrollRect = assets._chatText.GetComponentInParent<ScrollRect>();
            if (scrollRect == null)
                return;

            var overlay = ResolveChatLinkOverlay(scrollRect, assets._chatText);
            if (overlay == null)
                return;

            var handler = overlay.GetComponent<ChatLinkClickHandler>();
            if (handler == null)
                handler = overlay.AddComponent<ChatLinkClickHandler>();

            handler.Bind(assets, scrollRect, assets._chatText);
            assets._chatText.raycastTarget = false;
        }

        internal static bool ShouldInterceptOutgoingChat(string rawMessage)
        {
            return NetworkClient.active
                && IsChatSubmitKeyPressed()
                && !string.IsNullOrWhiteSpace(rawMessage);
        }

        internal static bool TryHandleLocalOocToggle(ChatBehaviour chat, string rawMessage)
        {
            if (!chat || !string.Equals(rawMessage?.Trim(), OocToggleCommand, StringComparison.OrdinalIgnoreCase))
                return false;

            _oocModeEnabled = !_oocModeEnabled;
            FinalizeLocalChatCommand(chat);
            PushLocalChatStatus(chat, _oocModeEnabled ? "OOC chat enabled." : "OOC chat disabled.");
            return true;
        }

        private static OutgoingChatPreparationStatus PrepareOutgoingChatMessage(string rawMessage, out string transformedMessage, out string errorPrompt)
        {
            transformedMessage = rawMessage ?? string.Empty;
            errorPrompt = null;

            if (string.IsNullOrWhiteSpace(rawMessage))
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
                transformedMessage = rawMessage;
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

        private static string ProtectChatLinks(string message, out List<ProtectedChatLink> protectedLinks)
        {
            protectedLinks = null;
            if (string.IsNullOrEmpty(message))
                return message;

            var output = new StringBuilder(message.Length);
            int segmentStart = 0;
            int tagStart = -1;
            bool inTag = false;

            for (int i = 0; i < message.Length; i++)
            {
                char c = message[i];
                if (!inTag && c == '<')
                {
                    AppendProtectedPlainText(output, message, segmentStart, i - segmentStart, ref protectedLinks);
                    inTag = true;
                    tagStart = i;
                    continue;
                }

                if (inTag && c == '>')
                {
                    output.Append(message, tagStart, i - tagStart + 1);
                    inTag = false;
                    segmentStart = i + 1;
                }
            }

            if (inTag && tagStart >= 0)
            {
                output.Append(message, tagStart, message.Length - tagStart);
            }
            else if (segmentStart < message.Length)
            {
                AppendProtectedPlainText(output, message, segmentStart, message.Length - segmentStart, ref protectedLinks);
            }

            return protectedLinks == null || protectedLinks.Count == 0 ? message : output.ToString();
        }

        private static string RestoreProtectedChatLinks(string message, List<ProtectedChatLink> protectedLinks)
        {
            if (string.IsNullOrEmpty(message) || protectedLinks == null || protectedLinks.Count == 0)
                return message;

            foreach (var link in protectedLinks)
            {
                if (link == null || string.IsNullOrEmpty(link.Token))
                    continue;

                string markup = $"<link=\"{link.CanonicalUrl}\"><u>{link.DisplayText}</u></link>";
                message = message.Replace(link.Token, markup, StringComparison.Ordinal);
            }

            return message;
        }

        private static bool HasOocPrefix(string message)
        {
            return !string.IsNullOrWhiteSpace(message)
                && message.TrimStart().StartsWith(OocPrefix, StringComparison.OrdinalIgnoreCase);
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

        private static GameObject ResolveChatLinkOverlay(ScrollRect scrollRect, TextMeshProUGUI chatText)
        {
            if (scrollRect == null)
                return null;

            RectTransform host = scrollRect.viewport;
            if (host == null && chatText != null)
                host = chatText.transform.parent as RectTransform;
            if (host == null)
                host = scrollRect.GetComponent<RectTransform>();
            if (host == null)
                return null;

            Transform existing = host.Find(ChatLinkOverlayName);
            GameObject overlay = existing != null ? existing.gameObject : null;
            if (overlay == null)
            {
                overlay = new GameObject(ChatLinkOverlayName, typeof(RectTransform), typeof(Image))
                {
                    layer = host.gameObject.layer
                };
                SetLayerRecursive(overlay, host.gameObject.layer);

                var overlayRect = overlay.GetComponent<RectTransform>();
                overlayRect.SetParent(host, false);
                overlayRect.anchorMin = Vector2.zero;
                overlayRect.anchorMax = Vector2.one;
                overlayRect.offsetMin = Vector2.zero;
                overlayRect.offsetMax = Vector2.zero;
                overlayRect.localScale = Vector3.one;
            }

            var image = overlay.GetComponent<Image>();
            if (image == null)
                image = overlay.AddComponent<Image>();

            image.color = new Color(0f, 0f, 0f, 0f);
            image.raycastTarget = true;
            image.maskable = true;

            var rect = overlay.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localScale = Vector3.one;
            rect.SetAsLastSibling();

            return overlay;
        }

        private static void AppendProtectedPlainText(StringBuilder output, string source, int startIndex, int length, ref List<ProtectedChatLink> protectedLinks)
        {
            if (length <= 0)
                return;

            string segment = source.Substring(startIndex, length);
            int cursor = 0;

            foreach (Match match in UrlRegex.Matches(segment))
            {
                if (!match.Success)
                    continue;

                output.Append(segment, cursor, match.Index - cursor);

                string candidate = match.Value;
                string trailing = TrimTrailingUrlPunctuation(ref candidate);
                if (!TryCanonicalizeUrl(candidate, out string canonicalUrl))
                {
                    output.Append(match.Value);
                    cursor = match.Index + match.Length;
                    continue;
                }

                protectedLinks ??= [];
                string token = $"CHATX_LINK_TOKEN_{protectedLinks.Count}_";
                protectedLinks.Add(new ProtectedChatLink
                {
                    Token = token,
                    DisplayText = candidate,
                    CanonicalUrl = canonicalUrl
                });

                output.Append(token);
                if (!string.IsNullOrEmpty(trailing))
                    output.Append(trailing);

                cursor = match.Index + match.Length;
            }

            if (cursor < segment.Length)
                output.Append(segment, cursor, segment.Length - cursor);
        }

        private static string TrimTrailingUrlPunctuation(ref string candidate)
        {
            if (string.IsNullOrEmpty(candidate))
                return string.Empty;

            int end = candidate.Length;
            while (end > 0 && IsTrimmedUrlPunctuation(candidate[end - 1]))
                end--;

            string trailing = end < candidate.Length ? candidate[end..] : string.Empty;
            candidate = candidate[..end];
            return trailing;
        }

        private static bool IsTrimmedUrlPunctuation(char c)
        {
            return c switch
            {
                '.' or ',' or '!' or '?' or ';' or ':' or ')' or ']' or '}' or '\'' or '"' => true,
                _ => false
            };
        }

        private static bool TryCanonicalizeUrl(string value, out string canonicalUrl)
        {
            canonicalUrl = null;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string working = value.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? $"https://{value}"
                : value;

            if (!Uri.TryCreate(working, UriKind.Absolute, out var uri))
                return false;

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return false;

            canonicalUrl = uri.AbsoluteUri;
            return true;
        }

        private static void ShowChatLinkPrompt(ChatBehaviourAssets assets, string url)
        {
            if (_chatLinkPromptOpen)
                return;

            if (!TryCanonicalizeUrl(url, out string canonicalUrl))
            {
                ShowErrorPrompt("That link is invalid.");
                return;
            }

            EnsureChatLinkPrompt(assets ?? ChatBehaviourAssets._current);
            if (_chatLinkPromptRoot == null || _chatLinkPromptRoot.Equals(null))
                return;

            _pendingChatLinkUrl = canonicalUrl;
            SetChatLinkPromptText(canonicalUrl);
            SetChatLinkPromptVisible(true);
            EventSystem.current?.SetSelectedGameObject(_chatLinkOpenButton != null ? _chatLinkOpenButton.gameObject : null);
        }

        private static void OpenPendingChatLink()
        {
            string targetUrl = _pendingChatLinkUrl;
            HideChatLinkPrompt();

            if (!TryCanonicalizeUrl(targetUrl, out string canonicalUrl))
            {
                ShowErrorPrompt("That link is invalid.");
                return;
            }

            Application.OpenURL(canonicalUrl);
        }

        private static void CancelPendingChatLink()
        {
            HideChatLinkPrompt();
        }

        private static void HideChatLinkPrompt()
        {
            _pendingChatLinkUrl = null;
            SetChatLinkPromptVisible(false);
            EventSystem.current?.SetSelectedGameObject(null);
        }

        private static void SetChatLinkPromptVisible(bool visible)
        {
            _chatLinkPromptOpen = visible;

            if (_chatLinkPromptRoot == null || _chatLinkPromptRoot.Equals(null))
                return;

            if (_chatLinkPromptElement != null)
            {
                _chatLinkPromptRoot.SetActive(true);
                var menuRect = GetMenuElementRect(_chatLinkPromptElement);
                if (menuRect != null)
                    menuRect.anchoredPosition = Vector2.zero;
                _chatLinkPromptElement.isEnabled = visible;
            }

            if (_chatLinkPromptCanvasGroup != null)
            {
                _chatLinkPromptCanvasGroup.alpha = visible ? 1f : 0f;
                _chatLinkPromptCanvasGroup.blocksRaycasts = visible;
                _chatLinkPromptCanvasGroup.interactable = visible;
            }

            if (_chatLinkPromptElement == null)
            {
                _chatLinkPromptRoot.SetActive(visible);
            }

            if (visible && _chatLinkPromptRoot.transform is RectTransform promptRect)
            {
                promptRect.SetAsLastSibling();
                LayoutRebuilder.ForceRebuildLayoutImmediate(promptRect);
            }
        }

        private static void SetChatLinkPromptText(string canonicalUrl)
        {
            string promptText = $"Open this link in your browser?\n{canonicalUrl}";
            if (_chatLinkPromptText != null)
                _chatLinkPromptText.text = promptText;
            if (_chatLinkPromptTextLegacy != null)
                _chatLinkPromptTextLegacy.text = promptText;
        }

        private static void EnsureChatLinkPrompt(ChatBehaviourAssets assets)
        {
            if (_chatLinkPromptRoot != null && !_chatLinkPromptRoot.Equals(null))
                return;

            Transform parent = ResolveChatLinkPromptParent(assets);
            if (parent == null)
                return;

            int layer = assets != null ? assets.gameObject.layer : parent.gameObject.layer;
            if (TryCreatePromptFromPartyInvite(parent, layer))
                return;

            ResolvePartyUiTemplates(out var acceptTemplate, out var declineTemplate, out var promptTemplate, out var panelTemplate);

            _chatLinkPromptRoot = new GameObject("ChatX_LinkPrompt", typeof(RectTransform), typeof(Image), typeof(CanvasGroup))
            {
                layer = layer
            };

            SetLayerRecursive(_chatLinkPromptRoot, layer);
            var rootRect = _chatLinkPromptRoot.GetComponent<RectTransform>();
            rootRect.SetParent(parent, false);
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;
            rootRect.SetAsLastSibling();

            var rootImage = _chatLinkPromptRoot.GetComponent<Image>();
            rootImage.color = new Color(0f, 0f, 0f, 0.45f);
            rootImage.raycastTarget = true;

            _chatLinkPromptCanvasGroup = _chatLinkPromptRoot.GetComponent<CanvasGroup>();

            var panelObject = new GameObject("Panel", typeof(RectTransform), typeof(Image))
            {
                layer = layer
            };
            SetLayerRecursive(panelObject, layer);
            var panelRect = panelObject.GetComponent<RectTransform>();
            panelRect.SetParent(rootRect, false);
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(720f, 230f);

            ApplyPanelStyle(panelObject.GetComponent<Image>(), panelTemplate);

            _chatLinkPromptText = CreatePromptText(panelRect, layer, promptTemplate, assets?._chatText);
            _chatLinkOpenButton = CreatePromptButton(panelRect, "Open", acceptTemplate, assets?._chatText, layer);
            _chatLinkCancelButton = CreatePromptButton(panelRect, "Cancel", declineTemplate, assets?._chatText, layer);

            if (_chatLinkOpenButton != null)
            {
                _chatLinkOpenButton.onClick = new Button.ButtonClickedEvent();
                _chatLinkOpenButton.onClick.AddListener(OpenPendingChatLink);
            }

            if (_chatLinkCancelButton != null)
            {
                _chatLinkCancelButton.onClick = new Button.ButtonClickedEvent();
                _chatLinkCancelButton.onClick.AddListener(CancelPendingChatLink);
            }

            SetChatLinkPromptVisible(false);
        }

        private static bool TryCreatePromptFromPartyInvite(Transform parent, int layer)
        {
            try
            {
                object current = PartyUiCurrentField?.GetValue(null);
                var inviteElement = PartyInviteElementField?.GetValue(current) as MenuElement;
                if (current == null || inviteElement == null || inviteElement.gameObject == null)
                    return false;

                _chatLinkPromptRoot = UnityEngine.Object.Instantiate(inviteElement.gameObject, parent, false);
                _chatLinkPromptRoot.name = "ChatX_LinkPrompt";
                _chatLinkPromptRoot.SetActive(true);
                SetLayerRecursive(_chatLinkPromptRoot, layer);

                var templateRect = inviteElement.GetComponent<RectTransform>();
                var rootRect = _chatLinkPromptRoot.GetComponent<RectTransform>();
                if (rootRect != null)
                {
                    Vector2 fallbackSize = new(720f, 230f);
                    Vector2 templateSize = fallbackSize;
                    if (templateRect != null)
                    {
                        templateSize = templateRect.rect.size;
                        if (templateSize.x <= 1f || templateSize.y <= 1f)
                            templateSize = templateRect.sizeDelta;
                    }

                    if (templateSize.x <= 1f || templateSize.y <= 1f)
                        templateSize = fallbackSize;

                    rootRect.anchorMin = new Vector2(0.5f, 0.5f);
                    rootRect.anchorMax = new Vector2(0.5f, 0.5f);
                    rootRect.pivot = templateRect != null ? templateRect.pivot : new Vector2(0.5f, 0.5f);
                    rootRect.sizeDelta = templateSize;
                    rootRect.anchoredPosition = Vector2.zero;
                    rootRect.localScale = Vector3.one;
                    rootRect.localRotation = Quaternion.identity;
                    rootRect.SetAsLastSibling();
                }

                _chatLinkPromptElement = _chatLinkPromptRoot.GetComponent<MenuElement>();
                var menuRect = GetMenuElementRect(_chatLinkPromptElement);
                if (menuRect != null)
                    menuRect.anchoredPosition = Vector2.zero;

                _chatLinkPromptCanvasGroup = GetMenuElementCanvasGroup(_chatLinkPromptElement)
                    ?? _chatLinkPromptRoot.GetComponentInChildren<CanvasGroup>(true)
                    ?? _chatLinkPromptRoot.GetComponent<CanvasGroup>();

                var originalPrompt = PartyInvitePromptField?.GetValue(current) as Component;
                var originalOpenButton = PartyAcceptInviteButtonField?.GetValue(current) as Component;
                var originalCancelButton = PartyDeclineInviteButtonField?.GetValue(current) as Component;

                _chatLinkPromptText = FindClonedComponent<TextMeshProUGUI>(inviteElement.transform, originalPrompt, _chatLinkPromptRoot.transform);
                _chatLinkPromptTextLegacy = FindClonedComponent<Text>(inviteElement.transform, originalPrompt, _chatLinkPromptRoot.transform)
                    ?? _chatLinkPromptRoot.GetComponentInChildren<Text>(true);
                _chatLinkOpenButton = FindClonedComponent<Button>(inviteElement.transform, originalOpenButton, _chatLinkPromptRoot.transform);
                _chatLinkCancelButton = FindClonedComponent<Button>(inviteElement.transform, originalCancelButton, _chatLinkPromptRoot.transform);

                if (_chatLinkOpenButton == null || _chatLinkCancelButton == null)
                {
                    UnityEngine.Object.Destroy(_chatLinkPromptRoot);
                    ResetLinkPromptRuntimeState();
                    return false;
                }

                _chatLinkOpenButton.onClick = new Button.ButtonClickedEvent();
                _chatLinkOpenButton.onClick.AddListener(OpenPendingChatLink);
                _chatLinkCancelButton.onClick = new Button.ButtonClickedEvent();
                _chatLinkCancelButton.onClick.AddListener(CancelPendingChatLink);

                ConfigurePromptButtonContent(_chatLinkOpenButton.gameObject, "Open", PromptButtonIconKind.Confirm, _chatLinkPromptText);
                ConfigurePromptButtonContent(_chatLinkCancelButton.gameObject, "Cancel", PromptButtonIconKind.Cancel, _chatLinkPromptText);

                SetChatLinkPromptVisible(false);
                return true;
            }
            catch (Exception ex)
            {
                Log?.LogDebug($"ChatX could not clone the party invite modal for link prompts: {ex.Message}");
                ResetLinkPromptRuntimeState();
                return false;
            }
        }

        private static Transform ResolveChatLinkPromptParent(ChatBehaviourAssets assets)
        {
            Transform Resolve(ChatBehaviourAssets candidate)
            {
                if (candidate == null)
                    return null;

                Canvas canvas = null;
                if (candidate._chatText != null)
                    canvas = candidate._chatText.canvas;

                canvas ??= candidate.GetComponentInParent<Canvas>();
                if (canvas != null)
                    return canvas.transform;

                return candidate.transform;
            }

            Transform resolved = Resolve(assets) ?? Resolve(ChatBehaviourAssets._current);
            if (resolved != null)
                return resolved;

            try
            {
                object current = PartyUiCurrentField?.GetValue(null);
                if (current != null && PartyPanelGroupField?.GetValue(current) is CanvasGroup panelGroup)
                {
                    var canvas = panelGroup.GetComponentInParent<Canvas>();
                    if (canvas != null)
                        return canvas.transform;
                }
            }
            catch (Exception ex)
            {
                Log?.LogDebug($"ChatX could not resolve link prompt canvas parent: {ex.Message}");
            }

            return null;
        }

        private static T FindClonedComponent<T>(Transform originalRoot, Component originalComponent, Transform clonedRoot)
            where T : Component
        {
            if (originalRoot == null || originalComponent == null || clonedRoot == null)
                return null;

            string relativePath = GetRelativeTransformPath(originalRoot, originalComponent.transform);
            Transform clonedTransform = string.IsNullOrEmpty(relativePath) ? clonedRoot : clonedRoot.Find(relativePath);
            return clonedTransform != null ? clonedTransform.GetComponent<T>() : null;
        }

        private static string GetRelativeTransformPath(Transform root, Transform target)
        {
            if (root == null || target == null)
                return null;

            if (ReferenceEquals(root, target))
                return string.Empty;

            var segments = new Stack<string>();
            var current = target;
            while (current != null && !ReferenceEquals(current, root))
            {
                segments.Push(current.name);
                current = current.parent;
            }

            if (!ReferenceEquals(current, root))
                return null;

            return string.Join("/", segments);
        }

        private static RectTransform GetMenuElementRect(MenuElement element)
        {
            return element != null ? MenuElementRectField?.GetValue(element) as RectTransform : null;
        }

        private static CanvasGroup GetMenuElementCanvasGroup(MenuElement element)
        {
            return element != null ? MenuElementCanvasGroupField?.GetValue(element) as CanvasGroup : null;
        }

        private static void ResolvePartyUiTemplates(out Button acceptTemplate, out Button declineTemplate, out Component promptTemplate, out Graphic panelTemplate)
        {
            acceptTemplate = null;
            declineTemplate = null;
            promptTemplate = null;
            panelTemplate = null;

            try
            {
                object current = PartyUiCurrentField?.GetValue(null);
                if (current == null)
                    return;

                acceptTemplate = PartyAcceptInviteButtonField?.GetValue(current) as Button;
                declineTemplate = PartyDeclineInviteButtonField?.GetValue(current) as Button;
                promptTemplate = PartyInvitePromptField?.GetValue(current) as Component;

                if (PartyPanelGroupField?.GetValue(current) is CanvasGroup panelGroup)
                    panelTemplate = panelGroup.GetComponent<Graphic>() ?? panelGroup.GetComponentInChildren<Graphic>(true);
            }
            catch (Exception ex)
            {
                Log?.LogDebug($"ChatX could not resolve party UI templates: {ex.Message}");
            }
        }

        private static void ApplyPanelStyle(Image target, Graphic template)
        {
            if (target == null)
                return;

            if (template is Image templateImage)
            {
                target.sprite = templateImage.sprite;
                target.material = templateImage.material;
                target.type = templateImage.type;
                target.color = templateImage.color;
                target.pixelsPerUnitMultiplier = templateImage.pixelsPerUnitMultiplier;
                target.preserveAspect = templateImage.preserveAspect;
            }
            else
            {
                target.color = new Color(0f, 0f, 0f, 0.92f);
            }

            target.raycastTarget = true;
        }

        private static TextMeshProUGUI CreatePromptText(RectTransform panelRect, int layer, Component template, TextMeshProUGUI fallback)
        {
            var textObject = new GameObject("PromptText", typeof(RectTransform), typeof(TextMeshProUGUI))
            {
                layer = layer
            };
            SetLayerRecursive(textObject, layer);

            var textRect = textObject.GetComponent<RectTransform>();
            textRect.SetParent(panelRect, false);
            textRect.anchorMin = new Vector2(0.08f, 0.38f);
            textRect.anchorMax = new Vector2(0.92f, 0.86f);
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textObject.GetComponent<TextMeshProUGUI>();
            ApplyPromptTextStyle(text, template, fallback);
            text.text = "Open this link in your browser?";
            return text;
        }

        private static Button CreatePromptButton(RectTransform panelRect, string label, Button template, TextMeshProUGUI fallback, int layer)
        {
            string objectName = $"Button_{label}";
            GameObject buttonObject;

            if (template != null)
            {
                buttonObject = UnityEngine.Object.Instantiate(template.gameObject);
                buttonObject.name = objectName;
            }
            else
            {
                buttonObject = CreateFallbackPromptButton(objectName, label, fallback, layer);
            }

            SetLayerRecursive(buttonObject, layer);

            var rect = buttonObject.GetComponent<RectTransform>();
            if (rect == null)
                rect = buttonObject.AddComponent<RectTransform>();

            rect.SetParent(panelRect, false);
            rect.anchorMin = label == "Open" ? new Vector2(0.23f, 0.08f) : new Vector2(0.53f, 0.08f);
            rect.anchorMax = label == "Open" ? new Vector2(0.47f, 0.28f) : new Vector2(0.77f, 0.28f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localScale = Vector3.one;

            var button = buttonObject.GetComponent<Button>();
            if (button == null)
                button = buttonObject.AddComponent<Button>();

            button.onClick = new Button.ButtonClickedEvent();
            ConfigurePromptButtonContent(buttonObject, label, label == "Open" ? PromptButtonIconKind.Confirm : PromptButtonIconKind.Cancel, fallback);
            return button;
        }

        private static GameObject CreateFallbackPromptButton(string name, string label, TextMeshProUGUI fallback, int layer)
        {
            var buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button))
            {
                layer = layer
            };

            var image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.18f, 0.18f, 0.18f, 0.95f);

            var labelObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI))
            {
                layer = layer
            };
            SetLayerRecursive(labelObject, layer);

            var labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.SetParent(buttonObject.transform, false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            var text = labelObject.GetComponent<TextMeshProUGUI>();
            ApplyPromptTextStyle(text, null, fallback);
            text.alignment = TextAlignmentOptions.Center;
            text.fontSize = Mathf.Max(20f, text.fontSize);
            text.text = label;

            return buttonObject;
        }

        private static void UpdateButtonLabel(GameObject buttonObject, string label, TextMeshProUGUI fallback)
        {
            if (buttonObject == null)
                return;

            var tmpText = buttonObject.GetComponentInChildren<TMP_Text>(true);
            if (tmpText != null)
            {
                tmpText.text = label;
                if (tmpText is TextMeshProUGUI tmpUgui)
                    ApplyPromptTextStyle(tmpUgui, null, fallback);
                return;
            }

            var text = buttonObject.GetComponentInChildren<Text>(true);
            if (text != null)
            {
                text.text = label;
                if (fallback != null)
                {
                    text.color = fallback.color;
                    text.fontSize = Mathf.RoundToInt(Mathf.Max(18f, fallback.fontSize));
                    text.alignment = TextAnchor.MiddleCenter;
                }
            }
        }

        private static void ConfigurePromptButtonContent(GameObject buttonObject, string label, PromptButtonIconKind iconKind, TextMeshProUGUI fallback)
        {
            if (buttonObject == null)
                return;

            UpdateButtonLabel(buttonObject, label, fallback);
            SuppressPromptButtonChildIcons(buttonObject);
            AdjustPromptButtonLabelLayout(buttonObject);
            BuildPromptButtonIcon(buttonObject, iconKind, fallback != null ? fallback.color : Color.white);
        }

        private static void SuppressPromptButtonChildIcons(GameObject buttonObject)
        {
            if (buttonObject == null)
                return;

            var rootRect = buttonObject.GetComponent<RectTransform>();
            float rootArea = 0f;
            if (rootRect != null)
            {
                Vector2 rootSize = rootRect.rect.size;
                rootArea = Mathf.Abs(rootSize.x * rootSize.y);
            }

            var rootImage = buttonObject.GetComponent<Image>();
            foreach (var image in buttonObject.GetComponentsInChildren<Image>(true))
            {
                if (image == null || image == rootImage)
                    continue;

                if (image.transform.parent != buttonObject.transform)
                    continue;

                var rect = image.rectTransform;
                float imageArea = rect != null ? Mathf.Abs(rect.rect.width * rect.rect.height) : 0f;
                bool keepAsLargeOverlay = rootArea > 0f && imageArea >= rootArea * 0.7f;
                if (keepAsLargeOverlay)
                    continue;

                image.enabled = false;
                image.raycastTarget = false;
            }
        }

        private static void AdjustPromptButtonLabelLayout(GameObject buttonObject)
        {
            if (buttonObject == null)
                return;

            foreach (var tmpText in buttonObject.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (tmpText == null || tmpText.gameObject.name == "ChatX_Icon")
                    continue;

                var rect = tmpText.rectTransform;
                if (rect != null)
                {
                    rect.offsetMin = new Vector2(Mathf.Max(rect.offsetMin.x, 34f), rect.offsetMin.y);
                    rect.offsetMax = new Vector2(Mathf.Min(rect.offsetMax.x, -8f), rect.offsetMax.y);
                }

                tmpText.alignment = TextAlignmentOptions.MidlineLeft;
                return;
            }

            foreach (var text in buttonObject.GetComponentsInChildren<Text>(true))
            {
                if (text == null || text.gameObject.name == "ChatX_Icon")
                    continue;

                if (text.transform is RectTransform rect)
                {
                    rect.offsetMin = new Vector2(Mathf.Max(rect.offsetMin.x, 34f), rect.offsetMin.y);
                    rect.offsetMax = new Vector2(Mathf.Min(rect.offsetMax.x, -8f), rect.offsetMax.y);
                }

                text.alignment = TextAnchor.MiddleLeft;
                return;
            }
        }

        private static void BuildPromptButtonIcon(GameObject buttonObject, PromptButtonIconKind iconKind, Color baseColor)
        {
            if (buttonObject == null)
                return;

            Transform existing = buttonObject.transform.Find("ChatX_Icon");
            GameObject iconRoot;
            if (existing != null)
            {
                iconRoot = existing.gameObject;
                for (int i = iconRoot.transform.childCount - 1; i >= 0; i--)
                    UnityEngine.Object.Destroy(iconRoot.transform.GetChild(i).gameObject);
            }
            else
            {
                iconRoot = new GameObject("ChatX_Icon", typeof(RectTransform));
                SetLayerRecursive(iconRoot, buttonObject.layer);
                var iconRect = iconRoot.GetComponent<RectTransform>();
                iconRect.SetParent(buttonObject.transform, false);
            }

            var rootRect = iconRoot.GetComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0f, 0.5f);
            rootRect.anchorMax = new Vector2(0f, 0.5f);
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.sizeDelta = new Vector2(24f, 24f);
            rootRect.anchoredPosition = new Vector2(20f, 0f);
            rootRect.SetAsFirstSibling();

            Color iconColor = baseColor;
            if (iconKind == PromptButtonIconKind.Confirm)
                iconColor = new Color(Mathf.Clamp01(baseColor.r + 0.08f), Mathf.Clamp01(baseColor.g + 0.12f), Mathf.Clamp01(baseColor.b), baseColor.a);

            switch (iconKind)
            {
                case PromptButtonIconKind.Confirm:
                    CreatePromptIconStroke(iconRoot.transform, new Vector2(5f, 12f), new Vector2(-5.5f, -3.5f), 42f, iconColor);
                    CreatePromptIconStroke(iconRoot.transform, new Vector2(5f, 22f), new Vector2(4.5f, 1.5f), -44f, iconColor);
                    break;
                case PromptButtonIconKind.Cancel:
                    CreatePromptIconStroke(iconRoot.transform, new Vector2(5f, 22f), Vector2.zero, 45f, iconColor);
                    CreatePromptIconStroke(iconRoot.transform, new Vector2(5f, 22f), Vector2.zero, -45f, iconColor);
                    break;
            }
        }

        private static void CreatePromptIconStroke(Transform parent, Vector2 size, Vector2 position, float rotation, Color color)
        {
            if (parent == null)
                return;

            var strokeObject = new GameObject("Stroke", typeof(RectTransform), typeof(Image))
            {
                layer = parent.gameObject.layer
            };

            var rect = strokeObject.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            rect.localRotation = Quaternion.Euler(0f, 0f, rotation);

            var image = strokeObject.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
        }

        private static void ApplyPromptTextStyle(TextMeshProUGUI target, Component template, TextMeshProUGUI fallback)
        {
            if (target == null)
                return;

            if (template is TMP_Text tmpTemplate)
            {
                target.font = tmpTemplate.font;
                target.fontStyle = tmpTemplate.fontStyle;
                target.fontMaterial = tmpTemplate.fontMaterial;
                target.color = tmpTemplate.color;
                target.fontSize = tmpTemplate.fontSize;
            }
            else if (template is Text textTemplate)
            {
                if (TMP_Settings.defaultFontAsset != null)
                    target.font = TMP_Settings.defaultFontAsset;
                target.color = textTemplate.color;
                target.fontSize = textTemplate.fontSize;
            }
            else if (fallback != null)
            {
                target.font = fallback.font;
                target.fontStyle = fallback.fontStyle;
                target.fontMaterial = fallback.fontMaterial;
                target.color = fallback.color;
                target.fontSize = fallback.fontSize;
            }
            else if (TMP_Settings.defaultFontAsset != null)
            {
                target.font = TMP_Settings.defaultFontAsset;
                target.color = Color.white;
                target.fontSize = 24f;
            }

            target.alignment = TextAlignmentOptions.Center;
            target.enableWordWrapping = true;
            target.overflowMode = TextOverflowModes.Overflow;
            target.raycastTarget = false;
        }

        private static void SetLayerRecursive(GameObject root, int layer)
        {
            if (root == null)
                return;

            root.layer = layer;
            foreach (Transform child in root.transform)
                SetLayerRecursive(child.gameObject, layer);
        }

        private sealed class ChatLinkClickHandler : MonoBehaviour, IPointerClickHandler, IPointerDownHandler, IPointerUpHandler, IInitializePotentialDragHandler, IBeginDragHandler, IDragHandler, IEndDragHandler, IScrollHandler
        {
            private TextMeshProUGUI _chatText;
            private ScrollRect _scrollRect;
            private ChatBehaviourAssets _assets;

            internal void Bind(ChatBehaviourAssets assets, ScrollRect scrollRect, TextMeshProUGUI chatText)
            {
                _assets = assets;
                _chatText = chatText ?? _assets?._chatText;
                _scrollRect = scrollRect ?? (_chatText != null ? _chatText.GetComponentInParent<ScrollRect>() : GetComponentInParent<ScrollRect>());
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData == null || eventData.button != PointerEventData.InputButton.Left || _chatText == null || _chatLinkPromptOpen)
                    return;

                int linkIndex = TMP_TextUtilities.FindIntersectingLink(_chatText, eventData.position, eventData.pressEventCamera);
                if (linkIndex < 0 || linkIndex >= _chatText.textInfo.linkCount)
                    return;

                string url = _chatText.textInfo.linkInfo[linkIndex].GetLinkID();
                if (string.IsNullOrWhiteSpace(url))
                    return;

                ShowChatLinkPrompt(_assets, url);
                eventData.Use();
            }

            public void OnPointerDown(PointerEventData eventData) => Forward(eventData, ExecuteEvents.pointerDownHandler);
            public void OnPointerUp(PointerEventData eventData) => Forward(eventData, ExecuteEvents.pointerUpHandler);
            public void OnInitializePotentialDrag(PointerEventData eventData) => Forward(eventData, ExecuteEvents.initializePotentialDrag);
            public void OnBeginDrag(PointerEventData eventData) => Forward(eventData, ExecuteEvents.beginDragHandler);
            public void OnDrag(PointerEventData eventData) => Forward(eventData, ExecuteEvents.dragHandler);
            public void OnEndDrag(PointerEventData eventData) => Forward(eventData, ExecuteEvents.endDragHandler);
            public void OnScroll(PointerEventData eventData) => Forward(eventData, ExecuteEvents.scrollHandler);

            private void Forward<THandler>(BaseEventData eventData, ExecuteEvents.EventFunction<THandler> handler)
                where THandler : IEventSystemHandler
            {
                if (_scrollRect == null || _scrollRect.Equals(null) || eventData == null)
                    return;

                ExecuteEvents.Execute(_scrollRect.gameObject, eventData, handler);
            }
        }
    }
}
