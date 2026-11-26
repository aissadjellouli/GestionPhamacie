using System.Text.RegularExpressions;
using Pharma.Application.Catalogue;

namespace Pharma.Application.Tests;

/// <summary>
/// Scripts 134 à 139 et 169 (contenance inscrite depuis la nomenclature) : la règle SQL doit donner le même résultat que la proposition
/// de la fiche produit (ContenanceProduit). Mêmes exemples que database/tests/134_contenance_sachets_ampoules_tests.sql et 137_contenance_unite_taille_unitaire_tests.sql et 139_contenance_flacons_seringues_stylos_tests.sql.
/// </summary>
public class ContenanceDepuisNomenclatureTests
{
    public static TheoryData<string?, int?> Exemples => new()
    {
        { "B/30", 30 }, { "B/030", 30 }, { "BOITE DE 20", 20 }, { "boîte de 14", 14 }, { " b / 012 ", 12 }, { "BTE/28", 28 },
        { "B/1000", 1000 }, { "B/5000", 5000 },
        { "B/1+1", null }, { "B/30 ET B/90", null }, { "FL/10ML", 1 }, { "T/15G", 1 }, { "B/2 FL DE 100 ML", 2 },
        { "B/1FL. DE PDRE.", null },
        { "B/30 SACHETS", 30 }, { "B/20 SACH.", 20 }, { "B/10 SACHETS-DOSES", 10 }, { "B/6 AMP.", 6 }, { "B/10 AMP BUV", 10 },
        { "B/5 AMPOULES", 5 }, { "B/30 CP", 30 }, { "B/20CPR", 20 }, { "B/20 COMPRIMÉS EFFERVESCENTS", 20 }, { "B/30 GELULES", 30 },
        { "B/10 SUPPOSITOIRES", 10 }, { "B/6 OVULES", 6 }, { "B/30 CAPSULES", 30 }, { "B/4 PATCHS", 4 },
        { "B/8 DISPOSITIFS TRANSDERMIQUES", 8 }, { "B/20 RECIPIENTS UNIDOSES", 20 }, { "B/1500 SACHETS", 1500 },
        { "b/30 gélules", 30 }, { "BOÎTE DE 20 COMPRIMÉS", 20 }, { "B/30 SACHETS DE 5 G", 30 }, { "B/5 AMP. DE 2ML", 5 }, { "B/5 AMPOULES EN POLYPROPYLENE", null },
        { "B/05AMP. DE 2ML", 5 }, { "B/05 AMP 2ML", 5 }, { "B/10 AMP.2ML", 10 }, { "B/10 AMP. DE 0,5 ML", 10 }, { "BOITE DE 05 AMPOULES DE 01ML", 5 },
        { "B/20 SACHETS DE 15ML", 20 }, { "B/14SACH. DE 5G", 14 }, { "B/20 RECIPIENTS UNIDOSES DE 2ML", 20 }, { "B/30 CP DE 500 MG", 30 }, { "boîte de 05 ampoules de 2 ml", 5 },
        { "B/10 DE 2ML", null }, { "B/5 AMP. DE 2", null }, { "B/20AMP./5ML", 20 }, { "B/20AMP.AUTOCASSABLES", null }, { "B/5 AMP. DE 2ML ET B/50 AMP. DE 2ML", null },
        { "B/01 FL", 1 }, { "B/10FL. DE 20ML", 10 }, { "B/01 FLACON DE 125ML", 1 }, { "B/01 FLC DE 60ML", 1 }, { "BOITE DE 1 FLACON DE 30 ML", 1 }, { "B/01 FL 2,5ML", 1 },
        { "B/1FL/5ML", 1 }, { "B/10 Flacons", 10 }, { "B/02 SERINGUES", 2 }, { "B/2 SERINGUE PRE-REMPLIES DE 0.8ML", 2 }, { "B/1SERING. PREREMPL. DE 0,2ML", 1 },
        { "B/6 Seringues préremplies", 6 }, { "B/1 SERIN PRE REMPLI DE 0,30 ML", 1 }, { "B/05 STYLOS PREREMPLIS DE 3ML", 5 }, { "B/01 STYLO", 1 }, { "B/50CART. DE 1,8ML", 50 },
        { "BOITE DE 05 CARTOUCHES DE 3ML", 5 }, { "B/04 POCHES DE 2L", 4 }, { "B/01 TUBE DE 50G", 1 }, { "B/1AMP. (5ML)", 1 }, { "B/10 FL (1 MG)", 10 }, { "B/1 AMP/5ML", 1 },
        { "B/1500 FLACONS", 1500 },
        // Script 169 (2026-11-26) : contenant seul = 1, pilulier = ses unités ; plusieurs présentations restent vides
        { "FL/120ML", 1 }, { "FL./120ML", 1 }, { "FL/120 ML", 1 }, { "FL/20DOSES", 1 }, { "FL./20 DOSES", 1 }, { "FL/30G", 1 }, { "FLACON DE 100ML", 1 },
        { "B/TUBE DE 30G", 1 }, { "PILULIER DE 60", 60 }, { "B/30 ET B/60", null }, { "B/28+7", null }, { "BIDON DE 5L EN PEHD", null }, { "100 ML", null },
        { "FL/05ML", 1 }, { "F/125ML", 1 }, { "B/FL 15 ML", 1 }, { "B/1FL. DE PDRE. + 1FL. DE SOLVT.", null }, { "B/01 FL. DE PDRE.", null }, { "B/01 FL DE 60", null },
        { "BOITE DE 1 FLACON CONTENANT 500 MG DE POUDRE", null }, { "B/1FL DE 30ML+ PIPETTE GRADUEE EN ML (2ml)", null }, { "B/01STYLO. PREREMPLI DE 0,5ML (CARTOUCHE BI COMPARTIMENTEE)+UNE AIGUILLE", null },
        { "B/10FL ET B/20FL", null }, { "B/60 GELULE + INHALATEUR", null }, { "BOITE D'UN FLACON", null }, { "POCHE DE 500ML", null }, { "B/400G", null }, { "TUBE/30G", 1 }, { "PILULIER/30", 30 },
        { "B/1FL. DE PDRE. + 1AMP. DE 2ML", null }, { "B/1 SERINGUE PRE-REMPLIE DE 1ML", 1 }, { "B/2 FL DE 100 ML", 2 }, { "B/1500 AMP DE 2ML", 1500 }, { "B/1 SERINGUE", 1 }, { "B/0", null }, { "", null }, { null, null },
    };

