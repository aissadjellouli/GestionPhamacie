using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Reglages;

namespace Pharma.Application.Reglages;

// ---- Mutualisation des réglages, lot M5 (D8) : saisie locale de parametre_fiscal et parametre_tracabilite, qui n'avaient pas d'écran ----
// Les règles sont celles du domaine (RegleFiscale, RegleTracabilite, lot M1) ; la garde des tables imposées passe par IGardeReglagesCentraux ;
// le journal note la valeur avant et après (D13). Droit : pharmacien titulaire.

/// <summary>Réglage fiscal : conservation (6 à 30 ans), format d'export (fixe), séparateur du CSV, fuseau des périodes.</summary>
public sealed record ParametreFiscal(int ConservationAnnees, string FormatExportDgi, string SeparateurCsv, string Fuseau);

/// <summary>Traçabilité des boîtes : numéro de boîte obligatoire ; patient obligatoire (jamais, psychotrope, ordonnance, toujours).</summary>
public sealed record ParametreTracabilite(bool BoiteObligatoire, string PatientObligatoire);

public interface IParametresFiscalTracabilite
{
    Task<ParametreFiscal> FiscalAsync(CancellationToken ct = default);
    Task EnregistrerFiscalAsync(ParametreFiscal p, CancellationToken ct = default);
    Task<ParametreTracabilite> TracabiliteAsync(CancellationToken ct = default);
    Task EnregistrerTracabiliteAsync(ParametreTracabilite p, CancellationToken ct = default);
    /// <summary>Le fuseau est-il connu de la base ? (AT TIME ZONE refuserait un identifiant inconnu).</summary>
    Task<bool> FuseauConnuAsync(string fuseau, CancellationToken ct = default);
}

public sealed class ConsulterParametreFiscal(IParametresFiscalTracabilite depot)
{
    public Task<ParametreFiscal> ExecuterAsync(CancellationToken ct = default) => depot.FiscalAsync(ct);
}

public sealed class ModifierParametreFiscal(IParametresFiscalTracabilite depot, IPermissionService droits, IJournal journal, IGardeReglagesCentraux? garde = null)
{
    public async Task ExecuterAsync(ParametreFiscal p, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler la conservation fiscale", ct);
        if (garde is not null) await garde.ExigerModifiableAsync(TablesMutualisables.Fiscal, ct);
        p = p with { Fuseau = (p.Fuseau ?? "").Trim(), SeparateurCsv = p.SeparateurCsv ?? "" };
        RegleFiscale.VerifierConservation(p.ConservationAnnees);
        RegleFiscale.VerifierFormat(p.FormatExportDgi);
        RegleFiscale.VerifierSeparateur(p.SeparateurCsv);
        RegleFiscale.VerifierFuseau(p.Fuseau);
        if (!await depot.FuseauConnuAsync(p.Fuseau, ct))
            throw new Pharma.Domain.Commun.RegleMetierException("Fuseau horaire : cet identifiant n’est pas connu de la base (par exemple Africa/Algiers).", "fuseau");
        var avant = await depot.FiscalAsync(ct);
        await depot.EnregistrerFiscalAsync(p, ct);
        await journal.EcrireAsync(utilisateurId, "parametre_fiscal", "parametre_fiscal", 1, new Dictionary<string, object?>
        {
            ["avant"] = Valeur(avant), ["apres"] = Valeur(p)
        }, null, ct);
    }

    private static object Valeur(ParametreFiscal p) => new
    {
        conservation_annees = p.ConservationAnnees, format_export_dgi = p.FormatExportDgi, separateur_csv = p.SeparateurCsv, fuseau = p.Fuseau
    };
}

public sealed class ConsulterParametreTracabilite(IParametresFiscalTracabilite depot)
{
    public Task<ParametreTracabilite> ExecuterAsync(CancellationToken ct = default) => depot.TracabiliteAsync(ct);
}

public sealed class ModifierParametreTracabilite(IParametresFiscalTracabilite depot, IPermissionService droits, IJournal journal, IGardeReglagesCentraux? garde = null)
{
    public async Task ExecuterAsync(ParametreTracabilite p, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler la traçabilité des boîtes", ct);
        if (garde is not null) await garde.ExigerModifiableAsync(TablesMutualisables.Tracabilite, ct);
        RegleTracabilite.VerifierPatientObligatoire(p.PatientObligatoire);
        var avant = await depot.TracabiliteAsync(ct);
        await depot.EnregistrerTracabiliteAsync(p, ct);
        await journal.EcrireAsync(utilisateurId, "parametre_tracabilite", "parametre_tracabilite", 1, new Dictionary<string, object?>
        {
            ["avant"] = Valeur(avant), ["apres"] = Valeur(p)
        }, null, ct);
    }

    private static object Valeur(ParametreTracabilite p) => new { boite_obligatoire = p.BoiteObligatoire, patient_obligatoire = p.PatientObligatoire };
}
