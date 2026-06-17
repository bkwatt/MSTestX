using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using System;
using System.Collections.Generic;

namespace MSTestX.Console.Tests;

[TestClass]
public class ProgramTests
{
    [TestMethod]
    public void GetLaunchMode_UsesRemoteAdapter_WhenRemoteIpIsProvided()
    {
        var arguments = new Dictionary<string, string?>
        {
            ["remoteIp"] = "127.0.0.1:38300"
        };

        Assert.AreEqual(LaunchMode.RemoteAdapter, Program.GetLaunchMode(arguments));
    }

    [TestMethod]
    public void GetLaunchMode_UsesRemoteAdapter_WhenWaitForRemoteIsProvided()
    {
        var arguments = new Dictionary<string, string?>
        {
            ["waitForRemote"] = null
        };

        Assert.AreEqual(LaunchMode.RemoteAdapter, Program.GetLaunchMode(arguments));
    }

    [TestMethod]
    public void GetLaunchMode_PrefersRemoteAdapter_OverAppPath()
    {
        var arguments = new Dictionary<string, string?>
        {
            ["remoteIp"] = "127.0.0.1:38300",
            ["apppath"] = "/tmp/Test.app"
        };

        Assert.AreEqual(LaunchMode.RemoteAdapter, Program.GetLaunchMode(arguments));
    }

    [TestMethod]
    public void GetLaunchMode_UsesAppleApp_WhenAppPathIsProvidedWithoutRemoteArguments()
    {
        var arguments = new Dictionary<string, string?>
        {
            ["apppath"] = "/tmp/Test.app"
        };

        Assert.AreEqual(LaunchMode.AppleApp, Program.GetLaunchMode(arguments));
    }

    [TestMethod]
    public void GetLaunchMode_DefaultsToAndroidAdb_WhenNoRemoteOrAppleArgumentsAreProvided()
    {
        var arguments = new Dictionary<string, string?>();

        Assert.AreEqual(LaunchMode.AndroidAdb, Program.GetLaunchMode(arguments));
    }

    [TestMethod]
    public void FormatDuration_FormatsSubMillisecondDuration()
    {
        Assert.AreEqual("[< 1ms]", TimeSpan.Zero.FormatDuration());
    }

    [TestMethod]
    public void FormatDuration_FormatsSecondScaleDuration()
    {
        Assert.AreEqual("[1s 250ms]", TimeSpan.FromMilliseconds(1250).FormatDuration());
    }

    [TestMethod]
    public void GetRedirectedOutcomeLabel_UsesStableLabels()
    {
        Assert.AreEqual("PASS", TestRunner.GetRedirectedOutcomeLabel(TestOutcome.Passed));
        Assert.AreEqual("FAIL", TestRunner.GetRedirectedOutcomeLabel(TestOutcome.Failed));
        Assert.AreEqual("SKIP", TestRunner.GetRedirectedOutcomeLabel(TestOutcome.Skipped));
        Assert.AreEqual("NONE", TestRunner.GetRedirectedOutcomeLabel(TestOutcome.None));
        Assert.AreEqual("NOTFOUND", TestRunner.GetRedirectedOutcomeLabel(TestOutcome.NotFound));
    }

    [TestMethod]
    public void GetAppLogFilename_UsesSiblingLogFile()
    {
        Assert.AreEqual("/tmp/results/run.log", Program.GetAppLogFilename("/tmp/results/run.trx"));
    }

    [TestMethod]
    public void GetAppLogFilename_ReturnsNull_WhenTrxPathIsMissing()
    {
        Assert.IsNull(Program.GetAppLogFilename(null));
        Assert.IsNull(Program.GetAppLogFilename(""));
    }

    [TestMethod]
    public void MobileDevice_IsUnexpectedTunnelExit_ReturnsFalse_ForIntentionalShutdown()
    {
        Assert.IsFalse(MobileDevice.IsUnexpectedTunnelExit(isIntentionalShutdown: true));
        Assert.IsTrue(MobileDevice.IsUnexpectedTunnelExit(isIntentionalShutdown: false));
    }

    [TestMethod]
    public void MobileDevice_FormatTunnelExitMessage_LabelsIntentionalShutdown()
    {
        var message = MobileDevice.FormatTunnelExitMessage(
            exitCode: 0,
            lastMessage: "closed by test runner",
            isIntentionalShutdown: true);

        StringAssert.Contains(message, "stopped intentionally");
        StringAssert.Contains(message, "closed by test runner");
    }

    [TestMethod]
    public void MobileDevice_FormatTunnelExitMessage_LabelsUnexpectedExit()
    {
        var message = MobileDevice.FormatTunnelExitMessage(
            exitCode: 1,
            lastMessage: "lost connection",
            isIntentionalShutdown: false);

        StringAssert.Contains(message, "exited unexpectedly");
        StringAssert.Contains(message, "lost connection");
    }
}
