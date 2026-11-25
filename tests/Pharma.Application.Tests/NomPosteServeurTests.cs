using Pharma.Desktop.Services;
using Pharma.Shared;

namespace Pharma.Application.Tests;

/// <summary>
/// Poste renommé par le titulaire (constat du 2026-11-26) : le serveur utilisait aussitôt le nouveau nom, le poste affichait toujours l'ancien
/// (poste-identite.json écrit à l'autorisation, jamais relu). Le poste reprend le nom donné par le serveur à ce poste prouvé.
/// </summary>
public class NomPosteServeurTests
{
    private static EtatPosteAutoriseDto Etat(bool prouve, long? id, string? nom) => new("exige", prouve, id, nom, null);

    [Fact]
    public void Poste_prouve_sous_son_identifiant_et_renomme_prend_le_nom_du_serveur()
    {
        Assert.Equal("CAISSE-2", NomPosteServeur.NouveauNom(7, "CAISSE-SYNC", Etat(true, 7, "CAISSE-2")));
        Assert.Equal("Caisse comptoir", NomPosteServeur.NouveauNom(7, "CAISSE-SYNC", Etat(true, 7, "  Caisse comptoir ")));
    }

    [Fact]
    public void Meme_nom_rien_a_changer()
    {
        Assert.Null(NomPosteServeur.NouveauNom(7, "CAISSE-SYNC", Etat(true, 7, "CAISSE-SYNC")));
    }

    [Fact]
    public void Jamais_le_nom_d_un_autre_poste_ni_sans_preuve_ni_sans_reponse()
    {
        Assert.Null(NomPosteServeur.NouveauNom(7, "CAISSE-SYNC", Etat(true, 8, "CAISSE-2")));     // autre identifiant
        Assert.Null(NomPosteServeur.NouveauNom(7, "CAISSE-SYNC", Etat(false, null, null)));        // preuve refusée ou mode inactif
        Assert.Null(NomPosteServeur.NouveauNom(7, "CAISSE-SYNC", Etat(true, null, "CAISSE-2")));   // identifiant absent
        Assert.Null(NomPosteServeur.NouveauNom(7, "CAISSE-SYNC", null));                           // serveur injoignable ou ancien
        Assert.Null(NomPosteServeur.NouveauNom(null, null, Etat(true, 7, "CAISSE-2")));            // poste non enrôlé
    }

    [Fact]
    public void Nom_vide_du_serveur_ignore()
    {
        Assert.Null(NomPosteServeur.NouveauNom(7, "CAISSE-SYNC", Etat(true, 7, "   ")));
        Assert.Null(NomPosteServeur.NouveauNom(7, "CAISSE-SYNC", Etat(true, 7, null)));
    }
}
