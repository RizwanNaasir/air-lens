using AirLens.Core.Transport;
using Xunit;

namespace AirLens.Core.Tests;

public class AdbForwarderTests
{
    [Theory]
    [InlineData("adb server version (40) doesn't match this client (41); killing...", 40)]
    [InlineData("* daemon not running\nadb server version (41) doesn't match this client (40); killing...\n* daemon started successfully", 41)]
    public void ParsesTheRunningServerVersionFromTheMismatchMessage(string output, int expected)
    {
        Assert.Equal(expected, AdbForwarder.TryParseServerVersion(output));
    }

    [Fact]
    public void NoMismatchMeansNoVersion()
    {
        Assert.Null(AdbForwarder.TryParseServerVersion("List of devices attached\nXPH5T19301000834\tdevice"));
    }

    [Fact]
    public void ParsesTheDeviceList()
    {
        var devices = AdbForwarder.ParseDevices("XPH5T19301000834\tdevice\nemulator-5554\tunauthorized\n\n");
        Assert.Equal([("XPH5T19301000834", "device"), ("emulator-5554", "unauthorized")], devices);
        Assert.Empty(AdbForwarder.ParseDevices(""));
    }
}
