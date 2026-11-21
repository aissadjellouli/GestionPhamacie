using System.Globalization;
using System.Text.Json;
using Pharma.Application.Commun;
using Pharma.Application.Synchronisation;
using Pharma.Desktop.Services;
using Pharma.Domain.Commun;
using Pharma.Shared;

namespace Pharma.Application.Tests;

/// <summary>
/// Reste du lot S7.3 : message du titulaire à distance affiché sur les postes. Choix du message (le plus récent encore valable, jamais
/// un échu), fermeture par le titulaire seul, pastille « Message » pour tous les employés connectés (fenêtre, pas de changement
/// d'écran), regroupement « À traiter » respecté (une ligne à 1366 px).
/// </summary>
public class MessageCentralTests
{
    private static readonly DateTime Maintenant = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);

    private sealed class Horloge(DateTime utc) : IHorloge
    {
        public DateTime MaintenantUtc => utc;
        public DateOnly Aujourdhui => DateOnly.FromDateTime(utc);
    }

    private sealed class Depot : IMessageCentralDepot
    {
        public readonly List<MessageCentralLu> Lus = [];
        public readonly List<(Guid Id, long Par)> Fermes = [];
        public bool EnPanne { get; set; }
        public Task<IReadOnlyList<MessageCentralLu>> AffichablesAsync(CancellationToken ct = default) =>
            EnPanne ? Task.FromException<IReadOnlyList<MessageCentralLu>>(new IOException("base injoignable"))
                    : Task.FromResult<IReadOnlyList<MessageCentralLu>>(Lus.Where(l => !Fermes.Any(f => f.Id == l.Id)).ToList());
        public Task<bool> FermerAsync(Guid id, long par, CancellationToken ct = default)
        {
            if (!Lus.Any(l => l.Id == id) || Fermes.Any(f => f.Id == id)) return Task.FromResult(false);
            Fermes.Add((id, par));
            return Task.FromResult(true);
        }
    }

    private static string Donnees(string texte, DateTime fin) => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["format"] = "message-v1", ["texte"] = texte, ["jusqu_au"] = fin.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
    });

    private static MessageCentralLu Lu(string texte, DateTime cree, DateTime fin) => new(Guid.NewGuid(), Donnees(texte, fin), cree, cree.AddMinutes(1));

    [Fact]
    public async Task Le_plus_recent_remplace_les_precedents_un_message_echu_jamais()
    {
        var depot = new Depot();
        var ancien = Lu("Ancien message", Maintenant.AddDays(-2), Maintenant.AddDays(3));
        var recent = Lu("Inventaire lundi", Maintenant.AddHours(-3), Maintenant.AddDays(1));
        var echu = Lu("Message échu", Maintenant.AddHours(-1), Maintenant.AddMinutes(-1));
        depot.Lus.AddRange([ancien, recent]);
        var lire = new LireMessageCentral(depot, new Horloge(Maintenant));

        var m = await lire.ExecuterAsync();
        Assert.Equal(recent.Id, m!.Id);
        Assert.Equal("Inventaire lundi", m.Texte);
        Assert.Equal(Maintenant.AddDays(1), m.JusquAuUtc);

        // Un nouveau message remplace le précédent : le plus récent échu, l'ancien encore valable ne revient PAS
        Assert.Null(RegleMessageCentral.Choisir([ancien, recent, echu], Maintenant));
        Assert.Null(RegleMessageCentral.Choisir([ancien, recent], Maintenant.AddDays(2)));
        Assert.Null(RegleMessageCentral.Choisir([ancien, recent], Maintenant.AddDays(4)));
        // Données hors bornes (jamais affichées, même lues en base)
        Assert.Null(RegleMessageCentral.Choisir([Lu("<b>x</b>", Maintenant.AddHours(-1), Maintenant.AddDays(1))], Maintenant));
        Assert.Null(RegleMessageCentral.Choisir([Lu("Trop loin", Maintenant.AddHours(-1), Maintenant.AddDays(8))], Maintenant));
    }

    [Fact]
    public async Task Lecture_impossible_n_affiche_rien_sans_erreur()
    {
        var depot = new Depot { EnPanne = true };
        Assert.Null(await new LireMessageCentral(depot, new Horloge(Maintenant)).ExecuterAsync());
    }

    [Fact]
    public async Task Fermeture_par_le_titulaire_seulement_puis_plus_affiche()
    {
        var depot = new Depot();
        var m = Lu("Inventaire lundi", Maintenant.AddHours(-1), Maintenant.AddDays(1));
        depot.Lus.Add(m);
        await Assert.ThrowsAsync<DroitRefuseException>(() => new FermerMessageCentral(depot, new DroitsFactices("caisse.vendre", "admin.parametres")).ExecuterAsync(m.Id, 5));
        Assert.Empty(depot.Fermes);

        var titulaire = new FermerMessageCentral(depot, new DroitsFactices() { Titulaire = true });
        await titulaire.ExecuterAsync(m.Id, 1);
        Assert.Equal((m.Id, 1L), depot.Fermes.Single());
        Assert.Null(await new LireMessageCentral(depot, new Horloge(Maintenant)).ExecuterAsync());
        await Assert.ThrowsAsync<RegleMetierException>(() => titulaire.ExecuterAsync(m.Id, 1));   // déjà fermé
    }

    // ---- Barre d'état ----
    private static readonly DateTime MaintenantLocal = new(2026, 11, 21, 10, 42, 0, DateTimeKind.Local);

    private static MessageCentralDto Dto(DateTime? fin = null, bool peutFermer = false) =>
        new(Guid.NewGuid(), "Inventaire lundi : caisse fermée à 17 h", (fin ?? MaintenantLocal.AddDays(1)).ToUniversalTime(), MaintenantLocal.AddHours(-1).ToUniversalTime(), peutFermer);

    private static AffichageBarre Barre(Func<string, bool> droits, MessageCentralDto? message, CompteursBarreDto? compteurs = null, EtatPosteDto? poste = null,
        bool joignable = true) =>
        PastillesBarre.Calculer(new EtatBarre(joignable, MaintenantLocal, droits, poste ?? new EtatPosteDto(true, MaintenantLocal.AddHours(-10), "reussie", null, null),
            null, compteurs, null, null, message));

    [Fact]
    public void Pastille_message_pour_tous_les_employes_ouvre_sa_fenetre()
    {
        foreach (var droits in new Func<string, bool>[] { _ => false, d => d == "caisse.vendre", d => d is "role.titulaire" or "admin.parametres" })
        {
            var p = Barre(droits, Dto()).Visibles.Single();
            Assert.Equal("message-central", p.Cle);
            Assert.Equal("Message", p.Libelle);
            Assert.Null(p.Niveau);                                          // information : grise
            Assert.Equal(PastillesBarre.LienMessage, p.Lien);               // toujours cliquable : petite fenêtre, pas d'écran
            Assert.Contains("Inventaire lundi", p.Titre);
        }
        // Échu (horloge du poste), serveur injoignable ou aucun message : rien
        Assert.Empty(Barre(_ => true, Dto(MaintenantLocal.AddMinutes(-1))).Visibles);
        Assert.Empty(Barre(_ => true, Dto(), joignable: false).Visibles);
        Assert.Empty(Barre(_ => true, null).Visibles);
    }

    [Fact]
    public void Message_regroupe_dans_a_traiter_avec_une_anomalie_ou_au_dela_de_six_grises()
    {
        string[] tout = ["role.titulaire", "caisse.vendre", "chifa.bordereaux", "achats.regler", "admin.parametres", "admin.sauvegardes"];
        // Avec une anomalie (sauvegarde échouée) : le message passe dans « À traiter », toujours cliquable
        var a = Barre(tout.Contains, Dto(), poste: new EtatPosteDto(true, MaintenantLocal.AddHours(-10), "echouee", null, null));
        Assert.Equal(["Sauvegarde échouée"], a.Visibles.Select(p => p.Libelle));
        Assert.Contains(a.ATraiter, p => p.Cle == "message-central" && p.Lien == PastillesBarre.LienMessage);

        // Six grises (cas mesuré) et le message : 7 grises → toutes dans « À traiter (7) », la barre reste sur une ligne
        var caisse = new EtatPosteDto(true, MaintenantLocal.AddHours(-10), "reussie", 4, MaintenantLocal.Date.AddHours(8));
        var plein = Barre(tout.Contains, Dto(), new CompteursBarreDto(3, 2, 5, 12, 2, false), caisse);
        Assert.Empty(plein.Visibles);
        Assert.Equal(7, plein.ATraiter.Count);
        Assert.True(plein.Regroupe);
        Assert.Null(plein.NiveauGroupe);
        // Six grises sans message : inchangé (toutes visibles)
        var six = Barre(tout.Contains, null, new CompteursBarreDto(3, 2, 5, 12, 2, false), caisse);
        Assert.Equal(6, six.Visibles.Count);
        Assert.False(six.Regroupe);
    }
}
