using Microsoft.EntityFrameworkCore;
using RensaioBackend.Data;
using RensaioBackend.Models.ContributionDatabase;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// The single decision point for retiring a contribution-database row that is no longer wanted.
///
/// A tombstone (<see cref="ContributionVersions.PendingDelete"/>) only carries meaning for a row
/// that EXISTS in the cloud: the tombstone is what teaches the cloud worker the row was deleted.
/// For a row that was created locally and never exported (<see cref="ContributionVersions.AddOrUpdate"/>)
/// there is nothing to propagate, so the correct action is a plain delete — otherwise the local
/// database accumulates tombstones the cloud never had, which is exactly the bloat that made
/// SeriesTitles 61% dead rows.
///
/// Transition table (current version → action):
///   Uploaded      (-2) → tombstone (-1)   // cloud copy exists; delete must propagate
///   AddOrUpdate   ( 0) → Remove           // never exported; no cloud copy to delete
///   PendingDelete (-1) → Keep             // delete already queued; MUST NOT be lost
///   UploadedDelete(-3) → Remove           // delete already exported; pure garbage
///
/// IMPORTANT: a pending-tombstone (-1) may be a cloud-backed row whose delete has not yet been
/// exported, so re-retiring it is a NO-OP — deleting it would silently cancel a delete the cloud
/// still needs to apply. Call sites additionally guard with <see cref="ContributionVersions.IsDeleted"/>
/// before calling <see cref="Retire"/>, so -1/-3 rows normally never reach it; the Keep branch
/// makes that safe even if one slips through.
/// </summary>
public static class ContributionRowLifecycle
{
    /// <summary>
    /// The action <see cref="Retire"/> will take for a row currently at <paramref name="version"/>.
    /// </summary>
    public enum RetirementAction
    {
        /// <summary>Set <c>Version = PendingDelete</c> so the delete propagates to the cloud.</summary>
        Tombstone,

        /// <summary>Hard-delete the row (no cloud copy to inform).</summary>
        Remove,

        /// <summary>Already a pending tombstone; leave it untouched so its queued delete survives.</summary>
        Keep,
    }

    /// <summary>
    /// Decides the retirement action from the row's CURRENT version. Pure — no side effects.
    /// </summary>
    public static RetirementAction Decide(int version) => version switch
    {
        ContributionVersions.Uploaded => RetirementAction.Tombstone,
        ContributionVersions.PendingDelete => RetirementAction.Keep,
        // 0 (AddOrUpdate) and the uploaded tombstone -3 (UploadedDelete) are both safe to delete:
        // 0 was never exported (no cloud copy), -3 is an exported delete (pure garbage).
        _ => RetirementAction.Remove,
    };

    /// <summary>
    /// Retires a row according to <see cref="Decide"/>. Safe to call on a detached,
    /// <c>AsNoTracking</c> instance: it is attached before mutation.
    /// </summary>
    /// <returns>The action taken.</returns>
    public static RetirementAction Retire<T>(ContributionDbContext db, T row)
        where T : class, IVersionedRow
    {
        var action = Decide(row.Version);

        switch (action)
        {
            case RetirementAction.Tombstone:
                db.Attach(row);
                row.Version = ContributionVersions.PendingDelete;
                db.Entry(row).Property(nameof(IVersionedRow.Version)).IsModified = true;
                break;

            case RetirementAction.Remove:
                db.Remove(row);
                break;

            case RetirementAction.Keep:
                break;
        }

        return action;
    }
}
