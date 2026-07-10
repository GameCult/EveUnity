using System;
using System.Collections.Generic;
using GameCult.Caching;
using MessagePack;

#nullable enable

namespace GameCult.Eve.Surface
{
    [CultDocument("gamecult.eve.asset_catalog", SchemaId)]
    [MessagePackObject]
    public sealed class EveAssetCatalogDocument
    {
        public const string SchemaId = "gamecult.eve.asset_catalog.v1";

        [SerializationConstructor]
        public EveAssetCatalogDocument(
            string schema,
            string providerId,
            string catalogId,
            long version,
            string updatedAtUtc,
            EveAssetCatalogEntry[] assets)
        {
            Schema = string.IsNullOrWhiteSpace(schema) ? SchemaId : schema;
            ProviderId = providerId ?? "";
            CatalogId = catalogId ?? "";
            Version = version;
            UpdatedAtUtc = updatedAtUtc ?? "";
            Assets = assets ?? Array.Empty<EveAssetCatalogEntry>();
        }

        public EveAssetCatalogDocument(
            string providerId,
            string catalogId,
            long version,
            string updatedAtUtc,
            EveAssetCatalogEntry[] assets)
            : this(SchemaId, providerId, catalogId, version, updatedAtUtc, assets)
        {
        }

        [Key(0)] public string Schema { get; }
        [Key(1)] public string ProviderId { get; }
        [Key(2)] public string CatalogId { get; }
        [Key(3)] public long Version { get; }
        [Key(4)] public string UpdatedAtUtc { get; }
        [Key(5)] public EveAssetCatalogEntry[] Assets { get; }
    }

    [MessagePackObject]
    public sealed class EveAssetCatalogEntry
    {
        [SerializationConstructor]
        public EveAssetCatalogEntry(
            string assetRef,
            string assetKind,
            EveAssetVariant[] variants,
            Dictionary<string, string> metadata)
        {
            AssetRef = assetRef ?? "";
            AssetKind = assetKind ?? "";
            Variants = variants ?? Array.Empty<EveAssetVariant>();
            Metadata = metadata == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        }

        public EveAssetCatalogEntry(
            string assetRef,
            string assetKind,
            EveAssetVariant[] variants,
            IReadOnlyDictionary<string, string>? metadata = null)
            : this(
                assetRef,
                assetKind,
                variants,
                metadata == null ? new Dictionary<string, string>() : new Dictionary<string, string>(metadata))
        {
        }

        [Key(0)] public string AssetRef { get; }
        [Key(1)] public string AssetKind { get; }
        [Key(2)] public EveAssetVariant[] Variants { get; }
        [Key(3)] public Dictionary<string, string> Metadata { get; }
    }

    [MessagePackObject]
    public sealed class EveAssetVariant
    {
        [SerializationConstructor]
        public EveAssetVariant(
            string runtimeId,
            string platform,
            string format,
            string uri,
            string contentHash,
            long sizeBytes,
            string assetKey,
            Dictionary<string, string> metadata)
        {
            RuntimeId = runtimeId ?? "";
            Platform = platform ?? "";
            Format = format ?? "";
            Uri = uri ?? "";
            ContentHash = contentHash ?? "";
            SizeBytes = sizeBytes;
            AssetKey = assetKey ?? "";
            Metadata = metadata == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        }

        public EveAssetVariant(
            string runtimeId,
            string platform,
            string format,
            string uri,
            string contentHash,
            long sizeBytes,
            string assetKey,
            IReadOnlyDictionary<string, string>? metadata = null)
            : this(
                runtimeId,
                platform,
                format,
                uri,
                contentHash,
                sizeBytes,
                assetKey,
                metadata == null ? new Dictionary<string, string>() : new Dictionary<string, string>(metadata))
        {
        }

        [Key(0)] public string RuntimeId { get; }
        [Key(1)] public string Platform { get; }
        [Key(2)] public string Format { get; }
        [Key(3)] public string Uri { get; }
        [Key(4)] public string ContentHash { get; }
        [Key(5)] public long SizeBytes { get; }
        [Key(6)] public string AssetKey { get; }
        [Key(7)] public Dictionary<string, string> Metadata { get; }
    }
}
