using System;

namespace Mihon.ExtensionsBridge.Models.Helpers;

/// <summary>
/// Pure, IKVM-free deep-clone helpers for the shared model types.
///
/// Mirrors <c>Mihon.ExtensionsBridge.Core.Extensions.CloneExtensions</c> so the
/// backend can keep taking defensive copies of repository metadata after the
/// Core reference is removed. Kept in a distinct namespace to avoid ambiguity
/// while both assemblies are still referenced during the migration.
/// </summary>
public static class CloneModelExtensions
{
    public static TachiyomiSource Clone(this TachiyomiSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new TachiyomiSource
        {
            Id = source.Id,
            Name = source.Name,
            Language = source.Language,
            BaseUrl = source.BaseUrl,
            VersionId = source.VersionId,
            MirrorUrls = [.. source.MirrorUrls],
        };
    }

    public static TachiyomiExtension Clone(this TachiyomiExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return new TachiyomiExtension
        {
            Name = extension.Name,
            Language = extension.Language,
            Package = extension.Package,
            Apk = extension.Apk,
            VersionCode = extension.VersionCode,
            Version = extension.Version,
            Nsfw = extension.Nsfw,
            Mixed = extension.Mixed,
            Icon = extension.Icon,
            ExtensionLib = extension.ExtensionLib,
            Jar = extension.Jar,
            Sources = extension.Sources.ConvertAll(source => source.Clone()),
        };
    }

    public static TachiyomiRepository Clone(this TachiyomiRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return new TachiyomiRepository(repository.Url)
        {
            Id = repository.Id,
            LastUpdatedUTC = repository.LastUpdatedUTC,
            Fingerprint = repository.Fingerprint,
            Name = repository.Name,
            WebSite = repository.WebSite,
            Url = repository.Url,
            Version = repository.Version,
            Extensions = repository.Extensions.ConvertAll(extension => extension.Clone()),
        };
    }

    public static RepositoryEntry Clone(this RepositoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new RepositoryEntry
        {
            RepositoryId = entry.RepositoryId,
            IsLocal = entry.IsLocal,
            Name = entry.Name,
            Extension = entry.Extension.Clone(),
            DownloadUrl = entry.DownloadUrl,
            DownloadUTC = entry.DownloadUTC,
            Apk = entry.Apk.Clone(),
            Jar = entry.Jar.Clone(),
            Icon = entry.Icon.Clone(),
            ClassName = entry.ClassName,
        };
    }

    public static RepositoryGroup Clone(this RepositoryGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return new RepositoryGroup
        {
            Name = group.Name,
            Entries = group.Entries.ConvertAll(entry => entry.Clone()),
            ActiveEntry = group.ActiveEntry,
            AutoUpdate = group.AutoUpdate,
        };
    }

    public static FileHash Clone(this FileHash fileHash)
    {
        ArgumentNullException.ThrowIfNull(fileHash);
        return new FileHash
        {
            FileName = fileHash.FileName,
            SHA256 = fileHash.SHA256,
        };
    }

    public static FileHashVersion Clone(this FileHashVersion fileHashVersion)
    {
        ArgumentNullException.ThrowIfNull(fileHashVersion);
        return new FileHashVersion
        {
            FileName = fileHashVersion.FileName,
            SHA256 = fileHashVersion.SHA256,
            Version = fileHashVersion.Version,
        };
    }
}
