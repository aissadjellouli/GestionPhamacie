using Pharma.Cloud.Api.Depart;

namespace Pharma.Cloud.Web.Codes;

/// <summary>
/// Réglage « Envoi des codes » lu en base pour l'envoi (script central 037, cloud_ecran_facteur_reglage_envoi) : mot de passe SMTP
/// CHIFFRÉ seulement. ToString() ne montre ni l'identifiant ni le mot de passe.
/// </summary>
public sealed record ReglageEnvoiCodes(string Canal, string? Serveur, int? Port, string Tls, string? Identifiant, string? Expediteur,
    string? MotDePasseChiffre, bool Complet)
{
    public override string ToString() => $"canal {Canal}, SMTP {Serveur}:{Port} {Tls}, mot de passe {(MotDePasseChiffre is null ? "absent" : "masqué")}";
}

/// <summary>
/// Lot S7.6 : choix de l'envoi d'un code de second facteur, À CHAQUE ENVOI, d'après le réglage en base (gardé 10 secondes en
/// mémoire) au lieu d'un mode figé au démarrage.
///   - Mode fichier (développement, Ecrans:ModeCodes=fichier, ou envoi fixé par les tests) : prioritaire, la base n'est pas lue.
///   - courriel : e-mail à l'adresse du compte ; sms : SMS (aucun fournisseur : refus clair, noté au journal) ; les_deux : e-mail
///     ET SMS (le SMS indisponible n'empêche pas l'e-mail).
///   - Réglage incomplet ou illisible (mot de passe non déchiffrable, dossier des clés absent) : repli (fichier en développement ;
///     en production, variables PHARMA_CLOUD_SMTP_* si elles sont complètes, comme avant le lot S7.6), sinon refus clair.
/// </summary>
public sealed class ChoixEnvoiCodes
{
    public const string RefusIncomplet = "reglage_incomplet";
    public static readonly TimeSpan DureeMemoire = TimeSpan.FromSeconds(10);

    private readonly IEnvoiCodeDistant? fixe;
    private readonly IEnvoiCodeDistant? repli;
    private readonly Func<string, CancellationToken, Task<ReglageEnvoiCodes?>> lire;
    private readonly Func<ReglagesSmtpCentral, IEnvoiCourriel> fabrique;
    private readonly IEnvoiCodeDistant sms;
    private readonly TimeProvider horloge;
    private readonly ILogger? journal;
    private readonly Lock verrou = new();
    private (DateTimeOffset Le, ReglageEnvoiCodes Reglage)? memoire;

    /// <param name="fixe">Envoi imposé (mode fichier du développement, tests) : la base n'est pas lue.</param>
    /// <param name="repli">Envoi si le réglage en base est incomplet (fichier en développement ; SMTP des variables en production ; null : refus).</param>
    /// <param name="lire">Lecture du réglage en base pour une session (null : aucune session valable).</param>
    /// <param name="protection">Déchiffrement du mot de passe SMTP (null : dossier des clés absent, réglage inutilisable).</param>
    /// <param name="fabrique">Envoi SMTP pour des réglages (MailKit ; remplacé par un envoi factice dans les tests).</param>
    public ChoixEnvoiCodes(IEnvoiCodeDistant? fixe, IEnvoiCodeDistant? repli, Func<string, CancellationToken, Task<ReglageEnvoiCodes?>> lire,
        ProtectionSmtpCentral? protection, Func<ReglagesSmtpCentral, IEnvoiCourriel> fabrique, TimeProvider horloge, ILogger? journal = null,
        IEnvoiCodeDistant? sms = null, IEnvoiCodeDistant? copieFichier = null)
    {
        (this.fixe, this.repli, this.lire, Protection, this.fabrique, this.horloge, this.journal) = (fixe, repli, lire, protection, fabrique, horloge, journal);
        this.sms = sms ?? new EnvoiCodeSmsIndisponible();
        this.copieFichier = copieFichier;
    }

    /// <summary>Développement (mode fichier) : chaque code est écrit dans ce fichier EN PLUS du canal du réglage en base (s'il est complet).</summary>
    private readonly IEnvoiCodeDistant? copieFichier;

    public ProtectionSmtpCentral? Protection { get; }

    /// <summary>Un fournisseur SMS existe (aucun aujourd'hui) : sans lui, « SMS » ne s'enregistre pas et « les deux » envoie l'e-mail seul.</summary>
    public bool SmsDisponible => sms is not EnvoiCodeSmsIndisponible;

    /// <summary>Envoi SMTP pour des réglages donnés (e-mail d'essai de l'écran « Envoi des codes »).</summary>
    public IEnvoiCourriel Courriel(ReglagesSmtpCentral smtp) => fabrique(smtp);

    /// <summary>Après un enregistrement : le prochain envoi relit la base.</summary>
    public void Oublier()
    {
        lock (verrou) memoire = null;
    }

