using System.Reflection;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Avoir du patient (script 106) : la clôture et la relève comptent les espèces seules ; un paiement « avoir » n'entre jamais dans
/// l'attendu du tiroir, et la base admet ce mode de paiement.
/// </summary>
public class AvoirHorsEspecesTests
{
    private static string Racine()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    [Fact]
    public void Cloture_attendu_en_especes_sans_l_avoir()
    {
        var sql = (string)typeof(Pharma.Infrastructure.Caisse.SessionsCaisseSql)
            .GetField("SqlVentesEspeces", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        Assert.Contains("FROM paiement WHERE session_caisse_id = $1 AND mode = 'especes'", sql);
        Assert.DoesNotContain("avoir", sql);
    }

    [Fact]
    public void Script_106_admet_le_mode_avoir_et_protege_le_solde()
    {
        var script = File.ReadAllText(Path.Combine(Racine(), "database", "migration", "106_avoir_patient.sql"));
        Assert.Contains("'remboursement_credit', 'avoir'", script);
        Assert.Contains("CREATE TABLE IF NOT EXISTS avoir_mouvement", script);
        Assert.Contains("CREATE OR REPLACE VIEW v_avoir_patient", script);
        // Débit sous verrou du patient, jamais au-delà du solde
        Assert.Contains("FOR UPDATE", script);
        Assert.Contains("Avoir insuffisant", script);
    }
}
