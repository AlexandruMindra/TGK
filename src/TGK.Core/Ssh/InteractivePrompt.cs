using System.Threading;
using System.Threading.Tasks;

namespace TGK.Core.Ssh;

/// <summary>
/// A question the server asks during keyboard-interactive sign-in that the stored credentials can't answer,
/// e.g. a one-time code, or a password when only a key was configured.
/// </summary>
/// <param name="Instruction">The server's explanation; often empty.</param>
/// <param name="Prompt">The question itself, e.g. <c>Verification code: </c>.</param>
/// <param name="Echo">False for secrets: the answer should be masked while it is typed.</param>
public sealed record InteractivePrompt(string Host, int Port, string Username, string Instruction, string Prompt, bool Echo);

/// <summary>
/// Shows <paramref name="prompt"/> to the user and returns the answer, or null when the user declines (the connection
/// attempt is then cancelled). Called on an SSH worker thread (never the UI thread) while the connection waits;
/// <paramref name="ct"/> is cancelled when the attempt is abandoned or the server would stop waiting, so an open
/// prompt should close.
/// </summary>
public delegate Task<string?> InteractivePromptHandler(InteractivePrompt prompt, CancellationToken ct);
