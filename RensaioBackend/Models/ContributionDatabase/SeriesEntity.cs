namespace RensaioBackend.Models.ContributionDatabase
{
    /// <summary>
    /// A series aggregate in the local contributor database. A series links a set of
    /// normalized titles (via <see cref="SeriesTitleEntity"/>) and gathers source entries
    /// (<see cref="ContributionSeriesEntity"/>) and metadata provider links
    /// (<see cref="ContributionMetadataEntity"/>) under one identity.
    /// </summary>
    public class SeriesEntity
    {
        /// <summary>BLOB(16) primary key (stored as a true 16-byte binary Guid).</summary>
        public Guid Id { get; set; } = Guid.NewGuid();
    }
}