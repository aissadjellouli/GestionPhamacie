using Pharma.Application.Commun;
using Pharma.Application.Factures;
using Pharma.Application.Stock;
using Pharma.Domain.Stock;

namespace Pharma.Application.Tests;

/// <summary>Emplacements et organismes payeurs : tableaux de gestion filtrés et paginés par le serveur (2026-11-15).</summary>
public class ListesGestionTests
{
    [Fact]
    public void Page_en_memoire_donne_la_page_le_total_et_ramene_une_page_trop_loin_a_la_derniere()
    {
        var lignes = Enumerable.Range(1, 23).ToList();
        var p = PageEnMemoire.Paginer(lignes, DemandePage.Lire(2, 10));
        Assert.Equal((23, 2, 10, 11, 20), (p.Total, p.Numero, p.Lignes.Count, p.Lignes[0], p.Lignes[^1]));
        var loin = PageEnMemoire.Paginer(lignes, DemandePage.Lire(9, 10));
        Assert.Equal((3, 3), (loin.Numero, loin.Lignes.Count));
        var vide = PageEnMemoire.Paginer(new List<int>(), DemandePage.Lire(4, 10));
        Assert.Equal((0, 1), (vide.Total, vide.Numero));
    }

    [Fact]
    public void Emplacements_filtres_par_zone_etat_et_texte_sans_accents()
    {
        var liste = new[]
        {
            new Emplacement(1, "B-01", "B", "01", null, "Réfrigérateur", ZoneEmplacement.Vente, true, 3),
            new Emplacement(2, "A-01", "A", "01", null, null, ZoneEmplacement.Vente, false, 0),
            new Emplacement(3, "R-1", "R", "1", null, "Stock", ZoneEmplacement.Reserve, true, 1),
        };
        Assert.Equal(["A-01", "B-01", "R-1"], ListerEmplacements.Filtrer(liste, null, null, null).Select(e => e.Code));
        Assert.Equal(["R-1"], ListerEmplacements.Filtrer(liste, "reserve", null, null).Select(e => e.Code));
        Assert.Equal(["A-01"], ListerEmplacements.Filtrer(liste, null, "inactif", null).Select(e => e.Code));
        Assert.Equal(["B-01"], ListerEmplacements.Filtrer(liste, null, null, "refrigerateur").Select(e => e.Code));
    }

    [Fact]
    public void Organismes_filtres_par_etat_et_texte()
    {
        var liste = new[]
        {
            new OrganismeFiche(1, "CNAS", "Caisse nationale des assurances sociales", "Alger", null, true),
            new OrganismeFiche(2, "MIL", "Organisme des militaires", null, "123", false),
        };
        Assert.Equal(["MIL"], ListeOrganismes.Filtrer(liste, "inactif", null).Select(o => o.Code));
        Assert.Equal(["CNAS"], ListeOrganismes.Filtrer(liste, null, "alger").Select(o => o.Code));
        Assert.Equal(2, ListeOrganismes.Filtrer(liste, "", "").Count);
    }
}
