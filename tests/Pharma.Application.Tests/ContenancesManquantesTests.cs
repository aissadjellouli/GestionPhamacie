using Pharma.Application.Catalogue;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Tests;

/// <summary>
/// Contenances des produits (décisions du titulaire du 2026-11-26) : compléter et corriger à l'écran (droit de la fiche produit, bornes,
/// jamais d'écrasement silencieux, journal avant / après), import d'un fichier (en-têtes, rapprochement, case « Corriger aussi »).
/// </summary>
public class ContenancesManquantesTests
{
    private sealed class Depot : IContenancesManquantes
    {
        public Dictionary<long, (string Nom, int? Contenance)> Produits { get; } = new() { [1] = ("DOLIPRANE", null), [2] = ("SMECTA", 30) };
        public string? Recherche { get; private set; }
        public bool Toutes { get; private set; }

        public Task<Page<ContenanceManquante>> ListerAsync(string? recherche, DemandePage page, CancellationToken ct = default, bool toutes = false)
        {
            (Recherche, Toutes) = (recherche, toutes);
            var lignes = Produits.Where(p => toutes || p.Value.Contenance is null)
                .Select(p => new ContenanceManquante(p.Key, p.Value.Nom, null, null, "B/30 ET B/60", 5, 2, p.Value.Contenance)).ToList();
            return Task.FromResult(new Page<ContenanceManquante>(lignes, lignes.Count, page.Page, page.Taille));
        }

        public Task<string?> EnregistrerSiVideAsync(long produitId, int contenance, CancellationToken ct = default) => CorrigerAsync(produitId, null, contenance, ct);

        public Task<string?> CorrigerAsync(long produitId, int? attendue, int contenance, CancellationToken ct = default)
        {
            if (!Produits.TryGetValue(produitId, out var p) || p.Contenance != attendue) return Task.FromResult<string?>(null);
            Produits[produitId] = (p.Nom, contenance);
            return Task.FromResult<string?>(p.Nom);
        }
    }

    [Fact]
    public async Task Completer_puis_corriger_avec_le_droit_de_la_fiche_produit_journal_avant_apres()
    {
        var (depot, journal) = (new Depot(), new JournalEspion());
        var gerer = new GererContenancesManquantes(depot, new DroitsFactices(Permissions.CatalogueCreer), journal);

        Assert.Single((await gerer.ListerAsync(1, "  doli  ", new DemandePage(1, 10))).Lignes);
        Assert.Equal(("doli", false), (depot.Recherche, depot.Toutes));
        Assert.Equal(2, (await gerer.ListerAsync(1, null, new DemandePage(1, 10), default, toutes: true)).Lignes.Count);

        await gerer.EnregistrerAsync(1, 1, 16);
        await gerer.EnregistrerAsync(1, 2, 20, default, avant: 30);      // correction
        await gerer.EnregistrerAsync(1, 2, 20, default, avant: 20);      // identique : rien
        Assert.Equal((16, 20), (depot.Produits[1].Contenance, depot.Produits[2].Contenance));
        Assert.Equal(2, journal.Actions.Count);
        Assert.Equal(GererContenancesManquantes.Motif, journal.Details[1]["motif"]);
        var c = (Dictionary<string, object?>)journal.Details[1]["contenance"]!;
        Assert.Equal((30, 20), ((int?)c["avant"], (int?)c["apres"]));
    }

    [Fact]
    public async Task Sans_le_droit_hors_bornes_ou_change_entre_temps_refuse_et_rien_n_est_ecrit()
    {
        var (depot, journal) = (new Depot(), new JournalEspion());
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new GererContenancesManquantes(depot, new DroitsFactices(), journal).EnregistrerAsync(1, 1, 16));

