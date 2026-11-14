using Npgsql;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Tresorerie;

/// <summary>
/// Sortie d'argent d'un compte de trésorerie (dépense, transfert, paiement d'un grossiste) : le compte est verrouillé dans la
/// transaction, son solde relu (v_solde_tresorerie), et la sortie refusée s'il ne suffit pas. Deux sorties simultanées du même
/// compte passent l'une après l'autre : la seconde voit la première.
/// </summary>
internal static class SoldeTresorerie
{
    public static async Task<decimal> VerrouillerAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long compteId, CancellationToken ct)
    {
        await using (var verrou = new NpgsqlCommand("SELECT 1 FROM compte_tresorerie WHERE id = $1 FOR UPDATE", cx, tx))
        {
            verrou.Parameters.AddWithValue(compteId);
            if (await verrou.ExecuteScalarAsync(ct) is null) throw new RegleMetierException("Compte introuvable.", "compte");
        }
        await using var cmd = new NpgsqlCommand("SELECT solde FROM v_solde_tresorerie WHERE id = $1", cx, tx);
        cmd.Parameters.AddWithValue(compteId);
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Refuse la sortie si le solde du compte, relu sous verrou, ne la couvre pas.</summary>
    public static async Task ExigerAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long compteId, decimal montant, CancellationToken ct,
        string? complement = null)
    {
        var solde = await VerrouillerAsync(cx, tx, compteId, ct);
        if (solde >= montant) return;
        await using var cmd = new NpgsqlCommand("SELECT nom FROM compte_tresorerie WHERE id = $1", cx, tx);
        cmd.Parameters.AddWithValue(compteId);
        var nom = (string)(await cmd.ExecuteScalarAsync(ct))!;
        throw new RegleMetierException($"Solde insuffisant sur {nom} ({solde:N2} DA pour {montant:N2} DA){complement}.", "montant");
    }
}
