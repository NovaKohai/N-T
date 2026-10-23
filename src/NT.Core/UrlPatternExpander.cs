using System.Globalization;
using System.Text.RegularExpressions;

namespace NT.Core;

/// <summary>
/// Expands URL wildcard range patterns such as <c>[1-10]</c>, <c>[01-20]</c>, or <c>[a-e]</c> into discrete URLs.
/// Safe and bounded to avoid unbounded memory expansion.
/// </summary>
public static partial class UrlPatternExpander
{
    private const int DefaultMaxExpansion = 1000;

    [GeneratedRegex(@"\[([0-9]+)-([0-9]+)(?::([0-9]+))?\]|\[([a-zA-Z])-([a-zA-Z])(?::([0-9]+))?\]")]
    private static partial Regex PatternRegex();

    /// <summary>
    /// Checks if a URL contains at least one valid range pattern.
    /// </summary>
    public static bool HasPattern(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return PatternRegex().IsMatch(url);
    }

    /// <summary>
    /// Expands a URL with one or more range patterns into a list of URLs.
    /// Returns a single-item list with the original URL if no patterns exist.
    /// Throws <see cref="InvalidOperationException"/> if the expansion exceeds <paramref name="maxCount"/>.
    /// </summary>
    public static IReadOnlyList<string> Expand(string url, int maxCount = DefaultMaxExpansion)
    {
        if (string.IsNullOrWhiteSpace(url)) return [];
        var matches = PatternRegex().Matches(url);
        if (matches.Count == 0) return [url];

        // Collect replacements for each match position
        var segments = new List<List<string>>();
        var matchSpans = new List<(int Index, int Length)>();

        foreach (Match match in matches)
        {
            var replacementList = ExpandMatch(match);
            if (replacementList.Count == 0) continue;

            segments.Add(replacementList);
            matchSpans.Add((match.Index, match.Length));
        }

        if (segments.Count == 0) return [url];

        // Check total combination count before materializing
        long totalCombinations = 1;
        foreach (var s in segments)
        {
            totalCombinations *= s.Count;
            if (totalCombinations > maxCount)
            {
                throw new InvalidOperationException($"URL pattern expansion would generate {totalCombinations} items, exceeding the limit of {maxCount}.");
            }
        }

        // Cartesian product generation
        var results = new List<string>((int)totalCombinations);
        GenerateCombinations(url, matchSpans, segments, 0, 0, new System.Text.StringBuilder(), results);
        return results;
    }

    private static List<string> ExpandMatch(Match match)
    {
        var items = new List<string>();

        // Numeric range: groups 1 & 2
        if (match.Groups[1].Success && match.Groups[2].Success)
        {
            string startStr = match.Groups[1].Value;
            string endStr = match.Groups[2].Value;
            int step = match.Groups[3].Success && int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s > 0 ? s : 1;

            if (long.TryParse(startStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long start) &&
                long.TryParse(endStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long end))
            {
                // Check if zero-padded
                bool zeroPadded = (startStr.Length > 1 && startStr.StartsWith('0')) || (endStr.Length > 1 && endStr.StartsWith('0'));
                int width = Math.Max(startStr.Length, endStr.Length);
                string format = zeroPadded ? new string('0', width) : "";

                if (start <= end)
                {
                    for (long n = start; n <= end; n += step)
                        items.Add(zeroPadded ? n.ToString(format, CultureInfo.InvariantCulture) : n.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    for (long n = start; n >= end; n -= step)
                        items.Add(zeroPadded ? n.ToString(format, CultureInfo.InvariantCulture) : n.ToString(CultureInfo.InvariantCulture));
                }
            }
        }
        // Alpha range: groups 4 & 5
        else if (match.Groups[4].Success && match.Groups[5].Success)
        {
            char start = match.Groups[4].Value[0];
            char end = match.Groups[5].Value[0];
            int step = match.Groups[6].Success && int.TryParse(match.Groups[6].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s > 0 ? s : 1;

            if (char.IsLetter(start) && char.IsLetter(end) && (char.IsUpper(start) == char.IsUpper(end)))
            {
                if (start <= end)
                {
                    for (int c = start; c <= end; c += step)
                        items.Add(((char)c).ToString());
                }
                else
                {
                    for (int c = start; c >= end; c -= step)
                        items.Add(((char)c).ToString());
                }
            }
        }

        return items;
    }

    private static void GenerateCombinations(
        string template,
        List<(int Index, int Length)> spans,
        List<List<string>> replacements,
        int spanIndex,
        int templateOffset,
        System.Text.StringBuilder current,
        List<string> output)
    {
        if (spanIndex >= spans.Count)
        {
            current.Append(template.AsSpan(templateOffset));
            output.Add(current.ToString());
            return;
        }

        var (idx, len) = spans[spanIndex];
        current.Append(template.AsSpan(templateOffset, idx - templateOffset));
        int savedLength = current.Length;

        foreach (var repl in replacements[spanIndex])
        {
            current.Append(repl);
            GenerateCombinations(template, spans, replacements, spanIndex + 1, idx + len, current, output);
            current.Length = savedLength;
        }
    }
}
