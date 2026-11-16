using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pharma.Domain.Commun;
using Pharma.Domain.Referentiels;

namespace Pharma.Domain.Reglages;

/// <summary>
/// Validation d'un modèle de réglages complet (format mutu-v1, docs/conception-synchronisation.md § 16 bis.3), tout ou rien par table (D9) :
/// renvoie la liste des erreurs en français (liste vide = modèle valide). Les mêmes règles métier que la saisie locale (classes Regle*)
/// sont appliquées ; les contrôles qui demandent la base de l'officine (catégorie existante, libellé déjà pris par un motif local, motif
/// système, chevauchement avec une remise locale) se font à l'application du modèle, pas ici.
/// Lecture stricte : un champ inconnu, manquant ou d'un mauvais type est une erreur ; aucun identifiant propre à une officine n'est admis.
/// </summary>
public static partial class ValidationModele
{
    public const string FormatV1 = "mutu-v1";
    public const string ModeImpose = "impose";
    public const string ModePropose = "propose";
    public const string ModeLocal = "local";
    /// <summary>Taille maximale d'un élément (octets UTF-8), comme la voie descendante (desc-v1).</summary>
    public const int TailleMax = 64 * 1024;
    public const int AuteurMax = 100;

    private static readonly string[] ChampsEnveloppe = ["format", "mode", "version_modele", "ajouts_locaux", "publie_le", "auteur", "valeur"];

    /// <summary>Tables dont l'élément porte « ajouts_locaux » (ajouts locaux permis ou non en mode imposé, D5 et D6).</summary>
    public static bool PorteAjoutsLocaux(string table) => table is TablesMutualisables.Motifs or TablesMutualisables.Remises;

