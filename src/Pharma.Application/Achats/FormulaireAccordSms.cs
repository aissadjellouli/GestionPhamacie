using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Achats;

/// <summary>
/// Formulaire papier d'accord SMS (2026-11-16) : imprimé pour le patient (en-tête de la pharmacie, nom, téléphone, texte réglable
/// par le titulaire), signé au comptoir ; « formulaire signé reçu » noté sur la fiche avec la date. Journal : identifiants seulement.
/// </summary>
public interface IFormulaireAccordSmsDepot
{
    /// <summary>Nom affiché et téléphone du patient ; null si le patient n'existe pas.</summary>
    Task<(string Nom, string? Telephone)?> PatientAsync(long patientId, CancellationToken ct = default);
    Task<(bool Recu, DateTime? Le)?> RecuAsync(long patientId, CancellationToken ct = default);
    Task EnregistrerRecuAsync(long patientId, bool recu, DateTime quandUtc, CancellationToken ct = default);
    /// <summary>Texte réglé par le titulaire ; null = texte par défaut.</summary>
    Task<string?> TexteAsync(CancellationToken ct = default);
    Task EnregistrerTexteAsync(string? texte, long utilisateurId, CancellationToken ct = default);
}

public interface IDocumentsAccordSms
{
    byte[] FormulaireAccordSms(ContenuFormulaireAccordSms contenu);
}

public sealed record ContenuFormulaireAccordSms(Administration.IdentitePharmacie? Pharmacie, string Patient, string? Telephone, string Texte, DateOnly Date);

public static class RegleFormulaireAccordSms
{
    public const int LongueurMax = Pharma.Domain.Reglages.RegleFormulaireSms.LongueurMax;

    /// <summary>Texte par défaut. [À VÉRIFIER : texte à faire valider par le titulaire / ANPDP]</summary>
    public const string TexteParDefaut =
        "J’accepte que la pharmacie m’envoie un SMS au numéro ci-dessus uniquement pour m’avertir que ma commande est disponible.\n" +
        "Ce SMS ne contient ni publicité ni information sur ma santé ou mes médicaments. Aucun message n’est envoyé par WhatsApp " +
        "ou une autre messagerie.\n" +
        "Je peux retirer cet accord à tout moment, sur simple demande au comptoir de la pharmacie.\n" +
        "Mes données sont traitées conformément à la loi n° 18-07 du 10 juin 2018 relative à la protection des personnes physiques " +
        "dans le traitement des données à caractère personnel.";

    public static string Texte(string? regle) => string.IsNullOrWhiteSpace(regle) ? TexteParDefaut : regle.Trim();
}

public sealed class FormulaireAccordSmsPatient(IFormulaireAccordSmsDepot depot, IDocumentsAccordSms documents, Administration.IPharmacie pharmacie,
    IPermissionService droits, IJournal journal, IHorloge horloge, Reglages.IGardeReglagesCentraux? garde = null)
{
    public async Task<(byte[] Contenu, string NomFichier)> ImprimerAsync(long patientId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "imprimer le formulaire d’accord SMS", ct);
        var (nom, telephone) = await depot.PatientAsync(patientId, ct) ?? throw new RegleMetierException("Patient introuvable.");
        var texte = RegleFormulaireAccordSms.Texte(await depot.TexteAsync(ct));
        var pdf = documents.FormulaireAccordSms(new ContenuFormulaireAccordSms(await pharmacie.LireAsync(ct), nom, telephone, texte,
            horloge.Aujourdhui));
        // Journal : identifiant du patient seulement (ni nom ni téléphone)
        await journal.EcrireAsync(utilisateurId, "formulaire_sms_imprime", "patient", patientId, new Dictionary<string, object?>(), Permissions.CaisseVendre, ct);
        return (pdf, $"accord_sms_{patientId}.pdf");
    }

    public async Task<(bool Recu, DateTime? Le)> LireRecuAsync(long patientId, CancellationToken ct = default) =>
        await depot.RecuAsync(patientId, ct) ?? throw new RegleMetierException("Patient introuvable.");

    public async Task EnregistrerRecuAsync(long patientId, bool recu, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "noter le formulaire d’accord SMS signé", ct);
        if (await depot.RecuAsync(patientId, ct) is null) throw new RegleMetierException("Patient introuvable.");
        await depot.EnregistrerRecuAsync(patientId, recu, horloge.MaintenantUtc, ct);
        await journal.EcrireAsync(utilisateurId, "formulaire_sms_recu", "patient", patientId,
            new Dictionary<string, object?> { ["recu"] = recu }, Permissions.CaisseVendre, ct);
    }

    /// <summary>Texte du formulaire (réglé ou par défaut) et indicateur « texte par défaut ».</summary>
    public async Task<(string Texte, bool ParDefaut)> TexteAsync(CancellationToken ct = default)
    {
        var regle = await depot.TexteAsync(ct);
        return (RegleFormulaireAccordSms.Texte(regle), string.IsNullOrWhiteSpace(regle));
    }

    /// <summary>Texte vide : retour au texte par défaut.</summary>
    public async Task ModifierTexteAsync(string? texte, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AdminParametres, "régler le texte du formulaire d’accord SMS", ct);
        var t = Pharma.Domain.Reglages.RegleFormulaireSms.Normaliser(texte, RegleFormulaireAccordSms.TexteParDefaut);
        if (garde is not null) await garde.ExigerModifiableAsync(Pharma.Domain.Reglages.TablesMutualisables.FormulaireSms, ct);
        var avant = await depot.TexteAsync(ct);
        await depot.EnregistrerTexteAsync(t, utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, "formulaire_sms_texte_modifie", "parametre", 1,
            new Dictionary<string, object?>
            {
                ["par_defaut"] = t is null,
                ["avant"] = new { texte = avant }, ["apres"] = new { texte = t }
            }, Permissions.AdminParametres, ct);
    }
}
