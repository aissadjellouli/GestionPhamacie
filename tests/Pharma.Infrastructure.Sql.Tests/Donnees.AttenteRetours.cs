namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>Fabrique du lot 2 (caisse : ventes en attente et retours) : paniers en attente, vente validée, lignes produit et prestation.</summary>
public sealed partial class Donnees
{
    /// <summary>Ajoute <paramref name="n"/> paniers en attente au poste (insertion directe : le verrou du poste n'est pas pris).</summary>
    public async Task PanierAttenteAsync(string poste, long utilisateurId, int n)
    {
        for (var i = 0; i < n; i++)
            await ExecuterAsync(
                "INSERT INTO panier_attente (poste, utilisateur_id, libelle, lignes, total, contenu) VALUES (@poste, @u, @libelle, 1, 10, '{}'::jsonb)",
                ("poste", poste), ("u", utilisateurId), ("libelle", $"Panier Essai {Suivant()}"));
    }

    /// <summary>Nombre de paniers en attente du poste.</summary>
    public Task<long> NombrePaniersAsync(string poste) => LireAsync<long>("SELECT count(*) FROM panier_attente WHERE poste = @poste", ("poste", poste));

    /// <summary>Vente libre validée (sans session de caisse, sans paiement) ; les lignes s'ajoutent avec <see cref="LigneProduitAsync"/> et <see cref="LignePrestationAsync"/>.</summary>
    public async Task<long> VenteValideeAsync(long utilisateurId, long? patientId = null, decimal total = 0m)
    {
        return await InsererAsync(
            "INSERT INTO vente (numero, utilisateur_id, patient_id, type, montant_total, part_patient, statut) VALUES (@numero, @u, @p, 'libre', @t, @t, 'validee') RETURNING id",
            ("numero", $"V-ESSAI-{Suivant()}"), ("u", utilisateurId), ("p", patientId), ("t", total));
    }

    /// <summary>Ligne produit d'une vente. Le stock du lot n'est pas décrémenté : la quantité du lot est celle voulue après la vente.</summary>
    public Task<long> LigneProduitAsync(long venteId, long produitId, long lotId, int quantite, decimal montantPatient)
        => InsererAsync(
            "INSERT INTO ligne_vente (vente_id, produit_id, lot_id, quantite, prix_unitaire, montant_patient) VALUES (@v, @p, @l, @q, @pu, @m) RETURNING id",
            ("v", venteId), ("p", produitId), ("l", lotId), ("q", quantite), ("pu", montantPatient / quantite), ("m", montantPatient));

    /// <summary>Ligne de prestation (sans produit ni lot), avec son type de prestation d'essai.</summary>
    public async Task<long> LignePrestationAsync(long venteId, decimal montantPatient)
    {
        var n = Suivant();
        var type = await InsererAsync("INSERT INTO type_prestation (code, libelle, prix) VALUES (@code, @libelle, @prix) RETURNING id",
            ("code", $"PE{n}"), ("libelle", $"Prestation Essai {n}"), ("prix", montantPatient));
        return await InsererAsync(
            "INSERT INTO ligne_vente (vente_id, type_prestation_id, libelle, taux_tva, quantite, prix_unitaire, montant_patient) VALUES (@v, @t, @libelle, 0, 1, @m, @m) RETURNING id",
            ("v", venteId), ("t", type), ("libelle", $"Prestation Essai {n}"), ("m", montantPatient));
    }
}
