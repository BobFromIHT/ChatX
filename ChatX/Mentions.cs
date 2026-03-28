using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace ChatX
{
    public partial class ChatX
    {
        internal static AudioClip _mentionAudioClip;
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

        private static bool ClipMatches(AudioClip clip, string clipName)
        {
            if (clip == null || string.IsNullOrEmpty(clipName)) return false;
            return clip.name.Equals(clipName, StringComparison.OrdinalIgnoreCase);
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
                    if (ClipMatches(clip, clipName))
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
                        if (field.GetValue(playerSound) is AudioClip single && ClipMatches(single, clipName))
                            return CacheHit(single);
                    }
                    else if (field.FieldType.IsArray && field.FieldType.GetElementType() == typeof(AudioClip))
                    {
                        if (field.GetValue(playerSound) is AudioClip[] many)
                        {
                            foreach (var clip in many)
                                if (ClipMatches(clip, clipName))
                                    return CacheHit(clip);
                        }
                    }
                }
            }

            foreach (var src in Resources.FindObjectsOfTypeAll<AudioSource>())
            {
                if (src != null && ClipMatches(src.clip, clipName))
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
                    if (ClipMatches(hover, clipName))
                        return CacheHit(hover);

                    var click = _cachedButtonSoundClickField?.GetValue(btn) as AudioClip;
                    if (ClipMatches(click, clipName))
                        return CacheHit(click);
                }
            }

            return null;
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

        private static bool IsAsciiLetter(char c)
            => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        private static bool HasLetterNeighbor(string s, int start, int len, int leftBound)
        {
            // leftBound = where the chat body starts; don't look left of it
            int leftIdx = (start > leftBound) ? start - 1 : -1;
            int rightIdx = (start + len < s.Length) ? start + len : -1;
            char? leftCh = (leftIdx >= 0) ? s[leftIdx] : (char?)null;
            char? rightCh = (rightIdx >= 0) ? s[rightIdx] : (char?)null;
            bool leftIsLetter = leftCh.HasValue && IsAsciiLetter(leftCh.Value);
            bool rightIsLetter = rightCh.HasValue && IsAsciiLetter(rightCh.Value);
            return leftIsLetter || rightIsLetter;
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
