namespace ProjectBeacon.Application.Security;

using Application.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

/// <summary>
/// One-shot re-encryption of v1 ciphertexts to the v2 format. A row is rewritten only after its
/// value decrypts and re-seals, so a failed row keeps its original ciphertext. The pass is
/// idempotent and stops when a batch makes no progress, so failing rows are counted, not hidden
/// behind a zero remainder.
/// </summary>
public static class DataKeyMigration
{
    public sealed record Result(int Migrated, int RemainingV1, int Failed, bool LegacyKeyAvailable);

    private const int BatchSize = 500;

    public static async Task<Result> ReEncryptAsync(IBeaconDb db, ISecretProtector secrets, CancellationToken ct = default)
    {
        // With v2 writes off, re-sealing would produce another v1 blob and the batch loop would
        // never make progress; the pass is a no-op that only reports what is still v1.
        if (!secrets.WriteV2)
        {
            return new Result(0, await CountRemainingAsync(db, ct), 0, secrets.HasLegacyKey);
        }

        var migrated = 0;
        var failed = 0;

        while (true)
        {
            var batch = await db.Users
                .Where(u => u.TotpSecretCipher != "" && !u.TotpSecretCipher.StartsWith(ISecretProtector.V2Prefix))
                .Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
                break;
            var succeeded = 0;
            foreach (var user in batch)
            {
                if (ReEncrypt(() => secrets.Open(user.TotpSecretCipher), secrets, cipher => user.UpdateTotpCipher(cipher)))
                {
                    succeeded++;
                    migrated++;
                }
                else
                {
                    failed++;
                }
            }
            await db.SaveChangesAsync(ct);
            if (succeeded == 0)
                break;
        }

        while (true)
        {
            var batch = await db.OpenCodeConnections
                .Where(c => c.ApiKeyCipher != "" && !c.ApiKeyCipher.StartsWith(ISecretProtector.V2Prefix))
                .Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
                break;
            var succeeded = 0;
            foreach (var row in batch)
            {
                if (ReEncrypt(() => secrets.Open(row.ApiKeyCipher), secrets, cipher => row.UpdateApiKeyCipher(cipher)))
                {
                    succeeded++;
                    migrated++;
                }
                else
                {
                    failed++;
                }
            }
            await db.SaveChangesAsync(ct);
            if (succeeded == 0)
                break;
        }

        return new Result(migrated, await CountRemainingAsync(db, ct), failed, secrets.HasLegacyKey);
    }

    private static async Task<int> CountRemainingAsync(IBeaconDb db, CancellationToken ct)
        => await db.Users
            .CountAsync(u => u.TotpSecretCipher != "" && !u.TotpSecretCipher.StartsWith(ISecretProtector.V2Prefix), ct)
           + await db.OpenCodeConnections
            .CountAsync(c => c.ApiKeyCipher != "" && !c.ApiKeyCipher.StartsWith(ISecretProtector.V2Prefix), ct);

    private static bool ReEncrypt(Func<string> open, ISecretProtector secrets, Action<string> write)
    {
        try
        {
            write(secrets.Seal(open()));
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
