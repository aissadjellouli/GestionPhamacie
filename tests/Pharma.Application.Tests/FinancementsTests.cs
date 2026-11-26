using Pharma.Application.Achats;
using Pharma.Application.Commun;
using Pharma.Application.Financements;
using Pharma.Application.Suivi;
using Pharma.Domain.Achats;
using Pharma.Domain.Commun;
using Pharma.Domain.Financements;

namespace Pharma.Application.Tests;

internal sealed class FinancementsEnMemoire : IFinancements
{
    public static readonly DateOnly DatePret = new(2026, 1, 10);
    public PretResume Pret { get; set; } = new(5, 2, "Banque fictive", "PR-1", "Travaux", 300000, DatePret, null, 0, 300000, 0, 3, 0, 0, null, null, "Titulaire");
    public List<EcheancePret> Echeances { get; } = [];
    public List<PaiementEcheanceAEcrire> Paiements { get; } = [];
    public List<UtilisationAEcrire> Utilisations { get; } = [];
    public List<RemboursementTitulaireAEcrire> Remboursements { get; } = [];
    public (int Payees, IReadOnlyList<LigneEcheancier> Lignes)? Remplace { get; private set; }
    public decimal SoldeTitulaire { get; set; }

    public FinancementsEnMemoire()
    {
        LigneEcheancier[] lignes =
        [
            new(new DateOnly(2026, 2, 10), 100000, 3000, 570, 0, 103570, 200000),
            new(new DateOnly(2026, 3, 10), 100000, 2000, 0, 0, 102000, 100000),
            new(new DateOnly(2026, 4, 10), 100000, 1000, 0, 0, 101000, 0)
        ];
        for (var i = 0; i < lignes.Length; i++) Echeances.Add(new EcheancePret(100 + i, 5, "PR-1", "Banque fictive", i + 1, lignes[i]));
    }

    public Task<Page<PretResume>> PretsAsync(FiltrePrets f, DemandePage p, CancellationToken ct = default) => Task.FromResult(new Page<PretResume>([Pret], 1, 1, p.Taille));
    public Task<PretDetail?> PretAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(id == Pret.Id ? new PretDetail(Pret, Echeances, []) : null);
    public Task<long> CreerPretAsync(PretAEcrire p, CancellationToken ct = default) { PretsEcrits.Add(p); return Task.FromResult(6L); }
    public Task ModifierPretAsync(long id, PretAEcrire p, CancellationToken ct = default) { PretsEcrits.Add(p); return Task.CompletedTask; }
    public Task JoindreDocumentAsync(long id, string chemin, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemplacerEcheancierAsync(long pretId, int payees, IReadOnlyList<LigneEcheancier> lignes, CancellationToken ct = default)
    { Remplace = (payees, lignes); return Task.CompletedTask; }
    public Task<CorrespondanceColonnes?> CorrespondanceAsync(long? compteId, string? preteur, CancellationToken ct = default) => Task.FromResult<CorrespondanceColonnes?>(null);
    /// <summary>Banque ou prêteur hors trésorerie pour lequel les colonnes ont été retenues.</summary>
    public (long? CompteId, string? Preteur)? CorrespondanceRetenue { get; private set; }
    public Task EnregistrerCorrespondanceAsync(long? compteId, string? preteur, CorrespondanceColonnes c, long u, CancellationToken ct = default)
    { CorrespondanceRetenue = (compteId, preteur); return Task.CompletedTask; }
    public List<PretAEcrire> PretsEcrits { get; } = [];
    public Task<Page<EcheancePret>> EcheancesAsync(FiltreEcheances f, DemandePage p, CancellationToken ct = default) =>
        Task.FromResult(new Page<EcheancePret>(Echeances, Echeances.Count, 1, p.Taille));
    public Task<EcheancePret?> EcheanceAsync(long id, CancellationToken ct = default) => Task.FromResult(Echeances.FirstOrDefault(e => e.Id == id));
    public Task<long> PayerEcheanceAsync(PaiementEcheanceAEcrire p, CancellationToken ct = default) { Paiements.Add(p); return Task.FromResult(50L); }
    public List<RemboursementAnticipeAEcrire> Anticipes { get; } = [];
    public Task<long> RembourserParAnticipationAsync(RemboursementAnticipeAEcrire r, CancellationToken ct = default) { Anticipes.Add(r); return Task.FromResult(80L); }
    public Task<long> UtiliserAsync(UtilisationAEcrire u, CancellationToken ct = default) { Utilisations.Add(u); return Task.FromResult(60L); }
    public Task<CompteTitulaire> CompteTitulaireAsync(FiltreCompteTitulaire f, DemandePage p, CancellationToken ct = default) =>
        Task.FromResult(new CompteTitulaire(SoldeTitulaire, SoldeTitulaire, 0, new Page<MouvementTitulaire>([], 0, 1, p.Taille)));
    public Task<long> RembourserTitulaireAsync(RemboursementTitulaireAEcrire r, CancellationToken ct = default) { Remboursements.Add(r); return Task.FromResult(70L); }
    public Task<IReadOnlyList<SourceDisponible>> SourcesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SourceDisponible>>([new SourceDisponible("pret:5", "Prêt PR-1 (Banque fictive)", 300000)]);
}

public class FinancementsTests
{
    private static readonly HorlogeFixe Horloge = new(new DateOnly(2026, 11, 9));
    private static DroitsFactices Titulaire => new("tresorerie.gerer", "achats.regler") { Titulaire = true };
    private static DroitsFactices Employe => new("tresorerie.gerer", "achats.regler");

