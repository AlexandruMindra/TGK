namespace TGK.Protocol.Dtos;

/// <summary>Password KDF parameters, chosen by the client at registration and stored opaquely by the server.</summary>
public sealed record KdfParams(string Algorithm, int Iterations)
{
    public const string Pbkdf2Sha256 = "pbkdf2-sha256";
    public const int DefaultIterations = 600_000;

    /// <summary>Lowest iteration count either side accepts from the other (a weak KDF would expose the password).</summary>
    public const int MinIterations = 100_000;

    public const int MaxIterations = 10_000_000;

    /// <summary>The floor <see cref="IsAcceptable"/> enforces; only test assemblies lower it, to keep key derivation cheap.</summary>
    public static int AcceptedMinIterations { get; internal set; } = MinIterations;

    public static KdfParams Default { get; } = new(Pbkdf2Sha256, DefaultIterations);

    /// <summary>True for a supported algorithm with an iteration count in [<see cref="AcceptedMinIterations"/>, <see cref="MaxIterations"/>].</summary>
    public bool IsAcceptable() => Algorithm == Pbkdf2Sha256 && Iterations >= AcceptedMinIterations && Iterations <= MaxIterations;
}
