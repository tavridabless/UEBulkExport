using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;

namespace UEBulkExport;

internal static class ContainerAesKeys
{
    public static void Submit(DefaultFileProvider provider, IEnumerable<string> rawKeys,
        CancellationToken ct = default)
    {
        // CUE4Parse shares each reader's mutable AES key and index across its mount tasks.
        // One candidate at a time means a successful reader is removed before trying a fallback.
        foreach (var raw in rawKeys)
        {
            var (guid, key) = Cli.SplitAesKey(raw);
            SubmitCandidate(guid is null ? default : new FGuid(guid), new FAesKey(key));
        }

        // Leave the implicit zero key behind every explicit candidate for the default GUID.
        SubmitCandidate(default, new FAesKey(new byte[32]));

        void SubmitCandidate(FGuid guid, FAesKey key)
        {
            ct.ThrowIfCancellationRequested();
            if (provider.UnloadedVfs.Any(reader => reader.EncryptionKeyGuid == guid))
                provider.SubmitKey(guid, key);
        }
    }
}