        var gerer = new GererContenancesManquantes(depot, new DroitsFactices(Permissions.CatalogueCreer), journal);
        foreach (var faux in new int?[] { null, 0, 10001 })
            Assert.Equal("contenance", (await Assert.ThrowsAsync<RegleMetierException>(() => gerer.EnregistrerAsync(1, 1, faux))).Champ);
        // Lu vide, mais renseigné entre-temps ; lu 25, mais vaut 30 : jamais écrasé
        await Assert.ThrowsAsync<RegleMetierException>(() => gerer.EnregistrerAsync(1, 2, 20));
        await Assert.ThrowsAsync<RegleMetierException>(() => gerer.EnregistrerAsync(1, 2, 20, default, avant: 25));
        Assert.Equal(30, depot.Produits[2].Contenance);
        Assert.Null(depot.Produits[1].Contenance);
        Assert.Empty(journal.Actions);
    }

    private static readonly IReadOnlyList<ProduitIdentifie> Catalogue =
    [
        new(1, "DOLIPRANE", "12/34A", "6130000000011", null),
        new(2, "SMECTA", "56/78B", "6130000000028", 30),
        new(3, "AUGMENTIN", "99/11C", null, null),
        new(4, "DOUBLON A", "77/77", null, null),
        new(5, "DOUBLON B", "77/77", null, null)
    ];

    [Fact]
    public void Fichier_rapproche_par_ses_en_tetes_lignes_retenues_et_ecartees_avec_leur_motif()
    {
        IReadOnlyList<IReadOnlyList<string?>> fichier =
        [
            ["Liste du grossiste"],
            ["Désignation", "N° d’enregistrement", "Code-barres", "Contenance (unités)"],
            ["Doliprane", "12/34a", null, "16"],
            ["Smecta", "56/78B", null, "30"],
            ["Smecta", null, "6130000000028", "60"],
            ["Augmentin", "99/11C", null, "12,0"],
            ["Augmentin", "99/11C", null, "24"],
            ["Inconnu", "00/00", null, "10"],
            ["Doublon", "77/77", null, "10"],
            ["Sans valeur", "12/34A", null, ""],
            ["Faux", "12/34A", null, "abc"]
        ];
        var a = LectureImportContenances.Rapprocher(fichier, Catalogue);
        Assert.Equal([(1L, 16, (int?)null)], a.Retenues.Select(r => (r.ProduitId, r.Contenance, r.Avant)));
        Assert.Equal(1, a.LignesVides);
        Assert.Contains(a.Ecartees, e => e.Ligne == 4 && e.Motif.Contains("identique"));
        Assert.Contains(a.Ecartees, e => e.Ligne == 5 && e.Motif.Contains("Corriger aussi"));
        Assert.Equal(2, a.Ecartees.Count(e => e.Motif.Contains("plusieurs contenances")));
        Assert.Contains(a.Ecartees, e => e.Ligne == 8 && e.Motif.Contains("introuvable"));
        Assert.Contains(a.Ecartees, e => e.Ligne == 9 && e.Motif.Contains("2 produits"));
        Assert.Contains(a.Ecartees, e => e.Ligne == 11 && e.Motif.Contains("entier"));

        // Case « Corriger aussi » : la contenance renseignée (30) est remplacée par 60, avec l'ancienne valeur
        var corrige = LectureImportContenances.Rapprocher(fichier, Catalogue, corriger: true);
        Assert.Contains(corrige.Retenues, r => r.ProduitId == 2 && r.Contenance == 60 && r.Avant == 30);
    }

    [Fact]
    public void Sans_colonne_contenance_ou_sans_colonne_produit_le_fichier_est_refuse_et_l_export_se_relit()
    {
        Assert.Equal("fichier", Assert.Throws<RegleMetierException>(() => LectureImportContenances.Rapprocher([["Produit", "Prix"]], Catalogue)).Champ);
        Assert.Equal("fichier", Assert.Throws<RegleMetierException>(() => LectureImportContenances.Rapprocher([["Produit", "Contenance"]], Catalogue)).Champ);

        // Fichier exporté par l'écran, contenance remplie : relu par son n° interne
        var export = LectureImportContenances.Exporter([(new ContenanceManquante(3, "AUGMENTIN; 1 g", null, null, "B/1 ET B/3", 9, 1), "99/11C", null)]);
        var texte = System.Text.Encoding.UTF8.GetString(export).TrimStart('﻿');
        var lignes = texte.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(l => (IReadOnlyList<string?>)LireCsv(l)).ToList();
        Assert.Equal("Contenance", lignes[0][^1]);
        lignes[1] = [.. lignes[1].Take(lignes[1].Count - 1), "3"];
        Assert.Equal(3, Assert.Single(LectureImportContenances.Rapprocher(lignes, Catalogue).Retenues).Contenance);
    }

    /// <summary>Découpe d'une ligne CSV « ; » avec guillemets (comme le lecteur du serveur).</summary>
    private static List<string?> LireCsv(string ligne)
    {
        var champs = new List<string?>();
        var courant = new System.Text.StringBuilder();
        var guillemets = false;
        for (var i = 0; i < ligne.Length; i++)
        {
            var c = ligne[i];
            if (c == '"') { if (guillemets && i + 1 < ligne.Length && ligne[i + 1] == '"') { courant.Append('"'); i++; } else guillemets = !guillemets; }
            else if (c == ';' && !guillemets) { champs.Add(courant.ToString()); courant.Clear(); }
            else courant.Append(c);
        }
        champs.Add(courant.ToString());
        return champs;
    }
}
