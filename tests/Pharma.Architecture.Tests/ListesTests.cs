namespace Pharma.Architecture.Tests;

/// <summary>
/// Règle des listes (docs/charte-graphique.md) : tout tableau dont le nombre de lignes peut grandir est paginé et filtré.
/// Un composant qui affiche plus de tableaux que de paginations doit figurer ci-dessous, avec le nombre de tableaux non paginés
/// admis et la raison (tableau borné par nature, ou exception choisie par le pharmacien). Un nouveau tableau sans pagination
/// fait échouer le test : soit on le pagine, soit on justifie l'exception ici.
/// </summary>
public class ListesTests
{
    /// <summary>Composant → (tableaux non paginés admis, raison).</summary>
    private static readonly Dictionary<string, (int Admis, string Raison)> Exceptions = new()
    {
        ["Achats/EtiquetagerLots.razor"] = (1, "reçoit une page déjà paginée par l'écran qui l'affiche"),
        ["Achats/OngletCommandes.razor"] = (1, "lignes d'un bon de commande (détail d'un document)"),
        ["Achats/OngletReceptions.razor"] = (1, "lignes de la livraison en cours de saisie (détail d'un document)"),
        ["Achats/OngletReglements.razor"] = (2, "factures restant dues d'un grossiste, et récapitulatif des 20 derniers règlements (historique complet : relevé paginé)"),
        ["Achats/OngletRetours.razor"] = (1, "lignes d'un retour fournisseur (détail d'un document)"),
        ["Achats/PanneauInventaireComplet.razor"] = (1, "rayons de l'officine (bornés)"),
        ["Caisse/FenetreRapprochementVolet.razor"] = (1, "lignes d'un volet Chifa face à celles d'une vente (détail d'un document)"),
        ["Mise/JournalFiscal.razor"] = (2, "récapitulatif TVA par taux (borné) et sceaux de la période (une clôture par caisse et par jour, période limitée à un an, cadre défilant)"),
        ["Mise/FenetreAutorisation.razor"] = (1, "détail d'une demande d'autorisation : lignes du panier concernées (détail d'un document)"),
        ["Factures/EditeurFacture.razor"] = (1, "ventes à cocher pour une facture (période et destinataire choisis)"),
        ["Factures/PanneauMedecins.razor"] = (1, "résultats d'une recherche (dès 2 lettres, plafonnée par le serveur)"),
        ["Pages/AdminConflitsFactureChifa.razor"] = (1, "conflits du script 101 (2026-11-15) : liste figée par une reprise unique, bornée par nature (0 attendu)"),
        ["Mise/IntegriteJournaux.razor"] = (1, "journaux chaînés : bornés (un par journal protégé et clé de chaîne, 2026-11-16)"),
        ["Pages/AdminSecondServeur.razor"] = (1, "serveurs de l'officine : bornés par nature (le principal et le 2e serveur, 2026-11-16)"),
        ["Pages/AdminDroits.razor"] = (2, "utilisateurs : exception choisie le 2026-11-13 ; droits par rôle (bornés)"),
        ["Pages/AdminNotifications.razor"] = (1, "destinataires des notifications (quelques personnes)"),
        ["Pages/AdminSynchronisation.razor"] = (3, "11 tables de réglages que le serveur central peut mutualiser : liste fermée, bornée par nature (2026-11-16, lot M7) ; "
            + "intégrité au central (lot S4, 2026-11-17) : 20 dernières anomalies au plus (plafond du serveur central) ; "
            + "restaurations constatées (lot S6.5, 2026-11-19) : une ligne par restauration de la base, rare par nature (plus de 2 en 12 mois : « à justifier »)"),
        ["Pages/AdminPsychotropes.razor"] = (1, "règles d'alerte du registre des psychotropes (quatre, bornées par le script 117, 2026-11-16)"),
        ["Pages/AdminReprise.razor"] = (2, "champs d'une cible de reprise et vérification après chargement (bornés)"),
        ["Pages/AdminProfilsReprise.razor"] = (1, "champs d'une cible dans la fenêtre d'édition d'un profil de reprise (bornés, 2026-11-16)"),
        ["Pages/BordereauxChifa.razor"] = (2, "fiche d'un bordereau (détail d'un document) : ses paiements (quelques virements) et son historique (200 dernières entrées) ; ses factures sont paginées"),
        ["Pages/Dettes.razor"] = (1, "échéancier par ancienneté (synthèse de trois tranches)"),
        ["Pages/EtatCaisse.razor"] = (2, "ventes par employé et comptages intermédiaires d'une session de caisse"),
        ["Pages/Factures.razor"] = (2, "lignes d'une facture et ses versions (détail d'un document)"),
        ["Pages/PilotageFinancier.razor"] = (2, "rejets Chifa par motif (motifs réglables, bornés) et TVA par taux (bornée), 2026-11-16, lot 11"),
        ["Pages/Rapports.razor"] =(2, "ventes par jour d'une période (un an au plus) et récapitulatifs des pertes et dons (synthèses de la période)"),
    };

    [Fact]
    public void Chaque_tableau_qui_peut_grandir_est_pagine()
    {
        var composants = Path.Combine(RacineDepot(), "src", "Pharma.Desktop", "Composants");
        var ecarts = new List<string>();
        foreach (var f in Directory.GetFiles(composants, "*.razor", SearchOption.AllDirectories))
        {
            var texte = File.ReadAllText(f);
            var nonPagines = Compter(texte, "<table") - Compter(texte, "<Pagination ");
            if (nonPagines <= 0) continue;
            var nom = Path.GetRelativePath(composants, f).Replace('\\', '/');
            if (!Exceptions.TryGetValue(nom, out var e)) ecarts.Add($"{nom} : {nonPagines} tableau(x) sans pagination, ni exception justifiée");
            else if (nonPagines > e.Admis) ecarts.Add($"{nom} : {nonPagines} tableau(x) sans pagination pour {e.Admis} admis ({e.Raison})");
        }
        Assert.True(ecarts.Count == 0, "Règle des listes : " + string.Join(" ; ", ecarts));
    }

    [Fact]
    public void Les_exceptions_designent_des_composants_existants()
    {
        var composants = Path.Combine(RacineDepot(), "src", "Pharma.Desktop", "Composants");
        var absents = Exceptions.Keys.Where(n => !File.Exists(Path.Combine(composants, n))).ToList();
        Assert.True(absents.Count == 0, "Exceptions sans composant (renommé ou supprimé) : " + string.Join(", ", absents));
    }

    private static int Compter(string texte, string motif)
    {
        var n = 0;
        for (var i = texte.IndexOf(motif, StringComparison.Ordinal); i >= 0; i = texte.IndexOf(motif, i + motif.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string RacineDepot()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}
