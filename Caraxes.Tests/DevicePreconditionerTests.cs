using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Caraxes.Core.Cluster;
using Caraxes.Core.Scenario;

namespace Caraxes.Tests;

[TestFixture]
public class DevicePreconditionerTests
{
    [Test]
    public async Task WritesBallastRecordsItAndDeletesTheFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "caraxes-precond-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const long bytes = 16L * 1024 * 1024;
            DevicePreconditioner.Result r = await DevicePreconditioner.ForBytes(dir, bytes).RunAsync(CancellationToken.None);

            Assert.That(r.Bytes, Is.EqualTo(bytes));
            Assert.That(r.EndUtc, Is.GreaterThanOrEqualTo(r.StartUtc));
            Assert.That(r.MegabytesPerSecond, Is.GreaterThan(0));
            Assert.That(File.Exists(Path.Combine(dir, "precondition.bin")), Is.False, "ballast is deleted");
            Assert.That(File.Exists(DevicePreconditioner.RecordPath(dir)), Is.True, "record is written");

            DevicePreconditioner.Result? back = DevicePreconditioner.TryRead(dir);
            Assert.That(back, Is.Not.Null);
            Assert.That(back!.Bytes, Is.EqualTo(bytes));
            Assert.That(back.EndUtc, Is.EqualTo(r.EndUtc).Within(TimeSpan.FromMilliseconds(1)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void NoRecordMeansNotPreconditioned()
    {
        string dir = Path.Combine(Path.GetTempPath(), "caraxes-precond-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { Assert.That(DevicePreconditioner.TryRead(dir), Is.Null); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void ScenarioKeyParsesAndDefaultsToOff()
    {
        ScenarioSpec on = ScenarioSpecReader.Read("name: p\ncluster:\n  name: p-c\nworkload:\n  rows: 5000\nprecondition_device_gb: 48\n");
        Assert.That(on.PreconditionDeviceGb, Is.EqualTo(48));
        ScenarioSpec off = ScenarioSpecReader.Read("name: p\ncluster:\n  name: p-c\nworkload:\n  rows: 5000\n");
        Assert.That(off.PreconditionDeviceGb, Is.EqualTo(0));
    }
}
