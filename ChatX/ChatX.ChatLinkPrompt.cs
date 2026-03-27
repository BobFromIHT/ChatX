using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ChatX
{
    public partial class ChatX
    {
        private static readonly Type PartyUiManagerType = typeof(Player).Assembly.GetType("PartyUIManager");
        private static readonly FieldInfo PartyUiCurrentField = AccessTools.Field(PartyUiManagerType, "_current");
        private static readonly FieldInfo PartyInviteElementField = AccessTools.Field(PartyUiManagerType, "_partyInviteElement");
        private static readonly FieldInfo PartyAcceptInviteButtonField = AccessTools.Field(PartyUiManagerType, "_acceptInviteButton");
        private static readonly FieldInfo PartyDeclineInviteButtonField = AccessTools.Field(PartyUiManagerType, "_declineInviteButton");
        private static readonly FieldInfo PartyInvitePromptField = AccessTools.Field(PartyUiManagerType, "_invitedByPrompt");
        private static readonly FieldInfo PartyPanelGroupField = AccessTools.Field(PartyUiManagerType, "_partyPanelGroup");
        private static readonly FieldInfo MenuElementRectField = AccessTools.Field(typeof(MenuElement), "menuRect");
        private static readonly FieldInfo MenuElementCanvasGroupField = AccessTools.Field(typeof(MenuElement), "_canvasGroup");

        private static GameObject _chatLinkPromptRoot;
        private static MenuElement _chatLinkPromptElement;
        private static CanvasGroup _chatLinkPromptCanvasGroup;
        private static TextMeshProUGUI _chatLinkPromptText;
        private static Text _chatLinkPromptTextLegacy;
        private static Button _chatLinkOpenButton;
        private static Button _chatLinkCopyButton;
        private static Button _chatLinkCancelButton;
        private static string _pendingChatLinkUrl;
        private static bool _chatLinkPromptOpen;

        private enum PromptButtonIconKind
        {
            Confirm,
            Copy,
            Cancel
        }

        internal static bool IsChatLinkPromptOpen => _chatLinkPromptOpen;

        internal static void ResetLinkPromptRuntimeState()
        {
            _pendingChatLinkUrl = null;
            _chatLinkPromptOpen = false;
            _chatLinkPromptElement = null;
            _chatLinkPromptText = null;
            _chatLinkPromptTextLegacy = null;
            _chatLinkOpenButton = null;
            _chatLinkCopyButton = null;
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

        internal static void ShowChatLinkPrompt(ChatBehaviourAssets assets, string url)
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
            {
                ShowErrorPrompt("Chat link prompts are unavailable right now.");
                return;
            }

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

        private static void CopyPendingChatLink()
        {
            string targetUrl = _pendingChatLinkUrl;
            HideChatLinkPrompt();

            if (!TryCanonicalizeUrl(targetUrl, out string canonicalUrl))
            {
                ShowErrorPrompt("That link is invalid.");
                return;
            }

            try
            {
                GUIUtility.systemCopyBuffer = canonicalUrl;
                PushLocalChatStatus(ChatBehaviour._current, "Link copied to clipboard.");
            }
            catch (Exception ex)
            {
                Log?.LogDebug($"ChatX could not copy the pending link: {ex.Message}");
                ShowErrorPrompt("Could not copy that link.");
            }
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
                _chatLinkPromptRoot.SetActive(visible);

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
            TryCreatePromptFromPartyInvite(parent, layer);
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
                    rootRect.sizeDelta = templateSize * 2f;
                    rootRect.anchoredPosition = Vector2.zero;
                    rootRect.localScale = Vector3.one;
                    rootRect.localRotation = Quaternion.identity;
                    rootRect.SetAsLastSibling();
                }

                _chatLinkPromptElement = _chatLinkPromptRoot.GetComponent<MenuElement>();
                var menuRect = GetMenuElementRect(_chatLinkPromptElement);
                if (menuRect != null)
                {
                    if (rootRect == null || !ReferenceEquals(menuRect, rootRect))
                    {
                        Vector2 menuSize = menuRect.rect.size;
                        if (menuSize.x <= 1f || menuSize.y <= 1f)
                            menuSize = menuRect.sizeDelta;

                        if (menuSize.x > 1f && menuSize.y > 1f)
                            menuRect.sizeDelta = menuSize * 2f;
                    }

                    menuRect.anchoredPosition = Vector2.zero;
                }

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
                _chatLinkCopyButton = ClonePromptButton(_chatLinkOpenButton, "Button_Copy", layer);

                if (_chatLinkOpenButton == null || _chatLinkCopyButton == null || _chatLinkCancelButton == null)
                {
                    UnityEngine.Object.Destroy(_chatLinkPromptRoot);
                    ResetLinkPromptRuntimeState();
                    return false;
                }

                _chatLinkOpenButton.onClick = new Button.ButtonClickedEvent();
                _chatLinkOpenButton.onClick.AddListener(OpenPendingChatLink);
                _chatLinkCopyButton.onClick = new Button.ButtonClickedEvent();
                _chatLinkCopyButton.onClick.AddListener(CopyPendingChatLink);
                _chatLinkCancelButton.onClick = new Button.ButtonClickedEvent();
                _chatLinkCancelButton.onClick.AddListener(CancelPendingChatLink);

                ConfigurePromptButtonContent(_chatLinkOpenButton.gameObject, "Open", PromptButtonIconKind.Confirm, _chatLinkPromptText, iconOnly: true);
                ConfigurePromptButtonContent(_chatLinkCopyButton.gameObject, "Copy", PromptButtonIconKind.Copy, _chatLinkPromptText, iconOnly: true);
                ConfigurePromptButtonContent(_chatLinkCancelButton.gameObject, "Cancel", PromptButtonIconKind.Cancel, _chatLinkPromptText, iconOnly: true);

                LayoutPromptButtons(menuRect ?? rootRect, _chatLinkOpenButton, _chatLinkCopyButton, _chatLinkCancelButton);
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

        private static Button ClonePromptButton(Button template, string objectName, int layer)
        {
            if (template == null || template.gameObject == null)
                return null;

            var parent = template.transform.parent;
            if (parent == null)
                return null;

            var buttonObject = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
            buttonObject.name = objectName;
            SetLayerRecursive(buttonObject, layer);

            var rect = buttonObject.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.localScale = Vector3.one;
                rect.localRotation = Quaternion.identity;
            }

            var button = buttonObject.GetComponent<Button>();
            return button != null ? button : buttonObject.AddComponent<Button>();
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

        private static void ConfigurePromptButtonContent(GameObject buttonObject, string label, PromptButtonIconKind iconKind, TextMeshProUGUI fallback, bool iconOnly = false)
        {
            if (buttonObject == null)
                return;

            UpdateButtonLabel(buttonObject, iconOnly ? string.Empty : label, fallback);
            SuppressPromptButtonChildIcons(buttonObject);
            SetPromptButtonLabelVisible(buttonObject, !iconOnly);
            if (!iconOnly)
                AdjustPromptButtonLabelLayout(buttonObject);
            BuildPromptButtonIcon(buttonObject, iconKind, fallback != null ? fallback.color : Color.white, centered: iconOnly);
        }

        private static void LayoutPromptButtons(RectTransform hostRect, params Button[] buttons)
        {
            if (hostRect == null || buttons == null || buttons.Length == 0)
                return;

            var liveButtons = new List<Button>(buttons.Length);
            foreach (var button in buttons)
            {
                if (button == null || button.Equals(null))
                    continue;

                liveButtons.Add(button);
            }

            if (liveButtons.Count == 0)
                return;

            Transform existingRow = hostRect.Find("ChatX_ButtonRow");
            RectTransform rowRect;
            if (existingRow != null)
            {
                rowRect = existingRow as RectTransform;
            }
            else
            {
                var rowObject = new GameObject("ChatX_ButtonRow", typeof(RectTransform));
                rowObject.layer = hostRect.gameObject.layer;
                SetLayerRecursive(rowObject, hostRect.gameObject.layer);
                rowRect = rowObject.GetComponent<RectTransform>();
                rowRect.SetParent(hostRect, false);
            }

            float buttonHeight = 0f;
            foreach (var button in liveButtons)
            {
                var rect = button.GetComponent<RectTransform>();
                if (rect == null)
                    continue;

                float height = Mathf.Abs(rect.rect.height);
                if (height <= 1f)
                    height = Mathf.Abs(rect.sizeDelta.y);
                if (height > 1f)
                {
                    buttonHeight = height;
                    break;
                }
            }

            if (buttonHeight <= 1f)
                buttonHeight = 44f;

            float buttonWidth = Mathf.Max(buttonHeight * 1.7f, 78f);
            float horizontalGap = Mathf.Clamp(buttonHeight * 0.2f, 8f, 14f);
            float totalWidth = (buttonWidth * liveButtons.Count) + (horizontalGap * (liveButtons.Count - 1));

            rowRect.anchorMin = new Vector2(0.5f, 0f);
            rowRect.anchorMax = new Vector2(0.5f, 0f);
            rowRect.pivot = new Vector2(0.5f, 0f);
            rowRect.sizeDelta = new Vector2(totalWidth, buttonHeight);
            rowRect.anchoredPosition = new Vector2(0f, Mathf.Clamp(buttonHeight * 0.5f, 18f, 30f));
            rowRect.SetAsLastSibling();

            for (int i = 0; i < liveButtons.Count; i++)
            {
                var button = liveButtons[i];
                var rect = button.GetComponent<RectTransform>();
                if (rect == null)
                    continue;

                button.transform.SetParent(rowRect, false);
                float startX = -((totalWidth - buttonWidth) * 0.5f);
                float xOffset = startX + (i * (buttonWidth + horizontalGap));
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(buttonWidth, buttonHeight);
                rect.anchoredPosition = new Vector2(xOffset, 0f);
                rect.localScale = Vector3.one;
                rect.localRotation = Quaternion.identity;

                var layoutElement = button.GetComponent<LayoutElement>();
                if (layoutElement != null)
                    layoutElement.ignoreLayout = true;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(rowRect);
            LayoutRebuilder.ForceRebuildLayoutImmediate(hostRect);
        }

        private static void SetPromptButtonLabelVisible(GameObject buttonObject, bool visible)
        {
            if (buttonObject == null)
                return;

            foreach (var tmpText in buttonObject.GetComponentsInChildren<TMP_Text>(true))
            {
                if (tmpText == null || tmpText.gameObject.name == "ChatX_Icon")
                    continue;

                tmpText.text = visible ? tmpText.text : string.Empty;
                tmpText.enabled = visible;
            }

            foreach (var text in buttonObject.GetComponentsInChildren<Text>(true))
            {
                if (text == null || text.gameObject.name == "ChatX_Icon")
                    continue;

                text.text = visible ? text.text : string.Empty;
                text.enabled = visible;
            }
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

        private static void BuildPromptButtonIcon(GameObject buttonObject, PromptButtonIconKind iconKind, Color baseColor, bool centered = false)
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
            rootRect.anchorMin = new Vector2(0.5f, 0.5f);
            rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.sizeDelta = new Vector2(24f, 24f);
            rootRect.anchoredPosition = centered ? Vector2.zero : new Vector2(-18f, 0f);
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
                case PromptButtonIconKind.Copy:
                    CreatePromptIconOutline(iconRoot.transform, new Vector2(10f, 12f), new Vector2(-2.5f, 2.5f), 2f, new Color(iconColor.r, iconColor.g, iconColor.b, iconColor.a * 0.72f));
                    CreatePromptIconOutline(iconRoot.transform, new Vector2(10f, 12f), new Vector2(2.5f, -1.5f), 2f, iconColor);
                    break;
                case PromptButtonIconKind.Cancel:
                    CreatePromptIconStroke(iconRoot.transform, new Vector2(5f, 22f), Vector2.zero, 45f, iconColor);
                    CreatePromptIconStroke(iconRoot.transform, new Vector2(5f, 22f), Vector2.zero, -45f, iconColor);
                    break;
            }
        }

        private static void CreatePromptIconOutline(Transform parent, Vector2 size, Vector2 position, float thickness, Color color)
        {
            if (parent == null)
                return;

            float halfWidth = Mathf.Max(0f, (size.x - thickness) * 0.5f);
            float halfHeight = Mathf.Max(0f, (size.y - thickness) * 0.5f);

            CreatePromptIconStroke(parent, new Vector2(size.x, thickness), position + new Vector2(0f, halfHeight), 0f, color);
            CreatePromptIconStroke(parent, new Vector2(size.x, thickness), position - new Vector2(0f, halfHeight), 0f, color);
            CreatePromptIconStroke(parent, new Vector2(thickness, size.y), position + new Vector2(halfWidth, 0f), 0f, color);
            CreatePromptIconStroke(parent, new Vector2(thickness, size.y), position - new Vector2(halfWidth, 0f), 0f, color);
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
    }
}
