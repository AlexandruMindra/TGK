using System;
using System.Collections.Generic;

namespace TGK.Protocol.Dtos;

/// <summary>A stored item. <see cref="Data"/> is the sealed payload, or null for a tombstone.</summary>
public sealed record VaultItem(string Id, long Revision, bool Deleted, byte[]? Data, DateTimeOffset UpdatedAt);

/// <summary>
/// <c>GET /api/vault?since=rev</c>: items (tombstones included) with a revision greater than <c>since</c>, oldest first.
/// A large result comes in pages: with <see cref="HasMore"/> set, <see cref="Revision"/> is the last item's and the
/// client asks again with <c>since</c> = that revision.
/// </summary>
public sealed record VaultPullResponse(long Revision, IReadOnlyList<VaultItem> Items, bool HasMore = false);

/// <summary>An upsert, or a delete when <see cref="Data"/> is null.</summary>
public sealed record VaultChange(string Id, byte[]? Data);

/// <summary><c>POST /api/vault</c>, applied last-write-wins in one transaction.</summary>
public sealed record VaultPushRequest(IReadOnlyList<VaultChange> Changes);

public sealed record AppliedChange(string Id, long Revision);

public sealed record VaultPushResponse(long Revision, IReadOnlyList<AppliedChange> Applied);
