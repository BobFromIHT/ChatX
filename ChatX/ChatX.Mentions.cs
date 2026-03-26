using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace ChatX
{
    public partial class ChatX
    {
        private static float _nextMentionPingAllowedAt; // Rate limiter for mention notification audio.
        private static readonly Dictionary<string, AudioClip> _mentionClipCache = new(StringComparer.OrdinalIgnoreCase);
        private static Type _cachedPlayerSoundType;
        private static FieldInfo[] _cachedPlayerSoundClipFields;
        private static Type _cachedButtonSoundType;
        private static FieldInfo _cachedButtonSoundHoverField;
        private static FieldInfo _cachedButtonSoundClickField;
        private static string _lastMissingMentionClipName;

        internal static void TryPlayMentionPingThrottled(float minInterval = 0.25f)

        {

            float now = Time.unscaledTime;

            if (now < _nextMentionPingAllowedAt) return;

            _nextMentionPingAllowedAt = now + minInterval;

            TryPlayMentionPing();

        }

        private static AudioSource ResolveMentionAudioSource()

        {

            var chat = Player._mainPlayer?._chatBehaviour;

            if (chat != null && chat.aSrc != null)

                return chat.aSrc;

            var current = ChatBehaviour._current;

            return current != null ? current.aSrc : null;

        }

        private static float GetMentionVolume() => Mathf.Clamp01(mentionPingVolume?.Value ?? 1f);

        private static FieldInfo[] GetPlayerSoundClipFields(object playerSound)

        {

            if (playerSound == null) return Array.Empty<FieldInfo>();

            var type = playerSound.GetType();

            if (_cachedPlayerSoundType == type && _cachedPlayerSoundClipFields != null)

                return _cachedPlayerSoundClipFields;

            _cachedPlayerSoundType = type;

            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var clipFields = new List<FieldInfo>(fields.Length);

            foreach (var field in fields)

            {

                if (field.FieldType == typeof(AudioClip))

                {

                    clipFields.Add(field);

                    continue;

                }

                if (field.FieldType.IsArray && field.FieldType.GetElementType() == typeof(AudioClip))

                    clipFields.Add(field);

            }

            _cachedPlayerSoundClipFields = clipFields.ToArray();

            return _cachedPlayerSoundClipFields;

        }

        private static void CacheButtonSoundFields(ButtonSound buttonSound)

        {

            if (buttonSound == null) return;

            var type = buttonSound.GetType();

            if (_cachedButtonSoundType == type) return;

            _cachedButtonSoundType = type;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

            _cachedButtonSoundHoverField = type.GetField("_hoverSoundClip", flags);

            _cachedButtonSoundClickField = type.GetField("_clickSoundClip", flags);

        }

        // Resolve mention audio by searching loaded clips, player fields, UI sounds, and Resources as a last resort.

        private static AudioClip FindAudioClipByName(string clipName)

        {

            AudioClip CacheHit(AudioClip clip)

            {

                if (clip != null)

                    _mentionClipCache[clipName] = clip;

                return clip;

            }

            if (string.IsNullOrWhiteSpace(clipName)) return null;

            clipName = clipName.Trim();

            if (_mentionClipCache.TryGetValue(clipName, out var cached))

            {

                if (cached != null)

                    return cached;

                _mentionClipCache.Remove(clipName);

            }

            try

            {

                foreach (var clip in Resources.FindObjectsOfTypeAll<AudioClip>())

                {

                    if (clip != null && clip.name.Equals(clipName, StringComparison.OrdinalIgnoreCase))

                        return CacheHit(clip);

                }

            }

            catch (Exception ex)

            {

                Log?.LogDebug($"ChatX mention clip search via Resources failed: {ex}");

            }

            var loaded = TryLoadFromResources(clipName);

            if (loaded != null)

                return CacheHit(loaded);

            var playerSound = Player._mainPlayer?._pSound;

            if (playerSound != null)

            {

                var fields = GetPlayerSoundClipFields(playerSound);

                foreach (var field in fields)

                {

                    if (field.FieldType == typeof(AudioClip))

                    {

                        if (field.GetValue(playerSound) is AudioClip single && single != null && single.name.Equals(clipName, StringComparison.OrdinalIgnoreCase))

                            return CacheHit(single);

                    }

                    else if (field.FieldType.IsArray && field.FieldType.GetElementType() == typeof(AudioClip))

                    {

                        if (field.GetValue(playerSound) is AudioClip[] many)

                        {

                            foreach (var clip in many)

                                if (clip != null && clip.name.Equals(clipName, StringComparison.OrdinalIgnoreCase))

                                    return CacheHit(clip);

                        }

                    }

                }

            }

            foreach (var src in Resources.FindObjectsOfTypeAll<AudioSource>())

            {

                if (src != null && src.clip != null && src.clip.name.Equals(clipName, StringComparison.OrdinalIgnoreCase))

                    return CacheHit(src.clip);

            }

            var buttonSounds = Resources.FindObjectsOfTypeAll<ButtonSound>();

            if (buttonSounds != null)

            {

                foreach (var btn in buttonSounds)

                {

                    if (btn == null) continue;

                    CacheButtonSoundFields(btn);

                    var hover = _cachedButtonSoundHoverField?.GetValue(btn) as AudioClip;

                    if (hover != null && hover.name.Equals(clipName, StringComparison.OrdinalIgnoreCase))

                        return CacheHit(hover);

                    var click = _cachedButtonSoundClickField?.GetValue(btn) as AudioClip;

                    if (click != null && click.name.Equals(clipName, StringComparison.OrdinalIgnoreCase))

                        return CacheHit(click);

                }

            }

            return null;

        }

        private static bool ClipMatches(AudioClip clip, string clipName)

        {

            if (clip == null || string.IsNullOrEmpty(clipName)) return false;

            return clip.name.Equals(clipName, StringComparison.OrdinalIgnoreCase);

        }

        private static AudioClip TryLoadFromResources(string clipName)

        {

            static AudioClip Attempt(string path)

            {

                try { return Resources.Load<AudioClip>(path); }

                catch (Exception ex)

                {

                    Log?.LogDebug($"ChatX Resources.Load failed for '{path}': {ex.Message}");

                    return null;

                }

            }

            AudioClip clip = Attempt(clipName);

            if (clip != null) return clip;

            if (clipName.StartsWith("_", StringComparison.Ordinal))

            {

                clip = Attempt(clipName.TrimStart('_'));

                if (clip != null) return clip;

            }

            var lower = clipName.ToLowerInvariant();

            clip = Attempt(lower);

            if (clip != null) return clip;

            if (lower.StartsWith("_", StringComparison.Ordinal))

            {

                clip = Attempt(lower.TrimStart('_'));

                if (clip != null) return clip;

            }

            return null;

        }

        internal static void ReloadMentionClip()

        {

            if (mentionPing == null || mentionPing.Value != true) return;

            var clipName = mentionPingClip?.Value?.Trim();

            if (string.IsNullOrEmpty(clipName)) return;

            if (_mentionAudioClip != null && ClipMatches(_mentionAudioClip, clipName))

                return;

            _mentionAudioClip = null;

            var clip = FindAudioClipByName(clipName);

            if (clip == null)

            {

                if (!string.Equals(_lastMissingMentionClipName, clipName, StringComparison.OrdinalIgnoreCase))

                    Log?.LogWarning($"ChatX mention clip '{clipName}' was not found among loaded audio clips.");

                _lastMissingMentionClipName = clipName;

                return;

            }

            _mentionAudioClip = clip;

            _mentionClipCache[clipName] = clip;

            _lastMissingMentionClipName = null;

            Log?.LogInfo($"ChatX mention clip set to '{_mentionAudioClip.name}'.");

        }

        internal static void ResetMentionRuntimeState()

        {

            _nextMentionPingAllowedAt = 0f;

            _mentionAudioClip = null;

            _mentionClipCache.Clear();

            _cachedPlayerSoundType = null;

            _cachedPlayerSoundClipFields = null;

            _cachedButtonSoundType = null;

            _cachedButtonSoundHoverField = null;

            _cachedButtonSoundClickField = null;

            _lastMissingMentionClipName = null;

        }

        private static void TryPlayMentionPing()

        {

            if (mentionPing?.Value != true) return;

            var source = ResolveMentionAudioSource();

            if (source == null) return;

            if (_mentionAudioClip == null)

            {

                ReloadMentionClip();

                if (_mentionAudioClip == null) return;

            }

            source.PlayOneShot(_mentionAudioClip, GetMentionVolume());

        }

        private static bool TryExtractSpeakerNickname(string message, out string nickname)

        {

            nickname = string.Empty;

            if (string.IsNullOrEmpty(message)) return false;

            const string namePrefix = "<color=#afeeee>[";

            const string nameSuffix = "]</color>";

            int startName = message.IndexOf(namePrefix, StringComparison.OrdinalIgnoreCase);

            if (startName < 0) return false;

            startName += namePrefix.Length;

            int endName = message.IndexOf(nameSuffix, startName, StringComparison.OrdinalIgnoreCase);

            if (endName < 0) return false;

            nickname = message[startName..endName].Trim();

            return nickname.Length > 0;

        }

        private static string GetLocalPlayerNickname()

        {

            try

            {

                var player = Player._mainPlayer;

                if (player == null) return string.Empty;

                var nickname = player.Network_nickname;

                return string.IsNullOrWhiteSpace(nickname) ? string.Empty : nickname.Trim();

            }

            catch

            {

                return string.Empty;

            }

        }

        private static bool ShouldPlayMentionPing(string message)

        {

            if (mentionPing?.Value != true) return false;

            var localName = GetLocalPlayerNickname();

            if (string.IsNullOrEmpty(localName)) return false;

            return TryExtractSpeakerNickname(message, out var speaker) &&

                   !speaker.Equals(localName, StringComparison.OrdinalIgnoreCase);

        }

        private static void ApplyMentionEffects(ref string message)

        {

            message = ApplyMentionUnderline(message, out bool mentionDetected);

            if (!mentionDetected) return;

            if (!ShouldPlayMentionPing(message)) return;

            TryPlayMentionPingThrottled(1f);

        }

        // -------------------------
        // Chat prefix & formatting utilities
        // -------------------------
        private static string NormalizeChannelColorToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return string.Empty;
            token = token.Trim();
            if (token.StartsWith("#", StringComparison.Ordinal) && token.Length >= 7)
                token = token[..7];
            return token.ToUpperInvariant();
        }
        private static bool TryExtractChatChannel(string message, out ChatBehaviour.ChatChannel channel)
        {
            channel = default;
            if (string.IsNullOrEmpty(message)) return false;
            const string marker = "</color>: <color=";
            int markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) return false;
            int colorStart = markerIndex + marker.Length;
            if (colorStart >= message.Length) return false;
            int colorEnd = message.IndexOf('>', colorStart);
            if (colorEnd < 0) return false;
            string colorToken = message[colorStart..colorEnd];
            string normalized = NormalizeChannelColorToken(colorToken);
            if (string.IsNullOrEmpty(normalized)) return false;
            switch (normalized)
            {
                case "YELLOW":
                case "#FFFF00":
                case "#FFD700":
                    channel = ChatBehaviour.ChatChannel.GLOBAL;
                    return true;
                case "#B2EC5D":
                    channel = ChatBehaviour.ChatChannel.PARTY;
                    return true;
                case "#FF8A90":
                    channel = ChatBehaviour.ChatChannel.ZONE;
                    return true;
                default:
                    return false;
            }
        }
        private static int SkipTimestampPrefix(string msg, int startIndex = 0)
        {
            if (string.IsNullOrEmpty(msg) || startIndex >= msg.Length) return startIndex;
            if (msg[startIndex] != '[') return startIndex;
            int i = startIndex + 1;
            // Expect HH:mm or h:mm (two digits for minutes, 1?2 digits for hour)
            // First hour digit
            if (i >= msg.Length || !char.IsDigit(msg[i])) return startIndex;
            i++;
            if (i < msg.Length && char.IsDigit(msg[i])) i++;
            // Colon
            if (i >= msg.Length || msg[i] != ':') return startIndex;
            i++;
            // Two minute digits
            if (i + 1 >= msg.Length || !char.IsDigit(msg[i]) || !char.IsDigit(msg[i + 1])) return startIndex;
            i += 2;
            // Optional space + AM/PM (case-insensitive)
            if (i < msg.Length && msg[i] == ' ')
            {
                if (i + 2 < msg.Length &&
                    (msg[i + 1] == 'A' || msg[i + 1] == 'a' || msg[i + 1] == 'P' || msg[i + 1] == 'p') &&
                    (msg[i + 2] == 'M' || msg[i + 2] == 'm'))
                {
                    i += 3;
                }
                else
                {
                    return startIndex;
                }
            }
            // Closing bracket
            if (i >= msg.Length || msg[i] != ']') return startIndex;
            i++;
            // Optional trailing space after the timestamp
            if (i < msg.Length && msg[i] == ' ') i++;
            return i;
        }
        // Map channel enum -> colored (G)/(P)/(Z)
        public static string BuildColoredPrefixFromEnum(ChatBehaviour.ChatChannel ch) => ch switch
        {
            ChatBehaviour.ChatChannel.GLOBAL => "<color=yellow>(G)</color>",
            ChatBehaviour.ChatChannel.PARTY => "<color=#B2EC5D>(P)</color>",
            ChatBehaviour.ChatChannel.ZONE => "<color=#FF8A90>(Z)</color>",
            _ => string.Empty
        };
        public static string BuildMessagePrefix(ChatBehaviour.ChatChannel ch)
        {
            var parts = new List<string>();
            if (chatTimestamp?.Value == true)
            {
                string fmt = timestampFormat.Value ? "HH:mm" : "h:mm tt";
                string time = DateTime.Now.ToString(fmt, CultureInfo.InvariantCulture);
                parts.Add($"[{time}]");
            }
            var channelPrefix = BuildColoredPrefixFromEnum(ch);
            if (!string.IsNullOrEmpty(channelPrefix))
                parts.Add(channelPrefix);
            return parts.Count == 0 ? string.Empty : string.Join(" ", parts) + " ";
        }
        public static string BuildOocBadge()
        {
            return "<color=yellow>[</color><color=#A7FC00>OOC</color><color=yellow>]</color>";
        }
        public static string ExtractRenderedOocBadge(ref string message)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;
            const string bodyMarker = "</color>: <color=";
            int markerIndex = message.IndexOf(bodyMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) return string.Empty;
            int bodyColorStart = markerIndex + bodyMarker.Length;
            int bodyOpenTagEnd = message.IndexOf('>', bodyColorStart);
            if (bodyOpenTagEnd < 0) return string.Empty;
            int bodyStart = bodyOpenTagEnd + 1;
            if (message.Length - bodyStart < OocPrefix.Length) return string.Empty;
            if (string.CompareOrdinal(message, bodyStart, OocPrefix, 0, OocPrefix.Length) != 0)
                return string.Empty;
            message = message.Remove(bodyStart, OocPrefix.Length);
            return BuildOocBadge();
        }
        public static string CombinePrefixSegments(string prefix, string badge)
        {
            bool hasPrefix = !string.IsNullOrWhiteSpace(prefix);
            bool hasBadge = !string.IsNullOrWhiteSpace(badge);
            if (!hasPrefix && !hasBadge) return string.Empty;
            prefix = hasPrefix ? prefix.TrimEnd() : string.Empty;
            badge = hasBadge ? badge.Trim() : string.Empty;
            if (!hasPrefix) return badge + " ";
            if (!hasBadge) return prefix.EndsWith(" ", StringComparison.Ordinal) ? prefix : prefix + " ";
            return prefix + " " + badge + " ";
        }
        // Detect either plain "(G) " / "(P) " / "(Z) " or colored "<color=...>(G|P|Z)</color> "
        public static bool LooksPrefixed(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return false;
            int i = SkipTimestampPrefix(msg);
            if (msg.Length - i >= 4)
            {
                var head = msg.Substring(i, 4);
                if (head == "(G) " || head == "(P) " || head == "(Z) ") return true;
            }
            if (i < msg.Length && msg.IndexOf("<color", i, StringComparison.OrdinalIgnoreCase) == i)
            {
                int gt = msg.IndexOf('>', i);
                if (gt >= 0)
                {
                    int endColor = msg.IndexOf("</color>", gt + 1, StringComparison.OrdinalIgnoreCase);
                    if (endColor > gt + 1)
                    {
                        string inner = msg[(gt + 1)..endColor];
                        if (inner.StartsWith("(G)") || inner.StartsWith("(P)") || inner.StartsWith("(Z)"))
                            return true;
                    }
                }
            }
            return false;
        }
        public static string InsertPrefix(string line, string prefix)
        {
            if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(line)) return line;
            return prefix + line;
        }
        public static string ApplyAsteriskFormatting(string s)
        {
            if (asteriskItalic?.Value != true || string.IsNullOrEmpty(s) || s.IndexOf('*') < 0)
                return s;
            var sb = new StringBuilder(s.Length + 16);
            var span = s.AsSpan();
            bool ital = false;        // are we inside <i>...</i> ?
            bool bold = false;        // are we inside <b>...</b> ?
            bool inTag = false;       // are we inside <...> rich text tag?
            int italOpenAt = -1;      // insertion index of last "<i>" (for unmatched revert)
            int boldOpenAt = -1;      // insertion index of last "<b>" (for unmatched revert)
            for (int i = 0; i < span.Length; i++)
            {
                char c = span[i];
                // Track rich-text tags to avoid toggling inside <...>
                if (c == '<') { inTag = true; sb.Append(c); continue; }
                if (c == '>' && inTag) { inTag = false; sb.Append(c); continue; }
                if (c == '\\' && i + 1 < span.Length && span[i + 1] == '*') { sb.Append('*'); i++; continue; }
                if (c == '*' && !inTag)
                {
                    // Count run length
                    int j = i + 1;
                    while (j < span.Length && span[j] == '*') j++;
                    int run = j - i;
                    if (run >= 2)
                    {
                        int remaining = run;
                        while (remaining >= 2)
                        {
                            if (bold) { sb.Append("</b>"); bold = false; boldOpenAt = -1; }
                            else { boldOpenAt = sb.Length; sb.Append("<b>"); bold = true; }
                            remaining -= 2;
                        }
                        if (remaining == 1)
                        {
                            if (ital) { sb.Append("</i>"); ital = false; italOpenAt = -1; }
                            else { italOpenAt = sb.Length; sb.Append("<i>"); ital = true; }
                        }
                    }
                    else
                    {
                        if (ital) { sb.Append("</i>"); ital = false; italOpenAt = -1; }
                        else { italOpenAt = sb.Length; sb.Append("<i>"); ital = true; }
                    }
                    i = j - 1;
                    continue;
                }
                sb.Append(c);
            }
            // If we ended mid-italic, revert the last "<i>" to a literal '*'
            if (bold && boldOpenAt >= 0)
            {
                sb.Remove(boldOpenAt, 3).Insert(boldOpenAt, "**");
                if (italOpenAt > boldOpenAt)
                    italOpenAt += 2 - 3; // adjust for length change
            }
            if (ital && italOpenAt >= 0)
            {
                sb.Remove(italOpenAt, 3).Insert(italOpenAt, "*");
            }
            return sb.ToString();
        }
        static bool IsAsciiLetter(char c)
            => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
        static bool HasLetterNeighbor(string s, int start, int len, int leftBound)
        {
            // leftBound = where the chat body starts; don't look left of it
            int leftIdx = (start > leftBound) ? start - 1 : -1;
            int rightIdx = (start + len < s.Length) ? start + len : -1;
            char? leftCh = (leftIdx >= 0) ? s[leftIdx] : (char?)null;
            char? rightCh = (rightIdx >= 0) ? s[rightIdx] : (char?)null;
            bool leftIsLetter = leftCh.HasValue && IsAsciiLetter(leftCh.Value);
            bool rightIsLetter = rightCh.HasValue && IsAsciiLetter(rightCh.Value);
            bool result = leftIsLetter || rightIsLetter;
            return result;
        }
        private static string ApplyMentionUnderline(string message, out bool mentionDetected)
        {
            // Walk the rendered string, underlining safe occurrences of the player's nickname without disturbing existing tags.
            mentionDetected = false;
            if (mentionUnderline?.Value != true) return message;
            if (string.IsNullOrEmpty(message)) return message;
            var nick = GetLocalPlayerNickname();
            if (string.IsNullOrEmpty(nick)) return message;
            // Start scanning after the speaker header if present
            const string bodyMarker = "</color>: ";
            int bodyStart = message.IndexOf(bodyMarker, StringComparison.OrdinalIgnoreCase);
            bodyStart = (bodyStart >= 0) ? bodyStart + bodyMarker.Length : 0;
            var sb = new StringBuilder(message.Length + 16);
            if (bodyStart > 0) sb.Append(message, 0, bodyStart);
            int i = bodyStart;
            while (i < message.Length)
            {
                int hit = message.IndexOf(nick, i, StringComparison.OrdinalIgnoreCase);
                if (hit < 0)
                {
                    sb.Append(message, i, message.Length - i);
                    break;
                }
                // copy text before the hit
                sb.Append(message, i, hit - i);
                // reject if glued to letters (e.g., "Bobby")
                if (HasLetterNeighbor(message, hit, nick.Length, bodyStart))
                {
                    sb.Append(message[hit]);
                    i = hit + 1;
                    continue;
                }
                // cheap "already underlined" guard
                int open = message.LastIndexOf("<u>", hit, StringComparison.OrdinalIgnoreCase);
                if (open >= 0)
                {
                    int close = message.IndexOf("</u>", open, StringComparison.OrdinalIgnoreCase);
                    if (close >= 0 && close >= hit + nick.Length)
                    {
                        sb.Append(message, hit, nick.Length);
                        i = hit + nick.Length;
                        continue;
                    }
                }
                // underline this occurrence
                sb.Append("<u>");
                sb.Append(message, hit, nick.Length);
                sb.Append("</u>");
                mentionDetected = true;
                i = hit + nick.Length;
            }
            return sb.ToString();
        }
    }
}
