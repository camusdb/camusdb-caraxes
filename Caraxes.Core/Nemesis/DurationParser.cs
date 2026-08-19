/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Globalization;

namespace Caraxes.Core.Nemesis;

/// <summary>
/// Parses the compact duration strings the scenario YAML uses for nemesis timing (<c>15s</c>,
/// <c>1m</c>, <c>250ms</c>, <c>1h</c>), matching <c>CamusDB.Workload</c>'s own parser so a scenario
/// reads consistently across its workload and nemesis blocks. A bare number is seconds.
/// </summary>
public static class DurationParser
{
    public static TimeSpan Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException("duration is empty");

        string s = value.Trim();

        (string suffix, Func<double, TimeSpan> factory)[] units =
        [
            ("ms", ms => TimeSpan.FromMilliseconds(ms)),
            ("s", n => TimeSpan.FromSeconds(n)),
            ("m", n => TimeSpan.FromMinutes(n)),
            ("h", n => TimeSpan.FromHours(n)),
        ];

        foreach ((string suffix, Func<double, TimeSpan> factory) in units)
        {
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                string number = s[..^suffix.Length];
                if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                    return factory(parsed);
                throw new FormatException($"invalid duration '{value}'");
            }
        }

        // Bare number = seconds.
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            return TimeSpan.FromSeconds(seconds);

        throw new FormatException($"invalid duration '{value}' (use forms like 15s, 1m, 250ms, 1h)");
    }
}
