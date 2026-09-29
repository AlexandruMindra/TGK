using System.Runtime.CompilerServices;
using TGK.Protocol.Dtos;

namespace TGK.Core.Tests;

/// <summary>Production KDF costs only matter against offline attacks; at test time they just make every login take seconds.</summary>
internal static class FastCrypto
{
    public const int KdfIterations = 1_000;

    [ModuleInitializer]
    internal static void Init() => KdfParams.AcceptedMinIterations = KdfIterations;
}