    /// <summary>Envoi retenu pour la session (empreinte du jeton), ou null et la clé du message de refus.</summary>
    public async Task<(IEnvoiCodeDistant? Envoi, string? Refus)> ChoisirAsync(string empreinteJeton, CancellationToken ct)
    {
        if (fixe is not null) return (fixe, null);
        ReglageEnvoiCodes? reglage;
        lock (verrou) reglage = memoire is { } m && horloge.GetUtcNow() - m.Le < DureeMemoire ? m.Reglage : null;
        if (reglage is null)
        {
            reglage = await lire(empreinteJeton, ct);
            if (reglage is not null) lock (verrou) memoire = (horloge.GetUtcNow(), reglage);
        }
        if (copieFichier is not null)
        {
            // Développement : fichier toujours, e-mail en plus si le réglage est complet (sinon le fichier seul, sans avertissement)
            if (reglage is not { Complet: true }) return (copieFichier, null);
            var (envoi, _) = Construire(reglage);
            return (envoi is null or EnvoiCodeFichier ? copieFichier : new EnvoiCodeAvecCopieFichier(envoi, copieFichier, journal), null);
        }
        return Construire(reglage);
    }

    /// <summary>
    /// Envoi pour un réglage (règle pure, testée) : canal respecté ; réglage incomplet ou illisible : repli, sinon refus.
    /// Avertissements (réglage incomplet, mot de passe non déchiffrable) écrits UNE fois par changement d'état (2026-11-22) : au
    /// premier envoi qui tombe sur ce réglage, puis seulement quand le réglage relu change ou redevient utilisable puis illisible
    /// (auparavant : à chaque code envoyé par le repli).
    /// </summary>
    public (IEnvoiCodeDistant? Envoi, string? Refus) Construire(ReglageEnvoiCodes? reglage)
    {
        string? causeDechiffrement = null;
        if (reglage is { Complet: true })
        {
            // Sans fournisseur SMS, « SMS » (réglage enregistré avant ce contrôle) et « les deux » passent par l'e-mail : un canal
            // indisponible ne doit jamais empêcher toute connexion, éditeur compris
            var canal = SmsDisponible ? reglage.Canal : reglage.Canal is "sms" or "les_deux" ? "courriel" : reglage.Canal;
            if (canal == "sms")
            {
                Retabli();
                return (sms, null);
            }
            if (canal is "courriel" or "les_deux")
            {
                (var smtp, causeDechiffrement) = SmtpEtCause(reglage, null);
                if (smtp is not null)
                {
                    Retabli();
                    var courriel = new EnvoiCodeCourriel(fabrique(smtp));
                    return (canal == "courriel" ? courriel : new EnvoiCodeLesDeux(courriel, sms), null);
                }
            }
        }
        if (NouvelEtatIllisible(reglage))
        {
            if (causeDechiffrement is not null) journal?.LogWarning("Envoi des codes : mot de passe SMTP non déchiffrable ({Cause})", causeDechiffrement);
            journal?.LogWarning("Envoi des codes : réglage en base incomplet ou illisible ({Repli})", repli is null ? "aucun repli : refus" : "repli " + repli.Canal);
        }
        return repli is not null ? (repli, null) : (null, RefusIncomplet);
    }

    // Dernier réglage illisible déjà signalé au journal (égalité du record : canal, serveur, port, TLS, identifiant, expéditeur,
    // texte chiffré) ; Note = false : aucun avertissement en cours
    private (bool Note, ReglageEnvoiCodes? Reglage) signale;

    /// <summary>Vrai si ce réglage illisible n'a pas encore été signalé (premier envoi, ou réglage relu différent).</summary>
    private bool NouvelEtatIllisible(ReglageEnvoiCodes? reglage)
    {
        lock (verrou)
        {
            if (signale.Note && Equals(signale.Reglage, reglage)) return false;
            signale = (true, reglage);
            return true;
        }
    }

    /// <summary>Réglage de nouveau utilisable : un prochain réglage illisible sera signalé.</summary>
    private void Retabli()
    {
        lock (verrou) signale = default;
    }

    /// <summary>Réglages SMTP d'un réglage en base (mot de passe déchiffré), ou null s'il est incomplet ou ne se déchiffre pas.</summary>
    public ReglagesSmtpCentral? Smtp(ReglageEnvoiCodes reglage, string? motDePasseSaisi = null)
    {
        var (smtp, cause) = SmtpEtCause(reglage, motDePasseSaisi);
        if (cause is not null) journal?.LogWarning("Envoi des codes : mot de passe SMTP non déchiffrable ({Cause})", cause);
        return smtp;
    }

    /// <summary>Réglages SMTP et, si le mot de passe enregistré ne se déchiffre pas, la cause (jamais le texte ni le mot de passe).</summary>
    private (ReglagesSmtpCentral? Smtp, string? CauseDechiffrement) SmtpEtCause(ReglageEnvoiCodes reglage, string? motDePasseSaisi)
    {
        string? motDePasse = motDePasseSaisi is { Length: > 0 } ? motDePasseSaisi : null;
        string? cause = null;
        if (motDePasse is null && reglage.MotDePasseChiffre is { Length: > 0 } chiffre)
        {
            // Protection indisponible (dossier illisible, DPAPI) : mot de passe illisible, jamais d'exception (ProtectionSmtpCentral.Lire)
            (motDePasse, cause) = Protection is null ? (null, "dossier des clés du central absent") : Protection.Lire(chiffre);
            if (motDePasse is null) cause ??= ProtectionSmtpCentral.CauseAutresCles;
        }
        return (ReglagesSmtpCentral.Creer(reglage.Serveur, reglage.Port, reglage.Tls, reglage.Identifiant, motDePasse, reglage.Expediteur).Reglages, cause);
    }
}