    private static PayerEcheancePret Payer(FinancementsEnMemoire f, JournalEspion j, DroitsFactices? droits = null, SessionsEnMemoire? sessions = null) =>
        new(f, sessions ?? new SessionsEnMemoire { Especes = 5000 }, droits ?? Titulaire, Horloge, j, new TresorerieEnMemoire());

    [Fact]
    public async Task Financements_reserves_au_titulaire()
    {
        var f = new FinancementsEnMemoire();
        await Assert.ThrowsAsync<DroitRefuseException>(() => new ConsulterFinancements(f, Employe, Horloge).PretsAsync(1, FiltrePrets.Lire(null, null, null), new DemandePage()));
        await Assert.ThrowsAsync<DroitRefuseException>(() => Payer(f, new JournalEspion(), Employe).ExecuterAsync(100, null, "especes", null, null, 0, 1, "Caisse 1"));
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new UtiliserFinancement(f, Employe, Horloge, new JournalEspion(), new TresorerieEnMemoire()).ExecuterAsync("titulaire", "tresorerie", null, 100, 1, null, null, null, 1));
        Assert.Empty(f.Paiements);
        // Sources : l'apport du titulaire d'abord, puis les prêts dont une part reste à employer
        var sources = await new ConsulterFinancements(f, Titulaire, Horloge).SourcesAsync(1);
        Assert.Equal(["titulaire", "pret:5"], sources.Select(s => s.Code));
    }

    [Fact]
    public async Task Echeance_payee_par_virement_banque_et_reference_obligatoires_parts_journalisees()
    {
        var f = new FinancementsEnMemoire();
        var j = new JournalEspion();
        Assert.Equal("compte", (await Assert.ThrowsAsync<RegleMetierException>(() => Payer(f, j).ExecuterAsync(100, null, "virement", null, "VIR-1", 0, 1, "Caisse 1"))).Champ);
        Assert.Equal("reference", (await Assert.ThrowsAsync<RegleMetierException>(() => Payer(f, j).ExecuterAsync(100, null, "virement", 2, " ", 0, 1, "Caisse 1"))).Champ);
        Assert.Equal("moyen", (await Assert.ThrowsAsync<RegleMetierException>(() => Payer(f, j).ExecuterAsync(100, null, "effet", 2, "X", 0, 1, "Caisse 1"))).Champ);
        Assert.Equal("tiroir", (await Assert.ThrowsAsync<RegleMetierException>(() => Payer(f, j).ExecuterAsync(100, null, "cheque", 2, "CHQ-1", 10, 1, "Caisse 1"))).Champ);
        Assert.Empty(f.Paiements);

        await Payer(f, j).ExecuterAsync(100, null, "prelevement", 2, " PRL-7 ", 0, 1, "Caisse 1");
        var p = Assert.Single(f.Paiements);
        Assert.Equal(("prelevement", (long?)2, "PRL-7", Horloge.Aujourdhui), (p.Moyen, p.CompteId, p.Reference, p.Date));
        Assert.Equal(["echeance_pret_payee"], j.Actions);
        Assert.Equal((100000m, 3570m), ((decimal)j.Details[0]["capital"]!, (decimal)j.Details[0]["charges_financieres"]!));
    }

