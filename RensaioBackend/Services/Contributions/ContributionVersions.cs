namespace RensaioBackend.Services.Contributions;

/// <summary>
/// Single source of truth for the contribution-database replication Version semantics.
///
/// The Version column is a replication cursor, NOT a state discriminator:
///   0  = AddOrUpdate  (pending)     — a live row, not yet exported.
///  -1  = PendingDelete             — a tombstone, not yet exported.
///  -2  = Uploaded AddOrUpdate       — a live row, already exported.
///  -3  = Uploaded Delete            — a tombstone, already exported.
///
/// The ONLY difference between -2/-3 and 0/-1 is "do not re-export". Functionally the
/// uploaded variants MUST behave identically to their pending counterparts: a -2 row is
/// live exactly like a 0 row, and a -3 row is deleted exactly like a -1 row. Any predicate
/// that compares against the raw 0/-1 literals (e.g. `Version != -1` as a "not tombstone"
/// test) is a bug: it treats -2/-3 as an unknown third state.
///
/// Use these helpers instead of raw literals so the two uploaded variants can never drift.
/// </summary>
public static class ContributionVersions
{
    /// <summary>Pending add/update (live, not yet exported).</summary>
    public const int AddOrUpdate = 0;

    /// <summary>Pending logical delete (tombstone, not yet exported).</summary>
    public const int PendingDelete = -1;

    /// <summary>Uploaded add/update (live, already exported).</summary>
    public const int Uploaded = -2;

    /// <summary>Uploaded logical delete (tombstone, already exported).</summary>
    public const int UploadedDelete = -3;

    /// <summary>
    /// True for a row that must be treated as existing/live: 0 (pending) or -2 (uploaded).
    /// False only for the two tombstones (-1, -3).
    /// </summary>
    public static bool IsLive(int version) => version != PendingDelete && version != UploadedDelete;

    /// <summary>True for a tombstoned row: -1 (pending) or -3 (uploaded).</summary>
    public static bool IsDeleted(int version) => version == PendingDelete || version == UploadedDelete;

    /// <summary>
    /// True for a row with un-exported changes: 0 (add/update) or -1 (delete). These are
    /// exactly the rows an export must include.
    /// </summary>
    public static bool IsPending(int version) => version == AddOrUpdate || version == PendingDelete;

    /// <summary>
    /// Maps a pending version to its uploaded counterpart: 0 → -2, -1 → -3. Rows already
    /// uploaded (-2/-3) are returned unchanged.
    /// </summary>
    public static int ToUploaded(int version) => version switch
    {
        AddOrUpdate => Uploaded,
        PendingDelete => UploadedDelete,
        _ => version,
    };
}