    /// <summary>
    /// Élément complet : taille, enveloppe (format, mode, version, auteur, date) et valeur de la table.
    /// <paramref name="aujourdhui"/> : à donner à la publication pour refuser une remise déjà terminée ; à la réception, null (un modèle
    /// publié reste valide quand une de ses remises arrive à son terme).
    /// </summary>
    public static IReadOnlyList<string> ValiderElement(string table, string? json, DateOnly? aujourdhui = null)
    {
        var erreurs = new List<string>();
        if (!TablesMutualisables.Connue(table))
            return [$"Table « {table} » : elle ne fait pas partie des réglages mutualisables."];
        if (string.IsNullOrWhiteSpace(json)) return ["Le modèle est vide."];
        if (Encoding.UTF8.GetByteCount(json) > TailleMax)
            return [$"Le modèle dépasse {TailleMax / 1024} Kio : réduisez-le."];

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException)
        {
            return ["Le modèle n’est pas un JSON valide."];
        }
        using (doc)
        {
            var racine = Objet.De(doc.RootElement, erreurs, "Modèle");
            if (racine is null) return erreurs;

            var format = racine.Texte("format");
            if (format is not null && format != FormatV1) erreurs.Add($"Format « {format} » non pris en charge (attendu : {FormatV1}).");
            var mode = racine.Texte("mode");
            if (mode is not null && mode is not (ModeImpose or ModePropose or ModeLocal))
                erreurs.Add("Mode : « impose », « propose » ou « local ».");
            var version = racine.Entier("version_modele");
            if (version is < 1) erreurs.Add("Version du modèle : un nombre entier d’au moins 1.");

            if (mode == ModeLocal)
            {
                // Libération : ni valeur ni date ni auteur
                foreach (var nom in ChampsEnveloppe.Where(n => n is not ("format" or "mode" or "version_modele")))
                    if (racine.Contient(nom))
                    {
                        erreurs.Add($"Mode « local » : le champ « {nom} » n’est pas admis.");
                        racine.Ignorer(nom);
                    }
                racine.Fin(erreurs);
                return erreurs;
            }

            if (mode is ModeImpose or ModePropose)
            {
                var auteur = racine.Texte("auteur");
                if (auteur is not null && (auteur.Trim().Length == 0 || auteur.Length > AuteurMax))
                    erreurs.Add($"Auteur : de 1 à {AuteurMax} caractères.");
                var publie = racine.Texte("publie_le");
                if (publie is not null && !DateTime.TryParseExact(publie, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _))
                    erreurs.Add("Date de publication : AAAA-MM-JJTHH:MM:SSZ (heure UTC).");
                if (PorteAjoutsLocaux(table)) racine.Booleen("ajouts_locaux");
                else if (racine.Contient("ajouts_locaux"))
                {
                    erreurs.Add("« ajouts_locaux » n’existe que pour les motifs et les remises automatiques.");
                    racine.Ignorer("ajouts_locaux");
                }
                if (racine.Valeur("valeur") is { } valeur) erreurs.AddRange(ValiderValeur(table, valeur, aujourdhui));
            }
            else foreach (var nom in ChampsEnveloppe) racine.Ignorer(nom);
            racine.Fin(erreurs);
        }
        return erreurs;
    }

    /// <summary>Valeur d'une table (objet « valeur » de l'élément) : règles de contrôle et forme.</summary>
    public static IReadOnlyList<string> ValiderValeur(string table, JsonElement valeur, DateOnly? aujourdhui = null)
    {
        var erreurs = new List<string>();
        switch (table)
        {
            case TablesMutualisables.Fiscal: Fiscal(valeur, erreurs); break;
            case TablesMutualisables.AlertesPsychotropes: AlertesPsychotropes(valeur, erreurs); break;
            case TablesMutualisables.Tracabilite: Tracabilite(valeur, erreurs); break;
            case TablesMutualisables.Serialisation: Serialisation(valeur, erreurs); break;
            case TablesMutualisables.Substitution: Substitution(valeur, erreurs); break;
            case TablesMutualisables.Motifs: Motifs(valeur, erreurs); break;
            case TablesMutualisables.FormulaireSms: FormulaireSms(valeur, erreurs); break;
            case TablesMutualisables.Surveillance: Surveillance(valeur, erreurs); break;
            case TablesMutualisables.Alertes: Alertes(valeur, erreurs); break;
            case TablesMutualisables.Inventaire: Inventaire(valeur, erreurs); break;
            case TablesMutualisables.Remises: Remises(valeur, erreurs, aujourdhui); break;
            default: erreurs.Add($"Table « {table} » : elle ne fait pas partie des réglages mutualisables."); break;
        }
        return erreurs;
    }

    // ---- Tables à ligne unique ------------------------------------------------------------

    private static void Fiscal(JsonElement v, List<string> erreurs)
    {
        var o = Objet.De(v, erreurs, "Réglage fiscal");
        if (o is null) return;
        var annees = o.Entier("conservation_annees");
        var format = o.Texte("format_export_dgi");
        var separateur = o.Texte("separateur_csv");
        var fuseau = o.Texte("fuseau");
        if (annees is { } a) Essayer(erreurs, () => RegleFiscale.VerifierConservation(a));
        if (format is not null) Essayer(erreurs, () => RegleFiscale.VerifierFormat(format));
        if (separateur is not null) Essayer(erreurs, () => RegleFiscale.VerifierSeparateur(separateur));
        if (fuseau is not null) Essayer(erreurs, () => RegleFiscale.VerifierFuseau(fuseau));
        o.Fin(erreurs);
    }

    private static void Tracabilite(JsonElement v, List<string> erreurs)
    {
        var o = Objet.De(v, erreurs, "Traçabilité");
        if (o is null) return;
        o.Booleen("boite_obligatoire");
        var patient = o.Texte("patient_obligatoire");
        if (patient is not null) Essayer(erreurs, () => RegleTracabilite.VerifierPatientObligatoire(patient));
        o.Fin(erreurs);
    }

    private static void Serialisation(JsonElement v, List<string> erreurs)
    {
        // Le connecteur est fixé à « factice » (contrainte de la base) : il ne fait pas partie du modèle
        var o = Objet.De(v, erreurs, "Sérialisation");
        if (o is null) return;
        o.Booleen("actif");
        o.Booleen("factice_hors_ligne");
        o.Fin(erreurs);
    }

    private static void Substitution(JsonElement v, List<string> erreurs)
    {
        var o = Objet.De(v, erreurs, "Substitution");
        if (o is null) return;
        o.Booleen("equivalent_exact_seulement");
        o.Fin(erreurs);
    }

    private static void FormulaireSms(JsonElement v, List<string> erreurs)
    {
        var o = Objet.De(v, erreurs, "Formulaire d’accord SMS");
        if (o is null) return;
        var avant = erreurs.Count;
        var texte = o.TexteOuNull("texte");
        if (erreurs.Count == avant) Essayer(erreurs, () => RegleFormulaireSms.VerifierLongueur(texte));
        o.Fin(erreurs);
    }

    private static void Surveillance(JsonElement v, List<string> erreurs)
    {
        // Les horaires d'ouverture restent propres à chaque officine (D7) : champs refusés par la lecture stricte
        var o = Objet.De(v, erreurs, "Contrôle interne");
        if (o is null) return;
        var avant = erreurs.Count;
        var s = new SeuilsSurveillance(
            o.Entier("seuil_lignes_retirees") ?? 0, o.Entier("seuil_tiroir") ?? 0, o.Entier("seuil_attentes_abandonnees") ?? 0,
            o.Entier("seuil_remises") ?? 0, o.Entier("seuil_remises_patient") ?? 0, o.Entier("seuil_ajustements") ?? 0,
            o.Entier("seuil_casses") ?? 0, o.Entier("seuil_echantillons") ?? 0, o.Entier("jours_comptage_sensibles") ?? 0,
            o.Booleen("reception_aveugle") ?? false, o.Entier("jours_avoir_retour") ?? 0, o.Montant("seuil_credit_nouveau_patient") ?? 0,
            o.Entier("jours_patient_recent") ?? 0, o.Montant("seuil_credits_jour") ?? 0, o.Entier("jours_credit_sans_remboursement") ?? 0,
            o.Entier("seuil_autorisations_valideur") ?? 0, o.Entier("seuil_autorisations_employe") ?? 0, o.Entier("minutes_postes_simultanes") ?? 0,
            o.Entier("seuil_changements_poste") ?? 0, o.Entier("seuil_baisses_prix") ?? 0, o.Entier("seuil_sorties_tiroir") ?? 0);
        if (erreurs.Count == avant) Essayer(erreurs, () => RegleSurveillance.VerifierSeuils(s));
        o.Fin(erreurs);
    }

    private static void Alertes(JsonElement v, List<string> erreurs)
    {
        var o = Objet.De(v, erreurs, "Seuils d’alerte");
        if (o is null) return;
        var avant = erreurs.Count;
        var orange = o.Entier("jours_orange") ?? 0;
        var rouge = o.Entier("jours_rouge") ?? 0;
        var horizon = o.Entier("horizon_risque_jours") ?? 0;
        var delai = o.Entier("delai_fournisseur_jours") ?? 0;
        var securite = o.Entier("jours_securite") ?? 0;
        var cible = o.Entier("jours_cible_commande") ?? 0;
        var dormant = o.Entier("jours_dormant") ?? 0;
        var lot = o.Entier("seuil_lot_delaisse") ?? 0;
        var lotCollectif = o.Entier("seuil_lot_delaisse_collectif") ?? 0;
        if (erreurs.Count == avant)
            Essayer(erreurs, () => Pharma.Domain.Alertes.RegleSeuils.VerifierReglageGeneral(orange, rouge, horizon, delai, securite, cible, dormant, lot, lotCollectif));
        o.Fin(erreurs);
    }

    private static void Inventaire(JsonElement v, List<string> erreurs)
    {
        var o = Objet.De(v, erreurs, "Inventaire");
        if (o is null) return;
        var avant = erreurs.Count;
        var boites = o.Entier("seuil_boites") ?? 0;
        var montant = o.Montant("seuil_montant") ?? 0;
        if (erreurs.Count == avant) Essayer(erreurs, () => RegleInventaire.VerifierSeuils(boites, montant));
        o.Fin(erreurs);
    }

    // ---- Tables à plusieurs lignes ------------------------------------------------------------

    private static void AlertesPsychotropes(JsonElement v, List<string> erreurs)
    {
        var o = Objet.De(v, erreurs, "Alertes des psychotropes");
        if (o is null) return;
        var vus = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (e, i) in o.Tableau("regles"))
        {
            var r = Objet.De(e, erreurs, $"Règle {i + 1}");
            if (r is null) continue;
            var avant = erreurs.Count;
            var code = r.Texte("code");
            r.Booleen("active");
            var seuil = r.Entier("seuil");
            var periode = r.Entier("periode_jours");
            r.Booleen("exiger_valideur");
            var unites = r.EntierOuNull("unites_par_jour");
            r.Fin(erreurs);
            if (erreurs.Count != avant || code is null) continue;
            if (!vus.Add(code)) erreurs.Add($"Règle « {code} » en double.");
            Essayer(erreurs, () => RegleAlertesPsychotropes.VerifierRegle(code, RegleAlertesPsychotropes.Nom(code), seuil ?? 0, periode ?? 0, unites));
        }
        foreach (var manquant in RegleAlertesPsychotropes.Codes.Where(c => !vus.Contains(c)).Order(StringComparer.Ordinal))
            erreurs.Add($"Règle « {manquant} » absente : les {RegleAlertesPsychotropes.Codes.Count} règles sont obligatoires.");
        o.Fin(erreurs);
    }

    private static void Motifs(JsonElement v, List<string> erreurs)
    {
        var o = Objet.De(v, erreurs, "Motifs");
        if (o is null) return;
        var valides = new List<(string Liste, string Code, string Libelle)>();
        foreach (var (e, i) in o.Tableau("motifs"))
        {
            var m = Objet.De(e, erreurs, $"Motif {i + 1}");
            if (m is null) continue;
            var avant = erreurs.Count;
            var liste = m.Texte("liste");
            var code = m.Texte("code");
            var libelle = m.Texte("libelle");
            m.Booleen("actif");
            var ordre = m.Entier("ordre");
            var effet = m.Texte("effet");
            var detail = m.Texte("detail");
            m.Fin(erreurs);
            if (erreurs.Count != avant || liste is null || code is null || libelle is null || effet is null || detail is null) continue;

            var nom = $"Motif « {code} » ({liste})";
            var local = new List<string>();
            Essayer(local, () => RegleMotifs.ExigerListe(liste));
            Essayer(local, () => RegleMotifs.VerifierCode(code));
            string lib = libelle.Trim();
            Essayer(local, () => lib = RegleMotifs.VerifierLibelle(libelle));
            Essayer(local, () => RegleTiroirVerifier(liste, effet, detail));
            if (ordre is < 0 or > RegleMotifs.OrdreMax) local.Add($"Ordre : de 0 à {RegleMotifs.OrdreMax}.");
            // Un motif nouveau du central n'existe que dans les listes qui acceptent l'ajout (D5)
            if (RegleMotifs.EstCodeCentral(code) && RegleMotifs.ListeConnue(liste)) Essayer(local, () => RegleMotifs.ExigerAjoutPermis(liste));
            erreurs.AddRange(local.Select(x => $"{nom} : {x}"));
            valides.Add((liste, code, lib));
        }
        foreach (var g in valides.GroupBy(m => (m.Liste, m.Code)).Where(g => g.Count() > 1))
            erreurs.Add($"Motif « {g.Key.Code} » ({g.Key.Liste}) en double.");
        foreach (var g in valides.GroupBy(m => m.Liste))
        {
            var vus = new List<string>();
            foreach (var m in g)
            {
                Essayer(erreurs, () => RegleMotifs.ExigerLibelleUnique(m.Libelle, vus), $"Liste « {g.Key} »");
                vus.Add(m.Libelle);
            }
        }
        o.Fin(erreurs);
    }

    private static void RegleTiroirVerifier(string liste, string effet, string detail)
    {
        var (e, _) = Caisse.RegleTiroir.VerifierEffet(effet, detail);
        RegleMotifs.ExigerEffetAutorise(liste, e);
    }

    private static void Remises(JsonElement v, List<string> erreurs, DateOnly? aujourdhui)
    {
        // Catégories seulement (D6) : un produit est un identifiant propre à l'officine, refusé par la lecture stricte
        var o = Objet.De(v, erreurs, "Remises automatiques");
        if (o is null) return;
        var valides = new List<(string Cle, string Categorie, DateOnly Debut, DateOnly Fin)>();
        foreach (var (e, i) in o.Tableau("remises"))
        {
            var r = Objet.De(e, erreurs, $"Remise {i + 1}");
            if (r is null) continue;
            var avant = erreurs.Count;
            var cle = r.Texte("cle");
            var categorie = r.Texte("categorie");
            var pourcentage = r.Nombre("pourcentage");
            var debut = r.Date("date_debut");
            var fin = r.Date("date_fin");
            var motif = r.TexteOuNull("motif");
            r.Fin(erreurs);
            if (erreurs.Count != avant || cle is null || categorie is null || pourcentage is null || debut is null || fin is null) continue;

            var nom = $"Remise « {cle} »";
            var local = new List<string>();
            if (!CleRemise().IsMatch(cle)) local.Add("identifiant : « c_ » suivi de 12 minuscules ou chiffres.");
            if (string.IsNullOrWhiteSpace(categorie)) local.Add("une remise du serveur central vise une catégorie.");
            Essayer(local, () => RegleRemisesAutomatiques.VerifierPourcentage(pourcentage.Value));
            Essayer(local, () => RegleRemisesAutomatiques.VerifierPeriode(debut.Value, fin.Value, aujourdhui));
            Essayer(local, () => RegleRemisesAutomatiques.NettoyerMotif(motif));
            erreurs.AddRange(local.Select(x => $"{nom} : {x}"));
            if (!string.IsNullOrWhiteSpace(categorie)) valides.Add((cle, categorie.Trim(), debut.Value, fin.Value));
        }
        foreach (var g in valides.GroupBy(r => r.Cle).Where(g => g.Count() > 1))
            erreurs.Add($"Remise « {g.Key} » en double.");
        foreach (var g in valides.GroupBy(r => r.Categorie, StringComparer.Ordinal))
        {
            var l = g.ToList();
            for (var a = 0; a < l.Count; a++)
                for (var b = a + 1; b < l.Count; b++)
                    if (l[a].Cle != l[b].Cle && RegleRemisesAutomatiques.Chevauche(l[a].Debut, l[a].Fin, l[b].Debut, l[b].Fin))
                        erreurs.Add($"Remises « {l[a].Cle} » et « {l[b].Cle} » : la catégorie « {g.Key} » a déjà une remise automatique sur une partie de cette période.");
        }
        o.Fin(erreurs);
    }

    // ---- Outils ------------------------------------------------------------------------------

    private static void Essayer(List<string> erreurs, Action regle, string? prefixe = null)
    {
        try { regle(); }
        catch (RegleMetierException ex) { erreurs.Add(prefixe is null ? ex.Message : $"{prefixe} : {ex.Message}"); }
    }

    [GeneratedRegex("^c_[a-z0-9]{12}$")]
    private static partial Regex CleRemise();

    /// <summary>Champs qui désignent une donnée propre à une officine : refusés avec un message explicite.</summary>
    private static readonly HashSet<string> ChampsPropresOfficine = new(StringComparer.Ordinal)
        { "id", "officine_id", "produit_id", "cree_par", "cree_le", "modifie_par", "modifie_le", "modifiee_par", "modifiee_le", "arretee_par", "arretee_le",
          "heure_ouverture", "heure_fermeture" };

    /// <summary>Lecture stricte d'un objet JSON : types vérifiés, champs lus mémorisés, champs restants refusés.</summary>
    private sealed class Objet
    {
        private readonly JsonElement _e;
        private readonly List<string> _erreurs;
        private readonly string _contexte;
        private readonly HashSet<string> _lus = new(StringComparer.Ordinal);

        private Objet(JsonElement e, List<string> erreurs, string contexte) => (_e, _erreurs, _contexte) = (e, erreurs, contexte);

        public static Objet? De(JsonElement e, List<string> erreurs, string contexte)
        {
            if (e.ValueKind == JsonValueKind.Object) return new Objet(e, erreurs, contexte);
            erreurs.Add($"{contexte} : un objet JSON est attendu.");
            return null;
        }

        public void Ignorer(string nom) => _lus.Add(nom);

        public bool Contient(string nom) => _e.TryGetProperty(nom, out _);

        private JsonElement? Champ(string nom)
        {
            _lus.Add(nom);
            if (_e.TryGetProperty(nom, out var v)) return v;
            _erreurs.Add($"{_contexte} : le champ « {nom} » est manquant.");
            return null;
        }

        public JsonElement? Valeur(string nom) => Champ(nom);

        public string? Texte(string nom)
        {
            if (Champ(nom) is not { } v) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            _erreurs.Add($"{_contexte} : « {nom} » doit être un texte.");
            return null;
        }

        /// <summary>Texte ou null explicite (le champ reste obligatoire pour que la forme soit déterministe).</summary>
        public string? TexteOuNull(string nom)
        {
            if (Champ(nom) is not { } v) return null;
            if (v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            _erreurs.Add($"{_contexte} : « {nom} » doit être un texte ou null.");
            return null;
        }

        public int? Entier(string nom)
        {
            if (Champ(nom) is not { } v) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            _erreurs.Add($"{_contexte} : « {nom} » doit être un nombre entier.");
            return null;
        }

        public int? EntierOuNull(string nom)
        {
            if (Champ(nom) is not { } v) return null;
            if (v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            _erreurs.Add($"{_contexte} : « {nom} » doit être un nombre entier ou null.");
            return null;
        }

        public decimal? Nombre(string nom)
        {
            if (Champ(nom) is not { } v) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
            _erreurs.Add($"{_contexte} : « {nom} » doit être un nombre.");
            return null;
        }

        /// <summary>Montant de la base (numeric(12,2)) : deux décimales au plus, moins de dix chiffres avant la virgule.</summary>
        public decimal? Montant(string nom)
        {
            var n = Nombre(nom);
            if (n is null) return null;
            if (decimal.Round(n.Value, 2) != n.Value || Math.Abs(n.Value) > 9_999_999_999.99m)
            {
                _erreurs.Add($"{_contexte} : « {nom} » est un montant à deux décimales au plus (jusqu’à 9 999 999 999,99).");
                return null;
            }
            return n;
        }

        public bool? Booleen(string nom)
        {
            if (Champ(nom) is not { } v) return null;
            if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
            _erreurs.Add($"{_contexte} : « {nom} » doit être vrai ou faux.");
            return null;
        }

        public DateOnly? Date(string nom)
        {
            var t = Texte(nom);
            if (t is null) return null;
            if (DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            _erreurs.Add($"{_contexte} : « {nom} » doit être une date AAAA-MM-JJ.");
            return null;
        }

        /// <summary>Éléments d'un tableau d'objets, avec leur rang ; tableau absent ou mal typé : erreur et liste vide.</summary>
        public IEnumerable<(JsonElement Element, int Rang)> Tableau(string nom)
        {
            if (Champ(nom) is not { } v) return [];
            if (v.ValueKind != JsonValueKind.Array)
            {
                _erreurs.Add($"{_contexte} : « {nom} » doit être une liste.");
                return [];
            }
            return v.EnumerateArray().Select((e, i) => (e, i)).ToList();
        }

        /// <summary>Tout champ non lu est refusé (champ inconnu, ou identifiant propre à une officine).</summary>
        public void Fin(List<string> erreurs)
        {
            foreach (var p in _e.EnumerateObject())
            {
                if (_lus.Contains(p.Name)) continue;
                erreurs.Add(ChampsPropresOfficine.Contains(p.Name)
                    ? $"{_contexte} : « {p.Name} » est propre à chaque officine : il ne peut pas figurer dans un modèle."
                    : $"{_contexte} : champ inconnu « {p.Name} ».");
            }
        }
    }
}