    [Fact]
    public async Task Echeance_avec_assurance_payee_assurance_en_charges_financieres()
    {
        var f = new FinancementsEnMemoire();
        f.Echeances[0] = f.Echeances[0] with { Ligne = f.Echeances[0].Ligne with { Assurance = 450, Montant = 104020 } };
        var j = new JournalEspion();
        await Payer(f, j).ExecuterAsync(100, null, "virement", 2, "VIR-9", 0, 1, "Caisse 1");
        Assert.Single(f.Paiements);
        // Intérêts 3 000 + TVA 570 + assurance 450 : une seule dépense « charges financières » ; le capital diminue la dette
        Assert.Equal((100000m, 4020m, 450m), ((decimal)j.Details[0]["capital"]!, (decimal)j.Details[0]["charges_financieres"]!, (decimal)j.Details[0]["assurance"]!));
    }

    [Fact]
    public async Task Echeancier_avec_assurance_enregistre_et_cout_au_journal()
    {
        var f = new FinancementsEnMemoire();
        var j = new JournalEspion();
        var cas = new EnregistrerEcheancier(f, Titulaire, j);
        var lignes = f.Echeances.Select(e => e.Ligne with { Assurance = 500, Montant = e.Ligne.Montant + 500 }).ToList();
        // Assurance non reportée dans l'échéance : refus
        var faux = lignes.ToList();
        faux[0] = faux[0] with { Montant = faux[0].Montant - 500 };
        Assert.Contains(await cas.ControlerAsync(5, faux, 1), a => a is { Ligne: 1, Champ: "montant" });

        await cas.ExecuterAsync(5, lignes, 1);
        Assert.Equal([500m, 500m, 500m], f.Remplace!.Value.Lignes.Select(l => l.Assurance));
        Assert.Equal((1500m, 8070m), ((decimal)j.Details[0]["assurance"]!, (decimal)j.Details[0]["cout_total"]!));
    }

