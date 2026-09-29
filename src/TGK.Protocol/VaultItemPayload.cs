using System.Text.Json;

namespace TGK.Protocol;

/// <summary>The plaintext of a sealed vault item: <see cref="Kind"/> is one of <see cref="ItemKinds"/>, <see cref="Data"/> the model.</summary>
public sealed record VaultItemPayload(string Kind, JsonElement Data);
