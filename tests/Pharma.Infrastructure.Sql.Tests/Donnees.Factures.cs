namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>Fabrique du lot 4 : factures clients, lignes, règlements et avoirs (tables facture_client, facture_client_ligne, reglement_facture_client).</summary>
public sealed partial class Donnees
{
    /// <summary>
    /// SQL d'un règlement de facture (mouvement de trésorerie « encaissement » puis ligne de règlement), partagé avec le bloqueur
    /// pour qu'un test pose le même règlement dans une transaction ouverte. Paramètres : facture, montant, utilisateur.
    /// </summary>
    public const string SqlReglementFacture = """
        WITH m AS (
            INSERT INTO mouvement_tresorerie (compte_id, date_mouvement, sens, montant, nature, libelle, utilisateur_id)
            VALUES ((SELECT min(id) FROM compte_tresorerie), current_date, 'entree', @montant, 'encaissement', 'Règlement d''essai', @utilisateur)
            RETURNING id)
        INSERT INTO reglement_facture_client (mouvement_id, facture_id, montant) SELECT id, @facture, @montant FROM m
        """;

    /// <summary>
    /// Facture client d'essai pour un patient fictif : statut « emise », « rejetee » ou « remplacee ». Numéro « F-AAAA-9nnnn » unique
    /// (l'année du numéro est celle de <paramref name="date"/>). Une ligne libre du montant TTC est écrite. Renvoie (id, numéro).
    /// </summary>
    public async Task<(long Id, string Numero)> FactureAsync(decimal ttc, long utilisateurId, string statut = "emise", DateOnly? date = null,
        string? numero = null, bool horsSuivi = false)
    {
        var jour = date ?? new DateOnly(2026, 6, 15);
        numero ??= $"F-{jour.Year}-9{Suivant()}";
        var patient = await PatientAsync();
        var id = await InsererAsync(
            "INSERT INTO facture_client (type, numero, date_facture, destinataire, patient_id, destinataire_nom, montant_ht, montant_tva, montant_ttc, " +
            "statut, motif_rejet, hors_suivi, motif_hors_suivi, utilisateur_id, date_echeance) " +
            "VALUES ('facture', @numero, @date, 'patient', @patient, 'Patient Essai', @ttc, 0, @ttc, @statut, @rejet, @hs, @mhs, @u, @date) RETURNING id",
            ("numero", numero), ("date", jour), ("patient", patient), ("ttc", ttc), ("statut", statut),
            ("rejet", statut == "rejetee" ? "Rejet d'essai" : null), ("hs", horsSuivi), ("mhs", horsSuivi ? "Hors suivi d'essai" : null), ("u", utilisateurId));
        await ExecuterAsync(
            "INSERT INTO facture_client_ligne (facture_id, ordre, part, designation, quantite, prix_unitaire_ttc, taux_tva, montant_ttc) " +
            "VALUES (@f, 1, 'libre', 'Ligne libre d''essai', 1, @ttc, 0, @ttc)", ("f", id), ("ttc", ttc));
        return (id, numero);
    }

    /// <summary>Règlement (encaissement) de <paramref name="montant"/> imputé sur la facture.</summary>
    public Task<int> ReglementAsync(long factureId, decimal montant, long utilisateurId)
        => ExecuterAsync(SqlReglementFacture, ("montant", montant), ("utilisateur", utilisateurId), ("facture", factureId));

    /// <summary>Avoir d'essai de <paramref name="ttc"/> sur la facture d'origine (sans effet sur la dette : part_credit = 0).</summary>
    public async Task<long> AvoirAsync(long factureOrigineId, decimal ttc, long utilisateurId)
    {
        var patient = await PatientAsync();
        return await InsererAsync(
            "INSERT INTO facture_client (type, numero, date_facture, destinataire, patient_id, destinataire_nom, montant_ht, montant_tva, montant_ttc, " +
            "utilisateur_id, facture_origine_id, motif) " +
            "VALUES ('avoir', @numero, @date, 'patient', @patient, 'Patient Essai', @ttc, 0, @ttc, @u, @origine, 'Avoir d''essai') RETURNING id",
            ("numero", $"A-2026-9{Suivant()}"), ("date", new DateOnly(2026, 6, 16)), ("patient", patient), ("ttc", ttc), ("u", utilisateurId), ("origine", factureOrigineId));
    }
}
