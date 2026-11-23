using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;
using Pharma.Infrastructure.Caisse;
using Xunit.Abstractions;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>C9 et C10 : retour client sur la vraie base (lignes produit seulement, garde de quantité, atomicité).</summary>
[Trait("Categorie", "Sql")]
public sealed class RetoursClientSqlTests(CopieSql copie, ITestOutputHelper sortie) : IClassFixture<CopieSql>
{
    private RetoursClientSql Retours => new(copie.Source);

    private static RetourAEcrire Retour(long vente, long utilisateur, decimal montant, params LigneRetourAEcrire[] lignes) =>
        new(vente, $"V-ESSAI-{Donnees.Suivant()}", "Retour d'essai", utilisateur, utilisateur, "especes", montant, null, "POSTE-ESSAI", lignes);

    private Task<long> CompterAsync(string table, string colonne, long id) =>
        copie.Donnees.LireAsync<long>($"SELECT count(*) FROM {table} WHERE {colonne} = @id", ("id", id));

    [FactSql]
    public async Task C9_la_prestation_n_est_pas_retournable_et_le_retour_total_ne_rend_que_le_produit()
    {
        sortie.WriteLine("Copie : " + copie.Nom);
        var d = copie.Donnees;
        var utilisateur = await d.UtilisateurAsync();
        var produit = await d.ProduitAsync();
        var lot = await d.LotAsync(produit, quantite: 3);
        var vente = await d.VenteValideeAsync(utilisateur, total: 80m);
        var ligneProduit = await d.LigneProduitAsync(vente, produit, lot, 2, 60m);
        var lignePrestation = await d.LignePrestationAsync(vente, 20m);

        var lecture = await Retours.LireAsync(vente);

        Assert.NotNull(lecture);
        var ligne = Assert.Single(lecture.Lignes);
        Assert.Equal(ligneProduit, ligne.LigneVenteId);
        Assert.Equal(60m, ligne.MontantPatient);
        Assert.Equal(20m, lecture.PartPatientPrestations);

        await Retours.EnregistrerAsync(Retour(vente, utilisateur, 60m,
            new LigneRetourAEcrire(ligneProduit, lot, 2, DestinationRetour.Stock, 60m, [])));

        Assert.Equal(60m, await d.LireAsync<decimal>("SELECT COALESCE(sum(montant_rembourse), 0) FROM retour_client WHERE vente_id = @v", ("v", vente)));
        Assert.Equal(5, await d.LireAsync<int>("SELECT quantite_disponible FROM lot WHERE id = @l", ("l", lot)));
        Assert.Equal(1, await CompterAsync("retour_client_ligne", "ligne_vente_id", ligneProduit));
        Assert.Equal(0, await CompterAsync("retour_client_ligne", "ligne_vente_id", lignePrestation));
        Assert.Equal(20m, await d.LireAsync<decimal>("SELECT montant_patient FROM ligne_vente WHERE id = @l", ("l", lignePrestation)));
        // Une fois tout rendu, la lecture montre tout le produit comme rendu
        var apres = await Retours.LireAsync(vente);
        Assert.Equal(ligne.QuantiteVendue, Assert.Single(apres!.Lignes).DejaRendue);
    }

    [FactSql]
    public async Task C9_un_retour_qui_vise_la_ligne_prestation_est_refuse_sans_rien_ecrire()
    {
        var d = copie.Donnees;
        var utilisateur = await d.UtilisateurAsync();
        var produit = await d.ProduitAsync();
        var lot = await d.LotAsync(produit, quantite: 3);
        var vente = await d.VenteValideeAsync(utilisateur, total: 80m);
        await d.LigneProduitAsync(vente, produit, lot, 2, 60m);
        var lignePrestation = await d.LignePrestationAsync(vente, 20m);

        var erreur = await Assert.ThrowsAsync<RegleMetierException>(() => Retours.EnregistrerAsync(Retour(vente, utilisateur, 20m,
            new LigneRetourAEcrire(lignePrestation, lot, 1, DestinationRetour.Stock, 20m, []))));

        Assert.Contains("vient déjà d’être rendue", erreur.Message);
        Assert.Equal(0, await CompterAsync("retour_client", "vente_id", vente));
        Assert.Equal(3, await d.LireAsync<int>("SELECT quantite_disponible FROM lot WHERE id = @l", ("l", lot)));
        Assert.Equal(0, await d.LireAsync<long>("SELECT count(*) FROM mouvement_stock WHERE lot_id = @l", ("l", lot)));
    }

