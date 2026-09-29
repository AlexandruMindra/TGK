using System.Runtime.CompilerServices;
using TGK.Protocol.Dtos;

namespace TGK.Server.Tests;

/// <summary>
/// Production KDF costs (600k PBKDF2 rounds on the client, 100k for the server verifier) only matter against offline
/// attacks; at test time they just make every register/login take seconds.
/// </summary>
internal static class FastCrypto
{
    public const int KdfIterations = 1_000;

    [ModuleInitializer]
    internal static void Init()
    {
        KdfParams.AcceptedMinIterations = KdfIterations;
        Store.VerifierIterations = 1_000;
    }
}
