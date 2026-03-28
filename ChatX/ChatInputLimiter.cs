using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ChatX
{
    internal static class ChatInputCharacterLimiter
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

        internal static void ApplyCharacterLimit()
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

        internal static IEnumerator ApplyWhenReady(ChatBehaviour chat)
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

        internal static void ResetCounter()
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
                Object.Destroy(counterObject);
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

        private static bool IsLive(Behaviour b) => b && b.isActiveAndEnabled && b.gameObject.activeInHierarchy;

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
}
