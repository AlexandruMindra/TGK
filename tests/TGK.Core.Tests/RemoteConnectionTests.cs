using System;
using System.Reflection;
using Renci.SshNet;
using Xunit;

namespace TGK.Core.Tests;

public sealed class RemoteConnectionTests
{
    // RemoteConnection ends an empty input on the command's channel itself (SSH.NET only does so after data): what it
    // reaches for must still be there after an SSH.NET upgrade. The end-to-end tests check that it works.
    [Fact]
    public void EmptyInput_CanStillBeEnded_WithThisSshNet()
    {
        FieldInfo? channel = typeof(SshCommand).GetField("_channel", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(channel);
        Type? session = typeof(SshCommand).Assembly.GetType("Renci.SshNet.Channels.ChannelSession");
        Assert.NotNull(session);
        Assert.True(channel.FieldType.IsAssignableFrom(session));
        Assert.NotNull(session.GetMethod("SendEof", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes));
    }
}
