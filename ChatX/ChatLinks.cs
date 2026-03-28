using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ChatX
{
    public partial class ChatX
    {
        private const string ChatLinkOverlayName = "ChatX_LinkClickOverlay";
        private static readonly Regex UrlRegex = new(@"\b(?:https?://|www\.)[^\s<]+", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private sealed class ProtectedChatLink
        {
            public string Token;
            public string DisplayText;
            public string CanonicalUrl;
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

        private static void SetLayerRecursive(GameObject root, int layer)
        {
            if (root == null)
                return;

            root.layer = layer;
            foreach (Transform child in root.transform)
                SetLayerRecursive(child.gameObject, layer);
        }
    }
}
