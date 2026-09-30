using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

namespace UEBulkExport;

public sealed record AesKeyValidation(
    bool Verified,
    int ContainersUnlocked,
    int FilesAdded,
    string? Error = null);

/// <summary>Validates candidates by observing whether they unlock real selected containers.</summary>
public static class AesKeyValidator
{
    public static Task<IReadOnlyDictionary<string, AesKeyValidation>> ValidateAsync(
        string paksDirectory,
        EGame game,
        IReadOnlyList<AesKeyCandidate> candidates,
        CancellationToken ct = default)
    {
        var directory = Discovery.ResolvePaksDirectory(paksDirectory);
        var baseline = Probe(directory, game, null, []);
        if (baseline.Locked.Count == 0)
            throw new UserFacingException(
                "The selected containers do not expose a locked index to validate against.",
                "Candidates were found, but cryptographic verification requires at least one encrypted .pak/.utoc.");

        var results = new Dictionary<string, AesKeyValidation>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var probe = Probe(directory, game, candidate.Key, baseline.KeyGuids);
                // A reader disappearing from UnloadedVfs is not enough: require the same named
                // reader to appear in MountedVfs so parser failures cannot become false positives.
                var unlocked = baseline.Locked.Intersect(probe.Mounted, StringComparer.OrdinalIgnoreCase).Count();
                var filesAdded = Math.Max(0, probe.FileCount - baseline.FileCount);
                results[candidate.Key] = new AesKeyValidation(unlocked > 0, unlocked, filesAdded);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A malformed decrypted index is the normal result of trying a wrong key.
                results[candidate.Key] = new AesKeyValidation(false, 0, 0, e.Message);
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, AesKeyValidation>>(results);
    }

    private sealed record ProbeResult(
        HashSet<string> Locked,
        HashSet<string> Mounted,
        IReadOnlyList<string> KeyGuids,
        int FileCount);

    private static ProbeResult Probe(
        string directory,
        EGame game,
        string? key,
        IReadOnlyList<string> targetGuids)
    {
        using var provider = new DefaultFileProvider(
            directory,
            SearchOption.AllDirectories,
            new VersionContainer(game));

        provider.Initialize();

        var submitted = new List<KeyValuePair<FGuid, FAesKey>>();
        if (key is null)
        {
            submitted.Add(new KeyValuePair<FGuid, FAesKey>(new FGuid(), new FAesKey(new byte[32])));
        }
        else
        {
            var aes = new FAesKey(key);
            submitted.Add(new KeyValuePair<FGuid, FAesKey>(new FGuid(), aes));
            foreach (var guid in targetGuids)
            {
                if (Guid.TryParse(guid, out var parsed) && parsed != Guid.Empty)
                    submitted.Add(new KeyValuePair<FGuid, FAesKey>(new FGuid(guid), aes));
            }
        }

        provider.SubmitKeys(submitted);
        provider.Mount();

        var unloaded = provider.UnloadedVfs
            .Where(vfs => !vfs.Name.StartsWith("global.", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var locked = unloaded.Select(vfs => vfs.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mounted = provider.MountedVfs.Select(vfs => vfs.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var guids = unloaded.Select(vfs => vfs.EncryptionKeyGuid.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProbeResult(locked, mounted, guids, provider.Files.Count);
    }
}
