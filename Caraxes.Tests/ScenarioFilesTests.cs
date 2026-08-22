/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
using Caraxes.Core.Scenario;

namespace Caraxes.Tests;

/// <summary>
/// Every checked-in scenario file must parse and validate. A scenario with a typo'd key or an
/// invalid nemesis block otherwise fails only at run time, after the operator has already waited
/// for an image build and a cluster boot — this catches it at test time instead.
/// </summary>
[TestFixture]
public sealed class ScenarioFilesTests
{
    [Test]
    public void EveryScenarioFileParsesAndValidates()
    {
        string dir = FindScenariosDirectory();
        string[] files = Directory.GetFiles(dir, "*.yml");
        Assert.That(files, Is.Not.Empty, $"no scenario files found under {dir}");

        int validated = 0;
        foreach (string file in files)
        {
            string yml = File.ReadAllText(file);

            // The directory also holds matrix sweeps (top-level 'axes' block) and bare cluster
            // specs (no top-level 'cluster' block); both have their own readers and schemas, and
            // the scenario reader rejects their keys by design. Select by shape, not by filename,
            // so a renamed file cannot silently skip validation.
            bool isScenario = HasTopLevelKey(yml, "cluster") && !HasTopLevelKey(yml, "axes");
            if (!isScenario)
                continue;

            Assert.DoesNotThrow(
                () => ScenarioSpecReader.Read(yml),
                $"scenario file '{Path.GetFileName(file)}' does not parse/validate");
            validated++;
        }

        Assert.That(validated, Is.GreaterThan(0), "no scenario-shaped files were validated");
    }

    private static bool HasTopLevelKey(string yml, string key)
    {
        foreach (string line in yml.Split('\n'))
        {
            if (line.StartsWith(key + ":", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string FindScenariosDirectory()
    {
        DirectoryInfo? probe = new(AppContext.BaseDirectory);
        while (probe is not null)
        {
            string candidate = Path.Combine(probe.FullName, "scenarios");
            if (Directory.Exists(candidate))
                return candidate;
            probe = probe.Parent;
        }

        throw new DirectoryNotFoundException(
            $"could not locate a 'scenarios' directory above {AppContext.BaseDirectory}");
    }
}
