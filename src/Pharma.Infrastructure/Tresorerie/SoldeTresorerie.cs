using Npgsql;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Tresorerie;

/// <summary>
/// Sortie d'argent d'un compte de trésorerie (dépense, transfert, remboursement au titulaire, échéance de prêt ; règlement d'un
/// grossiste ou paiement d'une traite seulement en espèces, depuis le coffre) : le compte est verrouillé dans la transaction, son
/// solde relu (v_solde_tresorerie), et la sortie refusée s'il ne suffit pas. Deux sorties simultanées du même compte passent l'une
/// après l'autre : la seconde voit la première. Un grossiste réglé par chèque, virement ou prélèvement, ou une traite payée par la
/// banque, ne passe pas ici : le découvert bancaire est accepté (décision du titulaire, 2026-11-15).
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

    /// <summary>
    /// Paiement d'une échéance de prêt : compte verrouillé ; découvert accepté sur une banque, refusé sur le coffre
    /// (RegleTresorerie.PaiementEcheancePermis).
    /// </summary>
    public static async Task ExigerPourEcheanceAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long compteId, decimal montant, CancellationToken ct,
        string? complement = null)
    {
        var solde = await VerrouillerAsync(cx, tx, compteId, ct);
        await using var cmd = new NpgsqlCommand("SELECT type FROM compte_tresorerie WHERE id = $1", cx, tx);
        cmd.Parameters.AddWithValue(compteId);
        var type = (string)(await cmd.ExecuteScalarAsync(ct))!;
        if (Pharma.Domain.Tresorerie.RegleTresorerie.PaiementEcheancePermis(type, solde, montant)) return;
        await ExigerAsync(cx, tx, compteId, montant, ct, complement);
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
