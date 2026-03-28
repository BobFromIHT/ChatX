using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ChatX
{
    internal sealed class ChatInputHandle
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

    internal static class ChatInputResolver
    {
        private static FieldInfo _chatInputField;
        private static bool _chatInputFieldResolved;
        private static bool _loggedMissingChatInputField;

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

        internal static void ResetRuntimeState()
        {
            _chatInputField = null;
            _chatInputFieldResolved = false;
            _loggedMissingChatInputField = false;
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
                ChatX.Log?.LogWarning("ChatX could not resolve ChatBehaviourAssets._chatInput. Chat input-dependent features will stay disabled.");
            }

            return _chatInputField;
        }

        private static bool IsSupportedInputType(Type type)
        {
            return typeof(InputField).IsAssignableFrom(type)
                || typeof(TMP_InputField).IsAssignableFrom(type);
        }
    }

    public partial class ChatX
    {
        private static bool IsChatSubmitKeyPressed()
        {
            return Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
        }

        internal static Transform FindDescendantTransformByName(Transform root, string name)
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

        internal static T FindDescendantComponentByName<T>(Transform root, string name) where T : Component
        {
            var transform = FindDescendantTransformByName(root, name);
            return transform != null ? transform.GetComponent<T>() : null;
        }
    }
}
