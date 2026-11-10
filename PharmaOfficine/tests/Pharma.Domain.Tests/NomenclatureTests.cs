using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Tests;

public class NomenclatureTests
{
    // Feuille telle que dans le fichier officiel : titres, puis en-tête, puis données
    private static readonly IReadOnlyList<IReadOnlyList<string?>> Feuille =
    [
        ["REPUBLIQUE ALGERIENNE DEMOCRATIQUE ET POPULAIRE"],
        [],
        ["N°ENREGISTREMENT", "CODE", "DENOMINATION COMMUNE INTERNATIONALE", "NOM DE MARQUE", "FORME", "DOSAGE", "CONDITIONNEMENT",
         "LISTE", "P1", "P2", "OBS", "LABORATOIRES DETENTEUR DE LA DECISION D'ENREGISTREMENT ",
         "PAYS DU LABORATOIRE DETENTEUR DE LA DECISION D'ENREGISTREMENT", "DATE D'ENREGISTREMENT INITIAL ",
         "DATE D'ENREGISTREMENT  FINAL ", "TYPE", "STATUT", "DUREE DE STABILITE"],
        ["595/15 A 068/06/22", "15 A 068", "PREGABALINE", "LYRICA", "GELULE", "150MG", "B/60", "LISTE I", "HOP", "OFF", null,
         "UPJOHN SAIDAL PHARMA", "ALGERIE", "38636", "2022-06-15", "RE", "F", "36 MOIS"],
        [null, null, null, null],
        ["035/01 A 003/17/23", "01 A 003", "CETIRIZINE DICHLORHYDRATE", "CETIRIPEX", "COMPRIME", "10MG", "B/15", "LISTE II"]
    ];

    [Fact]
    public void En_tete_trouve_sous_les_titres_et_colonnes_reconnues_par_leur_nom()
    {
        var lignes = Nomenclature.Lire("Nomenclature Juin 2026", Feuille);
        Assert.Equal(2, lignes.Count);
        var l = lignes[0];
        Assert.Equal(("595/15 A 068/06/22", "15 A 068", "PREGABALINE", "LYRICA"), (l.NumEnregistrement, l.Code, l.Dci, l.Marque));
        Assert.Equal(("B/60", "LISTE I", "UPJOHN SAIDAL PHARMA", "ALGERIE"), (l.Conditionnement, l.Liste, l.Laboratoire, l.Pays));
        Assert.Equal(SituationNomenclature.EnVigueur, l.Situation);
    }

    [Fact]
    public void Dates_excel_et_texte() =>
        Assert.Equal((new DateOnly(2005, 10, 11), new DateOnly(2022, 6, 15)),
            (Nomenclature.Lire("Nomenclature", Feuille)[0].DateInitiale, Nomenclature.Lire("Nomenclature", Feuille)[0].DateFinale));

    [Theory]
    [InlineData("Nomenclature Juin 2026", SituationNomenclature.EnVigueur)]
    [InlineData("Non Renouvelés ", SituationNomenclature.NonRenouvele)]
    [InlineData("Retraits", SituationNomenclature.Retire)]
    public void Situation_d_apres_le_nom_de_la_feuille(string feuille, SituationNomenclature attendue) =>
        Assert.Equal(attendue, Nomenclature.SituationDeFeuille(feuille));

    [Fact]
    public void Feuille_sans_en_tete_ignoree() =>
        Assert.Empty(Nomenclature.Lire("Feuil1", [["Nom", "Prix"], ["Doliprane", "120"]]));

    [Fact]
    public void Fichier_trop_petit_refuse() =>
        Assert.Throws<RegleMetierException>(() => Nomenclature.Verifier(Nomenclature.Lire("Nomenclature", Feuille)));
}