    [Fact]
    public async Task Taux_et_duree_du_pret_saisis_tels_qu_au_contrat_et_journalises()
    {
        var f = new FinancementsEnMemoire();
        var j = new JournalEspion();
        var cas = new EnregistrerPret(f, Titulaire, Horloge, j, new TresorerieEnMemoire());
        Assert.Equal("taux", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.CreerAsync(2, null, "PR-3", null, 1000, null, 1, taux: 100.5m))).Champ);
        Assert.Equal("taux", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.CreerAsync(2, null, "PR-3", null, 1000, null, 1, taux: 5.12345m))).Champ);
        Assert.Equal("duree", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.CreerAsync(2, null, "PR-3", null, 1000, null, 1, dureeMois: 0))).Champ);
        Assert.Empty(f.PretsEcrits);

        await cas.CreerAsync(2, null, "PR-3", null, 1000, null, 1, taux: 5.75m, dureeMois: 60);
        Assert.Equal(((decimal?)5.75m, (int?)60), (f.PretsEcrits[0].Taux, f.PretsEcrits[0].DureeMois));
        Assert.Equal(((decimal?)5.75m, (int?)60), ((decimal?)j.Details[0]["taux"], (int?)j.Details[0]["duree_mois"]));

        // Facultatifs ; corrigés même quand une échéance est payée
        f.Pret = f.Pret with { EcheancesPayees = 1 };
        await cas.ModifierAsync(5, 2, null, "PR-1", null, 300000, f.Pret.DatePret, 1, taux: 6m, dureeMois: 36);
        Assert.Equal(((decimal?)6m, (int?)36), (f.PretsEcrits[^1].Taux, f.PretsEcrits[^1].DureeMois));
        await cas.CreerAsync(2, null, "PR-4", null, 1000, null, 1);
        Assert.Equal(((decimal?)null, (int?)null), (f.PretsEcrits[^1].Taux, f.PretsEcrits[^1].DureeMois));
    }

    [Fact]
    public async Task Echeance_payee_en_especes_avec_complement_du_tiroir()
    {
        var f = new FinancementsEnMemoire();
        await Payer(f, new JournalEspion()).ExecuterAsync(100, null, "especes", 2, "ignorée", 3000, 1, "Caisse 1");
        var p = Assert.Single(f.Paiements);
        Assert.Equal((3000m, (long?)1, (long?)null), (p.PartTiroir, p.SessionCaisseId, p.CompteId));   // espèces : coffre, pas de banque

        // Tiroir insuffisant (fond 5 000 + ventes 5 000), ou caisse fermée : refus
        var autre = new FinancementsEnMemoire();
        await Assert.ThrowsAsync<RegleMetierException>(() => Payer(autre, new JournalEspion()).ExecuterAsync(100, null, "especes", null, null, 10000.01m, 1, "Caisse 1"));
        await Assert.ThrowsAsync<RegleMetierException>(() => Payer(autre, new JournalEspion(), sessions: new SessionsEnMemoire { Ouverte = null })
            .ExecuterAsync(100, null, "especes", null, null, 500, 1, "Caisse 1"));
        Assert.Empty(autre.Paiements);
    }

    [Fact]
    public async Task Echeance_deja_payee_ou_date_future_refusee()
    {
        var f = new FinancementsEnMemoire();
        f.Echeances[0] = f.Echeances[0] with { PayeeLe = new DateOnly(2026, 2, 10), Moyen = "especes", MontantPaye = f.Echeances[0].Ligne.Montant };
        await Assert.ThrowsAsync<RegleMetierException>(() => Payer(f, new JournalEspion()).ExecuterAsync(100, null, "especes", null, null, 0, 1, "Caisse 1"));
        Assert.Equal("date", (await Assert.ThrowsAsync<RegleMetierException>(() =>
            Payer(f, new JournalEspion()).ExecuterAsync(101, Horloge.Aujourdhui.AddDays(1), "especes", null, null, 0, 1, "Caisse 1"))).Champ);
    }

    [Fact]
    public async Task Echeancier_incoherent_refuse_echeances_payees_gardees_en_tete()
    {
        var f = new FinancementsEnMemoire();
        var cas = new EnregistrerEcheancier(f, Titulaire, new JournalEspion());
        var lignes = f.Echeances.Select(e => e.Ligne).ToList();
        var faux = lignes.ToList();
        faux[1] = faux[1] with { Montant = 102000.5m };
        Assert.Equal("echeancier", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(5, faux, 1))).Champ);
        Assert.Contains(await cas.ControlerAsync(5, faux, 1), a => a.Ligne == 2);
        Assert.Null(f.Remplace);

        // Première échéance payée : elle doit revenir identique, seules les suivantes sont remplacées
        f.Echeances[0] = f.Echeances[0] with { PayeeLe = new DateOnly(2026, 2, 10), Moyen = "especes", MontantPaye = f.Echeances[0].Ligne.Montant };
        var modifiee = lignes.ToList();
        modifiee[0] = modifiee[0] with { Interets = 2999, Montant = 103569 };
        Assert.Contains(await cas.ControlerAsync(5, modifiee, 1), a => a.Ligne == 1 && a.Message.Contains("déjà payée"));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(5, modifiee, 1));

        var nouvelles = lignes.ToList();
        nouvelles[2] = nouvelles[2] with { Date = new DateOnly(2026, 4, 15) };
        await cas.ExecuterAsync(5, nouvelles, 1);
        Assert.Equal(1, f.Remplace!.Value.Payees);
        Assert.Equal([new DateOnly(2026, 3, 10), new DateOnly(2026, 4, 15)], f.Remplace.Value.Lignes.Select(l => l.Date));
    }

    [Fact]
    public async Task Apercu_d_un_tableau_importe_avec_ses_incoherences()
    {
        var f = new FinancementsEnMemoire();
        IReadOnlyList<IReadOnlyList<string?>> tableau =
        [
            ["Date", "Capital", "Intérêts", "Échéance"],
            ["10/02/2026", "100 000,00", "3 000,00", "103 000,00"],
            ["10/03/2026", "100 000,00", "2 000,00", "102 000,00"],
            ["10/04/2026", "99 000,00", "1 000,00", "101 000,00"]
        ];
        var a = await new EnregistrerEcheancier(f, Titulaire, new JournalEspion())
            .ApercuAsync(5, tableau, new CorrespondanceColonnes(0, 1, 2, null, null, 3, null, 1), 1);
        Assert.Equal(3, a.Lignes.Count);
        Assert.Contains(a.Anomalies, x => x is { Ligne: 3, Champ: "montant" });    // 99 000 + 1 000 ≠ 101 000
        Assert.Contains(a.Anomalies, x => x is { Ligne: 0, Champ: "capital" });    // 299 000 ≠ 300 000
    }

    [Fact]
    public async Task Texte_lu_dans_un_pdf_decoupe_en_cellules()
    {
        var t = await new EnregistrerEcheancier(new FinancementsEnMemoire(), Titulaire, new JournalEspion())
            .LireTexteAsync(["", "1 10/02/2026 100 000,00 3 000,00 103 000,00"], 1);
        Assert.Equal(["1", "10/02/2026", "100 000,00", "3 000,00", "103 000,00"], Assert.Single(t));
    }

    [Fact]
    public async Task Apport_du_titulaire_verse_ou_depense_payee()
    {
        var f = new FinancementsEnMemoire();
        var j = new JournalEspion();
        var cas = new UtiliserFinancement(f, Titulaire, Horloge, j, new TresorerieEnMemoire());
        Assert.Equal("source", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(null, "tresorerie", null, 100, 1, null, null, null, 1))).Champ);
        Assert.Equal("categorie", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("titulaire", "depense", null, 100, 1, null, "Loyer", null, 1))).Champ);
        // Les charges financières viennent du paiement des échéances, pas d'une dépense saisie
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("titulaire", "depense", null, 100, 1, "charges_financieres", "Intérêts", null, 1));

        await cas.ExecuterAsync("titulaire", "tresorerie", null, 50000, 1, "loyer", null, null, 1);
        await cas.ExecuterAsync("pret:5", "depense", null, 12000, 2, "loyer", " Loyer novembre ", "F-11", 1);
        Assert.Equal(2, f.Utilisations.Count);
        Assert.Equal((true, (string?)null, "Apport du titulaire"), (f.Utilisations[0].Source.EstTitulaire, f.Utilisations[0].Categorie, f.Utilisations[0].Libelle));
        Assert.Equal(((long?)5, "loyer", "Loyer novembre", "F-11"), (f.Utilisations[1].Source.PretId, f.Utilisations[1].Categorie, f.Utilisations[1].Libelle, f.Utilisations[1].Reference));
        Assert.Equal(["financement_utilise", "financement_utilise"], j.Actions);
    }

    [Fact]
    public async Task Remboursement_du_titulaire_au_plus_ce_qui_lui_est_du_et_au_plus_le_solde_du_compte()
    {
        var f = new FinancementsEnMemoire { SoldeTitulaire = 20000 };
        var cas = new RembourserTitulaire(f, Titulaire, Horloge, new JournalEspion(), new TresorerieEnMemoire());
        Assert.Equal("montant", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(null, 20000.01m, 1, null, null, 1))).Champ);
        f.SoldeTitulaire = 80000;
        // Coffre : 10 000 DA
        Assert.Contains("Solde insuffisant", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(null, 15000, 1, null, null, 1))).Message);
        await cas.ExecuterAsync(null, 15000, 2, null, null, 1);
        Assert.Equal((15000m, 2L, "Remboursement du titulaire"), (f.Remboursements[0].Montant, f.Remboursements[0].CompteId, f.Remboursements[0].Libelle));
    }

    [Fact]
    public async Task Pret_d_un_preteur_hors_tresorerie_nom_libre_exactement_un_des_deux()
    {
        var f = new FinancementsEnMemoire();
        var cas = new EnregistrerPret(f, Titulaire, Horloge, new JournalEspion(), new TresorerieEnMemoire());
        // Ni banque ni nom, les deux à la fois, nom trop long : refus sur le champ du prêteur
        Assert.Equal("preteur", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.CreerAsync(null, "  ", "PR-9", null, 1000, null, 1))).Champ);
        Assert.Equal("preteur", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.CreerAsync(2, "Banque du Sud", "PR-9", null, 1000, null, 1))).Champ);
        Assert.Equal("preteur", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.CreerAsync(null, new string('a', 121), "PR-9", null, 1000, null, 1))).Champ);
        Assert.Equal("compte", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.CreerAsync(0, null, "PR-9", null, 1000, null, 1))).Champ);
        Assert.Empty(f.PretsEcrits);

        // Nom libre nettoyé, aucun compte de la trésorerie
        await cas.CreerAsync(null, "  Banque   du Sud ", "PR-9", null, 1000, null, 1);
        Assert.Equal(((long?)null, "Banque du Sud"), (f.PretsEcrits[0].CompteId, f.PretsEcrits[0].Preteur));

        // Une part employée : le prêteur ne passe pas vers une banque de la trésorerie
        f.Pret = f.Pret with { CompteId = null, Banque = "Banque du Sud", Utilise = 1000 };
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ModifierAsync(5, 2, null, "PR-1", null, 300000, f.Pret.DatePret, 1));
        await cas.ModifierAsync(5, null, "Banque du Sud", "PR-1 bis", null, 300000, f.Pret.DatePret, 1);
        Assert.Equal(((long?)null, "Banque du Sud", "PR-1 bis"), (f.PretsEcrits[^1].CompteId, f.PretsEcrits[^1].Preteur, f.PretsEcrits[^1].Reference));
    }

    [Fact]
    public async Task Nom_d_un_preteur_hors_tresorerie_corrige_meme_pret_utilise_ancien_et_nouveau_nom_au_journal()
    {
        var f = new FinancementsEnMemoire();
        var journal = new JournalEspion();
        var cas = new EnregistrerPret(f, Titulaire, Horloge, journal, new TresorerieEnMemoire());
        // Échéance payée et part employée : la faute de frappe se corrige quand même
        f.Pret = f.Pret with { CompteId = null, Banque = "Banqe du Sud", Utilise = 1000, EcheancesPayees = 1 };
        await cas.ModifierAsync(5, null, " Banque  du Sud ", "PR-1", null, 300000, f.Pret.DatePret, 1);
        Assert.Equal(((long?)null, "Banque du Sud"), (f.PretsEcrits[^1].CompteId, f.PretsEcrits[^1].Preteur));
        Assert.Equal(["pret_modifie"], journal.Actions);
        Assert.Equal(("Banqe du Sud", "Banque du Sud"), ((string?)journal.Details[0]["preteur_avant"], (string?)journal.Details[0]["preteur"]));

        // Même nom : pas d'ancien nom au journal ; montant et date toujours figés
        await cas.ModifierAsync(5, null, "Banqe du Sud", "PR-1", "Objet", 300000, f.Pret.DatePret, 1);
        Assert.False(journal.Details[1].ContainsKey("preteur_avant"));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ModifierAsync(5, null, "Banque du Sud", "PR-1", null, 250000, f.Pret.DatePret, 1));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ModifierAsync(5, null, "Banque du Sud", "PR-1", null, 300000, f.Pret.DatePret.AddDays(1), 1));
    }

    [Fact]
    public async Task Pret_utilise_d_une_banque_suivie_ni_autre_banque_ni_preteur_hors_tresorerie()
    {
        var f = new FinancementsEnMemoire();
        var tresorerie = new TresorerieEnMemoire();
        tresorerie.Comptes.Add(new(3, "banque", "Autre banque", "Banque", null, true, true, 0, new DateOnly(2026, 11, 1), 0));
        var cas = new EnregistrerPret(f, Titulaire, Horloge, new JournalEspion(), tresorerie);
        f.Pret = f.Pret with { EcheancesPayees = 1 };
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ModifierAsync(5, 3, null, "PR-1", null, 300000, f.Pret.DatePret, 1));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ModifierAsync(5, null, "Banque du Sud", "PR-1", null, 300000, f.Pret.DatePret, 1));
        Assert.Empty(f.PretsEcrits);

        // Prêt pas encore utilisé : la banque change librement
        f.Pret = f.Pret with { EcheancesPayees = 0 };
        await cas.ModifierAsync(5, 3, null, "PR-1", null, 300000, f.Pret.DatePret, 1);
        Assert.Equal((long?)3, f.PretsEcrits[^1].CompteId);
    }

    [Fact]
    public async Task Argent_d_un_preteur_hors_tresorerie_jamais_verse_sur_un_compte_mais_paie_une_depense()
    {
        var f = new FinancementsEnMemoire();
        f.Pret = f.Pret with { CompteId = null, Banque = "Banque du Sud" };
        var cas = new UtiliserFinancement(f, Titulaire, Horloge, new JournalEspion(), new TresorerieEnMemoire());
        Assert.Equal("usage", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("pret:5", "tresorerie", null, 1000, 1, null, null, null, 1))).Champ);
        Assert.Empty(f.Utilisations);
        // Dépense payée directement : l'entrée et la dépense sur le compte choisi, solde inchangé, source tracée
        await cas.ExecuterAsync("pret:5", "depense", null, 1000, 1, "loyer", "Loyer", null, 1);
        Assert.Equal(("depense", (long?)5), (f.Utilisations[0].Usage, f.Utilisations[0].Source.PretId));
    }

    [Fact]
    public async Task Colonnes_d_import_retenues_par_preteur_hors_tresorerie()
    {
        var f = new FinancementsEnMemoire();
        f.Pret = f.Pret with { CompteId = null, Banque = "Banque du Sud" };
        await new EnregistrerEcheancier(f, Titulaire, new JournalEspion()).RetenirCorrespondanceAsync(5, new CorrespondanceColonnes(0, 1, 2, null, null, 3, null, 1), 1);
        Assert.Equal(((long?)null, "Banque du Sud"), f.CorrespondanceRetenue);
        Assert.Equal("banque du sud", PreteurPret.Cle("  Banque   DU Sud "));
    }

    [Fact]
    public async Task Pret_accorde_par_une_banque_de_la_tresorerie()
    {
        var f = new FinancementsEnMemoire();
        var cas = new EnregistrerPret(f, Titulaire, Horloge, new JournalEspion(), new TresorerieEnMemoire());
        Assert.Equal("compte", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.CreerAsync(1, null, "PR-2", null, 1000, null, 1))).Champ);   // le coffre n'accorde pas de prêt
        Assert.Equal("reference", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.CreerAsync(2, null, " ", null, 1000, null, 1))).Champ);
        Assert.Equal("date", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.CreerAsync(2, null, "PR-2", null, 1000, Horloge.Aujourdhui.AddDays(1), 1))).Champ);
        Assert.Equal(6L, await cas.CreerAsync(2, null, "PR-2", null, 1000, null, 1));

        // Une part employée : le montant ne change plus
        f.Pret = f.Pret with { Utilise = 1000 };
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ModifierAsync(5, 2, null, "PR-1", null, 250000, f.Pret.DatePret, 1));
        await cas.ModifierAsync(5, 2, null, "PR-1 bis", "Objet", 300000, f.Pret.DatePret, 1);
    }

    [Fact]
    public async Task Facture_de_grossiste_payee_par_un_pret_hors_tresorerie_par_le_titulaire_seul()
    {
        var situation = new SituationGrossiste(4, "Grossiste fictif", 1200,
            [new FactureFournisseur(10, "F-1", "BL-1", new DateTime(2026, 10, 1), 1200, 0, 1200)], []);
        var stock = new ReglementsEnMemoire(situation);
        EnregistrerReglement Cas(DroitsFactices d) => new(stock, new SessionsEnMemoire(), d, Horloge, new JournalEspion(), new TresorerieEnMemoire());

        await Assert.ThrowsAsync<DroitRefuseException>(() => Cas(Employe).ExecuterAsync(4, null, 500, "virement", null, null, null, 0, null, null, 1, "Caisse 1",
            source: "pret:5"));
        Assert.Null(stock.Ecrit);

        // Moyen, banque et tiroir ignorés : aucun compte de trésorerie débité, la source est tracée
        await Cas(Titulaire).ExecuterAsync(4, null, 500, "especes", null, null, null, 300, null, null, 1, "Caisse 1", compteId: 2, source: "pret:5");
        Assert.Equal((MoyenReglement.Financement, (long?)null, 0m, (long?)5), (stock.Ecrit!.Moyen, stock.Ecrit.CompteId, stock.Ecrit.PartTiroir, stock.Ecrit.Source!.PretId));
    }

    [Fact]
    public async Task Traite_payee_par_un_apport_hors_tresorerie_sauf_traite_remise_avec_une_banque()
    {
        var jour = Horloge.Aujourdhui;
        var sansBanque = new TraiteAPayer(20, "RG-000020", 4, "Grossiste fictif", "TR-20", null, jour.AddDays(-60), jour.AddDays(-1), 3000, null, null);
        var avecBanque = new TraiteAPayer(21, "RG-000021", 4, "Grossiste fictif", "TR-21", null, jour.AddDays(-60), jour.AddDays(3), 2000, 2, "Banque fictive");
        var traites = new TraitesEnMemoire(sansBanque, avecBanque);
        PayerTraite Cas() => new(traites, new SessionsEnMemoire(), Titulaire, Horloge, new JournalEspion(), new TresorerieEnMemoire());

        Assert.Equal("source", (await Assert.ThrowsAsync<RegleMetierException>(() => Cas().ExecuterAsync(21, null, null, null, null, 0, 1, "Caisse 1", "titulaire"))).Champ);
        await Cas().ExecuterAsync(20, null, "especes", 2, null, 500, 1, "Caisse 1", "titulaire");
        var p = Assert.Single(traites.Payees);
        Assert.Equal((MoyenReglement.Financement, (long?)null, 0m, true), (p.Moyen, p.CompteId, p.PartTiroir, p.Source!.EstTitulaire));
    }

    [Fact]
    public void Suivi_des_dettes_pret_rappele_7_jours_avant_titulaire_jamais_en_retard()
    {
        var jour = Horloge.Aujourdhui;
        SommeDue Somme(string categorie, decimal montant, int? echeanceDans, int depuis = 400) =>
            new("dette", categorie, null, "Banque fictive", "Prêt", jour.AddDays(-depuis), echeanceDans is { } e ? jour.AddDays(e) : null, montant, false);
        var s = RegleSuiviDettes.Construire(
        [
            Somme("pret", 200000, 6),           // échéance dans 6 jours : rappel
            Somme("pret", 100000, 30),          // plus tard : pas d'alerte
            Somme("pret", 50000, -2),           // échue, non payée
            Somme("titulaire", 80000, null),    // apport ancien : jamais en retard
            Somme("grossiste", 1000, null, 5)
        ], 30, jour);
        Assert.Equal([true, false, true], s.Lignes.Where(l => l.Somme.Categorie == "pret").OrderByDescending(l => l.Somme.Montant).Select(l => l.EnAlerte));
        Assert.False(s.Lignes.Single(l => l.Somme.Categorie == "titulaire").EnAlerte);
        var alerte = s.Alertes.Single(a => a.Categorie == "pret");
        Assert.Equal((2, 250000m), (alerte.Nombre, alerte.Montant));
        Assert.Equal(1000m, s.TotalDettes);                // les financements ne sont pas des factures de grossistes
        Assert.Equal(430000m, s.TotalFinancements);
        Assert.DoesNotContain(s.Tranches, t => t.Montant > 1000);
    }

    [Fact]
    public async Task Dettes_des_financements_montrees_au_titulaire_seul()
    {
        var suivi = new SuiviEnMemoire(
        [
            new SommeDue("dette", "grossiste", 4, "Grossiste fictif", "Facture F-1", Horloge.Aujourdhui.AddDays(-5), null, 1000, false),
            new SommeDue("dette", "pret", null, "Banque fictive", "Prêt PR-1", Horloge.Aujourdhui.AddDays(-50), Horloge.Aujourdhui.AddDays(3), 200000, false),
            new SommeDue("dette", "titulaire", null, "Titulaire", "Apports", Horloge.Aujourdhui.AddDays(-50), null, 80000, false)
        ]);
        var employe = await new ObtenirSuiviDettes(suivi, Employe, Horloge).ExecuterAsync(1);
        Assert.Equal(["grossiste"], employe.Lignes.Select(l => l.Somme.Categorie));
        var titulaire = await new ObtenirSuiviDettes(suivi, Titulaire, Horloge).LignesAsync(1, FiltreSuiviDettes.Lire("pret", null, null, null, null), new DemandePage());
        Assert.Equal(200000m, titulaire.Total);
        var vide = await new ObtenirSuiviDettes(suivi, Employe, Horloge).LignesAsync(1, FiltreSuiviDettes.Lire("pret", null, null, null, null), new DemandePage());
        Assert.Equal(0, vide.Page.Total);
    }
}

internal sealed class SuiviEnMemoire(IReadOnlyList<SommeDue> sommes) : ISuiviDettes
{
    public Task<IReadOnlyList<SommeDue>> SommesDuesAsync(CancellationToken ct = default) => Task.FromResult(sommes);
    public Task<int> DelaiAlerteAsync(CancellationToken ct = default) => Task.FromResult(30);
    public Task ModifierDelaiAlerteAsync(int jours, CancellationToken ct = default) => Task.CompletedTask;
}
