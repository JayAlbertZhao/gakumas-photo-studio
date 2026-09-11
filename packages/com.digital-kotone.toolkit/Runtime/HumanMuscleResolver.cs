using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    internal static class HumanMuscleResolver
    {
        public static int FindExact(params string[] candidates)
        {
            string[] names = HumanTrait.MuscleName;
            for (int index = 0; index < names.Length; index++)
            {
                string normalized = Normalize(names[index]);
                foreach (string candidate in candidates)
                {
                    if (normalized == Normalize(candidate)) return index;
                }
            }
            Debug.LogWarning("[Quartz] Humanoid muscle not found: candidates=" +
                string.Join(",", candidates));
            return -1;
        }

        public static int Find(string side, params string[] requiredTokens)
        {
            string sideToken = Normalize(side);
            string[] names = HumanTrait.MuscleName;
            for (int index = 0; index < names.Length; index++)
            {
                string normalized = Normalize(names[index]);
                if (!normalized.Contains(sideToken)) continue;
                bool matches = true;
                foreach (string token in requiredTokens)
                {
                    if (normalized.Contains(Normalize(token))) continue;
                    matches = false;
                    break;
                }
                if (matches) return index;
            }
            Debug.LogWarning(string.Format(
                "[Quartz] Humanoid muscle not found: side={0} tokens={1}",
                side, string.Join(",", requiredTokens)));
            return -1;
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            char[] buffer = new char[value.Length];
            int count = 0;
            foreach (char character in value)
            {
                if (!char.IsLetterOrDigit(character)) continue;
                buffer[count++] = char.ToLowerInvariant(character);
            }
            return new string(buffer, 0, count);
        }
    }
}
