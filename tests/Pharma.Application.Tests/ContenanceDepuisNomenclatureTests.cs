using System.Text.RegularExpressions;
using Pharma.Application.Catalogue;

namespace Pharma.Application.Tests;

/// <summary>
/// Scripts 134 à 137 (contenance inscrite depuis la nomenclature) : la règle SQL doit donner le même résultat que la proposition
/// de la fiche produit (ContenanceProduit). Mêmes exemples que database/tests/134_contenance_sachets_ampoules_tests.sql et 137_contenance_extension_tests.sql.
/// </summary>
public class ContenanceDepuisNomenclatureTests
{
    public static TheoryData<string?, int?> Exemples => new()
    {
        { "B/30", 30 }, { "B/030", 30 }, { "BOITE DE 20", 20 }, { "boîte de 14", 14 }, { " b / 012 ", 12 }, { "BTE/28", 28 },
        { "B/1000", 1000 }, { "B/5000", 5000 },
        { "B/1+1", null }, { "B/30 ET B/90", null }, { "FL/10ML", null }, { "T/15G", null }, { "B/2 FL DE 100 ML", null },
        { "B/1FL. DE PDRE.", null },
        { "B/30 SACHETS", 30 }, { "B/20 SACH.", 20 }, { "B/10 SACHETS-DOSES", 10 }, { "B/6 AMP.", 6 }, { "B/10 AMP BUV", 10 },
        { "B/5 AMPOULES", 5 }, { "B/30 CP", 30 }, { "B/20CPR", 20 }, { "B/20 COMPRIMÉS EFFERVESCENTS", 20 }, { "B/30 GELULES", 30 },
        { "B/10 SUPPOSITOIRES", 10 }, { "B/6 OVULES", 6 }, { "B/30 CAPSULES", 30 }, { "B/4 PATCHS", 4 },
        { "B/8 DISPOSITIFS TRANSDERMIQUES", 8 }, { "B/20 RECIPIENTS UNIDOSES", 20 }, { "B/1500 SACHETS", 1500 },
        { "b/30 gélules", 30 }, { "BOÎTE DE 20 COMPRIMÉS", 20 }, { "B/30 SACHETS DE 5 G", 30 }, { "B/5 AMP. DE 2ML", 5 }, { "B/5 AMPOULES EN POLYPROPYLENE", null },
        { "B/05AMP. DE 2ML", 5 }, { "B/05 AMP 2ML", 5 }, { "B/10 AMP.2ML", 10 }, { "B/10 AMP. DE 0,5 ML", 10 }, { "BOITE DE 05 AMPOULES DE 01ML", 5 },
        { "B/20 SACHETS DE 15ML", 20 }, { "B/14SACH. DE 5G", 14 }, { "B/20 RECIPIENTS UNIDOSES DE 2ML", 20 }, { "B/30 CP DE 500 MG", 30 }, { "boîte de 05 ampoules de 2 ml", 5 },
        { "B/10 DE 2ML", null }, { "B/5 AMP. DE 2", null }, { "B/20AMP./5ML", null }, { "B/20AMP.AUTOCASSABLES", null }, { "B/5 AMP. DE 2ML ET B/50 AMP. DE 2ML", null },
        { "B/1FL. DE PDRE. + 1AMP. DE 2ML", null }, { "B/1 SERINGUE PRE-REMPLIE DE 1ML", null }, { "B/2 FL DE 100 ML", null }, { "B/1500 AMP DE 2ML", 1500 }, { "B/1 SERINGUE", null }, { "B/0", null }, { "", null }, { null, null },
    };

    /// <summary>Motif PostgreSQL à jour (script 137, fonction contenance_depuis_conditionnement ; entre « FROM ' » et « ' »), sans l'option « (?i) » de tête.</summary>
    private static Regex MotifDuScript()
    {
        var sql = File.ReadAllText(Path.Combine(Racine(), "database", "migration", "137_contenance_extension.sql"));
        var m = Regex.Match(sql, @"substring\(p_conditionnement FROM '\(\?i\)([^']+)'\)");
        Assert.True(m.Success, "motif introuvable dans le script 137");
        return new Regex(m.Groups[1].Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    [Theory]
    [MemberData(nameof(Exemples))]
    public void Le_motif_du_script_donne_la_valeur_proposee_par_la_fiche(string? conditionnement, int? attendu)
    {
        Assert.Equal(attendu, ContenanceProduit.DepuisConditionnement(conditionnement));
        var m = conditionnement is null ? null : MotifDuScript().Match(conditionnement);
        int? parScript = m is { Success: true } && int.Parse(m.Groups[1].Value) is var n and >= ContenanceProduit.Minimum and <= ContenanceProduit.Maximum ? n : null;
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
