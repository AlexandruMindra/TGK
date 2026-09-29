using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Renci.SshNet;
using Renci.SshNet.Common;
using TGK.Core.Models;

namespace TGK.Core.Ssh;

/// <summary>
/// Opens a PTY shell that receives environment variables. SSH.NET has no public way to send "env" requests: its
/// <see cref="ShellStream"/> opens the channel, requests the PTY and starts the shell in one constructor. This does the
/// same through SSH.NET internals (checked against the pinned 2026.0.0) with the env requests between PTY and shell.
/// When the internals are not found (another SSH.NET version), <see cref="IsSupported"/> is false and callers open
/// the shell without the variables.
/// </summary>
internal static class EnvironmentShell
{
    private const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static readonly Internals? Members = Find();

    public static bool IsSupported => Members is not null;

    /// <summary>
    /// Like <see cref="SshClient.CreateShellStream(string, uint, uint, uint, uint, int)"/>, plus one env request per
    /// variable. Servers refuse names they don't accept (OpenSSH: only those listed in AcceptEnv); refusals are ignored.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not <see cref="IsSupported"/>.</exception>
    public static ShellStream Open(SshClient client, string term, uint cols, uint rows, uint width, uint height, int bufferSize, IReadOnlyList<EnvVar> environment)
    {
        Internals members = Members ?? throw new InvalidOperationException("SSH.NET internals for env requests were not found.");
        object session = members.Session.GetValue(client) ?? throw new SshConnectionException("Client not connected.");
        var shell = (ShellStream)Invoke(() => members.Constructor.Invoke([session, bufferSize, false]))!;
        try
        {
            object channel = members.Channel.GetValue(shell)!;
            Invoke(() => members.Open.Invoke(channel, null));
            if (!(bool)Invoke(() => members.PtyRequest.Invoke(channel, [term, cols, rows, width, height, null]))!)
                throw new SshException("The pseudo-terminal request was not accepted by the server. Consult the server log for more information.");
            foreach (EnvVar variable in environment)
                Invoke(() => members.EnvRequest.Invoke(channel, [variable.Name, variable.Value]));
            if (!(bool)Invoke(() => members.ShellRequest.Invoke(channel, null))!)
                throw new SshException("The request to start a shell was not accepted by the server. Consult the server log for more information.");
            return shell;
        }
        catch
        {
            shell.Dispose();
            throw;
        }
    }

    // Reflection wraps what the member throws; callers map SSH.NET's own exceptions.
    private static object? Invoke(Func<object?> call)
    {
        try
        {
            return call();
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
    }

    private static Internals? Find()
    {
        try
        {
            Assembly sshNet = typeof(SshClient).Assembly;
            Type? sessionType = sshNet.GetType("Renci.SshNet.ISession");
            Type? channelType = sshNet.GetType("Renci.SshNet.Channels.IChannelSession");
            if (sessionType is null || channelType is null)
            {
                CoreLog.Warn("This SSH.NET version lacks what environment variables need; they will not be sent.");
                return null;
            }
            PropertyInfo? session = typeof(BaseClient).GetProperty("Session", Internal);
            ConstructorInfo? constructor = typeof(ShellStream).GetConstructor(Internal, [sessionType, typeof(int), typeof(bool)]);
            FieldInfo? channel = typeof(ShellStream).GetField("_channel", Internal);
            MethodInfo? open = channelType.GetMethod("Open", Type.EmptyTypes);
            MethodInfo? pty = channelType.GetMethod("SendPseudoTerminalRequest",
                [typeof(string), typeof(uint), typeof(uint), typeof(uint), typeof(uint), typeof(IDictionary<TerminalModes, uint>)]);
            MethodInfo? env = channelType.GetMethod("SendEnvironmentVariableRequest", [typeof(string), typeof(string)]);
            MethodInfo? shell = channelType.GetMethod("SendShellRequest", Type.EmptyTypes);
            if (session is null || constructor is null || channel?.FieldType != channelType || open is null
                || pty?.ReturnType != typeof(bool) || env?.ReturnType != typeof(bool) || shell?.ReturnType != typeof(bool))
            {
                CoreLog.Warn("This SSH.NET version lacks what environment variables need; they will not be sent.");
                return null;
            }
            return new Internals(session, constructor, channel, open, pty, env, shell);
        }
        catch (Exception ex) when (ex is AmbiguousMatchException or TypeLoadException)
        {
            CoreLog.Warn($"Environment variables are not supported with this SSH.NET version: {ex.Message}");
            return null;
        }
    }

    private sealed record Internals(
        PropertyInfo Session,
        ConstructorInfo Constructor,
        FieldInfo Channel,
        MethodInfo Open,
        MethodInfo PtyRequest,
        MethodInfo EnvRequest,
        MethodInfo ShellRequest);
}