    /// <summary>
    /// Fonction contenance_depuis_conditionnement à jour (script 169) rejouée en C# : COALESCE(motif « boîte », motif « pilulier »,
    /// 1 si le motif « contenant seul » est reconnu). Motifs lus dans le script, sans l'option « (?i) » de tête.
    /// </summary>
    private static int? ParScript(string conditionnement)
    {
        var sql = File.ReadAllText(Path.Combine(Racine(), "database", "migration", "169_contenance_contenant_seul_pilulier.sql"));
        var nombres = Regex.Matches(sql, @"substring\(p_conditionnement FROM '\(\?i\)([^']+)'\)");
        var seul = Regex.Match(sql, @"p_conditionnement ~\* '([^']+)'");
        Assert.True(nombres.Count == 2 && seul.Success, "motifs introuvables dans le script 169");
        foreach (Match motif in nombres)
        {
            var m = new Regex(motif.Groups[1].Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Match(conditionnement);
            if (m.Success) return int.Parse(m.Groups[1].Value) is var n and >= ContenanceProduit.Minimum and <= ContenanceProduit.Maximum ? n : null;
        }
        return new Regex(seul.Groups[1].Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).IsMatch(conditionnement) ? 1 : null;
    }

    [Theory]
    [MemberData(nameof(Exemples))]
    public void Le_motif_du_script_donne_la_valeur_proposee_par_la_fiche(string? conditionnement, int? attendu)
    {
        Assert.Equal(attendu, ContenanceProduit.DepuisConditionnement(conditionnement));
        int? parScript = conditionnement is null ? null : ParScript(conditionnement);
        Assert.Equal(attendu, parScript);
    }

    [Fact]
    public void Plusieurs_lignes_retenues_seulement_si_toutes_identiques_et_reconnues()
    {
        Assert.Equal(20, ContenanceProduit.DepuisNomenclature(["B/20", "B/20"]));
        Assert.Null(ContenanceProduit.DepuisNomenclature(["B/20", "B/30"]));
        Assert.Null(ContenanceProduit.DepuisNomenclature(["B/20", "FL/10ML"]));
    }

    private static string Racine()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}
