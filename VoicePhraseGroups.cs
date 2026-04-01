using System;
using System.Collections.Generic;
using System.Linq;

namespace MusicPlayer
{
    public sealed class VoicePhraseGroup
    {
        public string CanonicalPhrase { get; }
        public IReadOnlyList<string> Aliases { get; }

        public VoicePhraseGroup(string canonicalPhrase, IEnumerable<string> aliases)
        {
            canonicalPhrase = VoiceTriggerService.NormalizePhrase(canonicalPhrase);

            if (string.IsNullOrWhiteSpace(canonicalPhrase))
                throw new ArgumentException("Canonical phrase cannot be blank.", nameof(canonicalPhrase));

            var normalizedAliases = aliases
                .Select(VoiceTriggerService.NormalizePhrase)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!normalizedAliases.Contains(canonicalPhrase, StringComparer.OrdinalIgnoreCase))
                normalizedAliases.Insert(0, canonicalPhrase);

            CanonicalPhrase = canonicalPhrase;
            Aliases = normalizedAliases;
        }
    }

    public static class VoicePhraseGroups
    {
        public static List<string> FlattenAliases(IEnumerable<VoicePhraseGroup> groups)
        {
            return groups
                .SelectMany(g => g.Aliases)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static Dictionary<string, string> BuildAliasToCanonicalMap(IEnumerable<VoicePhraseGroup> groups)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                foreach (var alias in group.Aliases)
                    map[alias] = group.CanonicalPhrase;
            }

            return map;
        }
    }
}