    [FactSql]
    public async Task C10_un_retour_en_trop_est_refuse_sans_aucune_ecriture()
    {
        var d = copie.Donnees;
        var utilisateur = await d.UtilisateurAsync();
        var patient = await d.PatientAsync(solde: 40m);
        var produit = await d.ProduitAsync();
        var lot = await d.LotAsync(produit, quantite: 1);
        var vente = await d.VenteValideeAsync(utilisateur, patient, 60m);
        var ligne = await d.LigneProduitAsync(vente, produit, lot, 2, 60m);
        var journal = JournalAEcrire.Fixe(utilisateur, "retour_client", "vente", null, new Dictionary<string, object?>(), null);

        // 3 boîtes demandées pour 2 vendues : la garde de quantité restante, relue sous le verrou de la vente, refuse
        var erreur = await Assert.ThrowsAsync<RegleMetierException>(() => Retours.EnregistrerAsync(
            Retour(vente, utilisateur, 90m, new LigneRetourAEcrire(ligne, lot, 3, DestinationRetour.Stock, 90m, [])) with
            {
                ReductionDette = 40m, Journaux = [journal]
            }));

        Assert.Contains("vient déjà d’être rendue", erreur.Message);
        Assert.Equal(0, await CompterAsync("retour_client", "vente_id", vente));
        Assert.Equal(0, await CompterAsync("retour_client_ligne", "ligne_vente_id", ligne));
        Assert.Equal(1, await d.LireAsync<int>("SELECT quantite_disponible FROM lot WHERE id = @l", ("l", lot)));
        Assert.Equal(0, await d.LireAsync<long>("SELECT count(*) FROM mouvement_stock WHERE lot_id = @l", ("l", lot)));
        Assert.Equal(40m, await d.LireAsync<decimal>("SELECT solde_credit FROM patient WHERE id = @p", ("p", patient)));
        Assert.Equal(0, await d.LireAsync<long>("SELECT count(*) FROM journal_audit WHERE action = 'retour_client' AND enregistrement_id = @v", ("v", vente)));
    }

    /// <summary>
    /// C10 (scénario du plan « lot passé sous n entre lecture et retour ») : la garde « quantite_disponible >= n » de la destruction s'exécute
    /// après l'ajout des n boîtes rendues au même lot, dans la même transaction : elle ne peut donc pas refuser. Un lot vidé entre-temps
    /// reçoit le retour puis la destruction, et revient à 0 (comportement constaté, à confirmer par le titulaire).
    /// </summary>
    [FactSql]
    public async Task C10_lot_vide_entre_lecture_et_retour_la_destruction_est_acceptee_et_le_lot_revient_a_zero()
    {
        var d = copie.Donnees;
        var utilisateur = await d.UtilisateurAsync();
        var produit = await d.ProduitAsync();
        var lot = await d.LotAsync(produit, quantite: 0);
        var vente = await d.VenteValideeAsync(utilisateur, total: 60m);
        var ligne = await d.LigneProduitAsync(vente, produit, lot, 2, 60m);

        await Retours.EnregistrerAsync(Retour(vente, utilisateur, 60m, new LigneRetourAEcrire(ligne, lot, 2, DestinationRetour.Destruction, 60m, [])));

        Assert.Equal(0, await d.LireAsync<int>("SELECT quantite_disponible FROM lot WHERE id = @l", ("l", lot)));
        Assert.Equal(1, await d.LireAsync<long>("SELECT count(*) FROM mouvement_stock WHERE lot_id = @l AND type = 'retour_client' AND quantite = 2", ("l", lot)));
        Assert.Equal(1, await d.LireAsync<long>("SELECT count(*) FROM mouvement_stock WHERE lot_id = @l AND type = 'destruction' AND quantite = -2", ("l", lot)));
    }
}
