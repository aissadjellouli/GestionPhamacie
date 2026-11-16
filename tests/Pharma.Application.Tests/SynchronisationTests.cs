using System.Security.Cryptography;
using System.Text;
using Pharma.Application.Synchronisation;
using Pharma.Shared;

namespace Pharma.Application.Tests;

/// <summary>Contrat sync-v1 (forme, sérialisation déterministe, empreinte) et constitution des lots (lot S1, sans envoi).</summary>
public class SynchronisationTests
{
    private static readonly Guid Officine = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTime Le = new(2026, 11, 16, 8, 30, 0, DateTimeKind.Utc);

    private static LotSyncDto Lot(params LigneSyncDto[] lignes) =>
        new(new EnTeteLotSyncDto(ContratSync.VersionV1, Officine, 1, 1, lignes.Length, ""), lignes);

    // ---- Contrat ----

    [Fact]
    public void La_forme_sync_v1_est_figee()
    {
        var lot = Lot(new LigneSyncDto(7, "vente", "42", ContratSync.Creation, Le.AddTicks(1234567), """{ "b": 2.50, "a": "é", "patient_id": "P-0a" }"""),
            new LigneSyncDto(8, "vente", "43", ContratSync.Suppression, Le, null));
        var json = Encoding.UTF8.GetString(SerialisationSyncV1.Serialiser(lot));
        Assert.Equal("""{"version":"sync-v1","officine":"11111111-2222-3333-4444-555555555555","epoque":1,"lot":1,"nombre":2,"empreinte":"","lignes":[""" +
            """{"n":7,"table":"vente","id":"42","op":"creation","le":"2026-11-16T08:30:00.123456Z","donnees":{"a":"é","b":2.50,"patient_id":"P-0a"}},""" +
            """{"n":8,"table":"vente","id":"43","op":"suppression","le":"2026-11-16T08:30:00.000000Z","donnees":null}]}""", json);
    }

    [Fact]
    public void La_serialisation_ne_depend_ni_de_l_ordre_des_cles_ni_des_espaces()
    {
        var a = Lot(new LigneSyncDto(1, "produit", "5", ContratSync.Modification, Le, """{"x":{"z":1,"y":[2,{"b":1,"a":0}]},"prix":120.00}"""));
        var b = Lot(new LigneSyncDto(1, "produit", "5", ContratSync.Modification, Le, """ { "prix" : 120.00 , "x" : { "y" : [2, {"a":0,"b":1}], "z" : 1 } } """));
        Assert.Equal(SerialisationSyncV1.Serialiser(a), SerialisationSyncV1.Serialiser(b));
        Assert.Equal(SerialisationSyncV1.CalculerEmpreinte(a), SerialisationSyncV1.CalculerEmpreinte(b));
    }

    [Fact]
    public void L_empreinte_couvre_le_lot_serialise_avec_une_empreinte_vide()
    {
        var lot = Lot(new LigneSyncDto(1, "vente", "1", ContratSync.Creation, Le, """{"id":1}"""));
        var attendue = Convert.ToHexStringLower(SHA256.HashData(SerialisationSyncV1.Serialiser(lot)));
        var signe = SerialisationSyncV1.AvecEmpreinte(lot);
        Assert.Equal(attendue, signe.EnTete.Empreinte);
        Assert.Matches("^[0-9a-f]{64}$", signe.EnTete.Empreinte);
        Assert.True(SerialisationSyncV1.EmpreinteValide(signe));
        // Toute modification (donnée, n° de lot, officine) change l'empreinte
        Assert.False(SerialisationSyncV1.EmpreinteValide(signe with { Lignes = [signe.Lignes[0] with { DonneesJson = """{"id":2}""" }] }));
        Assert.False(SerialisationSyncV1.EmpreinteValide(signe with { EnTete = signe.EnTete with { NumeroLot = 2 } }));
        Assert.False(SerialisationSyncV1.EmpreinteValide(signe with { EnTete = signe.EnTete with { OfficineId = Guid.NewGuid() } }));
    }

    [Fact]
    public void Un_lot_relu_est_identique_et_garde_son_empreinte()
    {
        var lot = SerialisationSyncV1.AvecEmpreinte(Lot(
            new LigneSyncDto(3, "patient", "P-0123456789abcdef01234567", ContratSync.Modification, Le, """{"id":"P-0123456789abcdef01234567","solde_credit":0.00}""")));
        var octets = SerialisationSyncV1.Serialiser(lot);
        var relu = SerialisationSyncV1.Lire(octets);
        Assert.Equal(octets, SerialisationSyncV1.Serialiser(relu));
        Assert.True(SerialisationSyncV1.EmpreinteValide(relu));
        Assert.Equal(Le, relu.Lignes[0].SurvenuLeUtc);
    }

    [Fact]
    public void Un_lot_mal_forme_est_refuse()
    {
        Assert.Throws<ArgumentException>(() => SerialisationSyncV1.Serialiser(Lot() with { EnTete = Lot().EnTete with { VersionContrat = "sync-v0" } }));
        Assert.Throws<ArgumentException>(() => SerialisationSyncV1.Serialiser(Lot() with { EnTete = Lot().EnTete with { NombreLignes = 3 } }));
        Assert.Throws<ArgumentException>(() => SerialisationSyncV1.Serialiser(Lot(new LigneSyncDto(1, "vente", "1", "maj", Le, "{}"))));
        Assert.Throws<ArgumentException>(() => SerialisationSyncV1.Serialiser(Lot(new LigneSyncDto(1, "vente", "1", ContratSync.Suppression, Le, "{}"))));
        Assert.Throws<ArgumentException>(() => SerialisationSyncV1.Serialiser(Lot(new LigneSyncDto(1, "vente", "1", ContratSync.Creation, Le, null))));
    }

    // ---- Constitution des lots ----

    private sealed class FormatV1 : IFormatLotSync
    {
        public string VersionContrat => ContratSync.VersionV1;
        public int TailleOctets(LigneSortieSync l) => SerialisationSyncV1.TailleLigne(Dto(l));
        public LotFormule Formuler(EnTeteLotSync e, IReadOnlyList<LigneSortieSync> lignes)
        {
            var lot = SerialisationSyncV1.AvecEmpreinte(new LotSyncDto(
                new EnTeteLotSyncDto(e.VersionContrat, e.OfficineId, e.Epoque, e.NumeroLot, lignes.Count, ""), lignes.Select(Dto).ToList()));
            return new LotFormule(SerialisationSyncV1.Serialiser(lot), lot.EnTete.Empreinte);
        }
        private static LigneSyncDto Dto(LigneSortieSync l) => new(l.Numero, l.Table, l.IdLocal, l.Operation, l.SurvenuLeUtc, l.DonneesJson);
    }

    private sealed class DepotFactice(EtatSync etat, IEnumerable<LigneSortieSync> lignes) : ISortieSyncDepot
    {
        public EtatSync Etat { get; set; } = etat;
        public List<LigneSortieSync> Attente { get; } = lignes.ToList();
        public List<LotConstitue> Lots { get; } = [];
        public bool Simultanee { get; set; }

        public Task<EtatSync> EtatAsync(CancellationToken ct = default) => Task.FromResult(Etat);
        public Task<IReadOnlyList<LigneSortieSync>> LignesEnAttenteAsync(int maximum, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LigneSortieSync>>(Attente.OrderBy(l => l.Numero).Take(maximum).ToList());
        public Task<bool> EnregistrerLotAsync(LotConstitue lot, CancellationToken ct = default)
        {
            if (Simultanee || lot.Numero != Etat.DernierLot + 1) return Task.FromResult(false);
            Lots.Add(lot);
            Attente.RemoveAll(l => lot.Lignes.Contains(l.Numero));
            Etat = Etat with { DernierLot = lot.Numero };
            return Task.FromResult(true);
        }
    }

    private static EtatSync Etat(bool active = true, Guid? officine = null, int maxLignes = 500, int maxOctets = 1_048_576, long dernierLot = 0) =>
        new(active, officine ?? Officine, 1, ContratSync.VersionV1, dernierLot, 0, maxLignes, maxOctets, 0);

    private static IEnumerable<LigneSortieSync> Lignes(int n, int tailleDonnees = 10) =>
        Enumerable.Range(1, n).Select(i => new LigneSortieSync(i, "vente", i.ToString(), ContratSync.Creation, Le,
            $$"""{"id":{{i}},"texte":"{{new string('x', tailleDonnees)}}"}"""));

    [Fact]
    public async Task Desactivee_ou_non_inscrite_rien_n_est_constitue()
    {
        var depot = new DepotFactice(Etat(active: false), Lignes(3));
        var r = await new ConstitutionLotsSync(depot, new FormatV1()).ConstituerAsync();
        Assert.Equal(ConstitutionLotsSync.MotifDesactivee, r.Motif);
        depot.Etat = Etat(officine: Guid.Empty);
        r = await new ConstitutionLotsSync(depot, new FormatV1()).ConstituerAsync();
        Assert.Equal(ConstitutionLotsSync.MotifNonInscrite, r.Motif);
        Assert.Empty(depot.Lots);
    }

    [Fact]
    public async Task Les_lots_sont_bornes_en_lignes_et_numerotes_sans_trou()
    {
        var depot = new DepotFactice(Etat(maxLignes: 4, dernierLot: 6), Lignes(10));
        var r = await new ConstitutionLotsSync(depot, new FormatV1()).ConstituerAsync();
        Assert.Null(r.Motif);
        Assert.Equal((3, 10), (r.Lots, r.Lignes));
        Assert.Equal([7L, 8L, 9L], depot.Lots.Select(l => l.Numero));
        Assert.Equal([4, 4, 2], depot.Lots.Select(l => l.Lignes.Count));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), depot.Lots.SelectMany(l => l.Lignes));
        Assert.All(depot.Lots, l => Assert.Matches("^[0-9a-f]{64}$", l.Empreinte));
    }

    [Fact]
    public async Task Les_lots_sont_bornes_en_octets_et_une_ligne_trop_grosse_part_seule()
    {
        var format = new FormatV1();
        var ligne = Lignes(1, 100).Single();
        var taille = format.TailleOctets(ligne);
        var depot = new DepotFactice(Etat(maxOctets: Math.Max(1024, taille * 3)), Lignes(7, 100));
        await new ConstitutionLotsSync(depot, format).ConstituerAsync();
        Assert.All(depot.Lots, l => Assert.True(l.Lignes.Count <= Math.Max(1024, taille * 3) / taille));
        Assert.Equal(7, depot.Lots.Sum(l => l.Lignes.Count));

        var grosse = new DepotFactice(Etat(maxOctets: 1024), Lignes(2, 3000));
        await new ConstitutionLotsSync(grosse, format).ConstituerAsync();
        Assert.Equal([1, 1], grosse.Lots.Select(l => l.Lignes.Count));
    }

    [Fact]
    public async Task L_empreinte_enregistree_est_celle_du_lot_reconstitue()
    {
        var depot = new DepotFactice(Etat(), Lignes(3));
        await new ConstitutionLotsSync(depot, new FormatV1()).ConstituerAsync();
        var lot = depot.Lots.Single();
        var refait = new FormatV1().Formuler(new EnTeteLotSync(ContratSync.VersionV1, Officine, 1, 1), Lignes(3).ToList());
        Assert.Equal(refait.Empreinte, lot.Empreinte);
        Assert.Equal(refait.Contenu.Length, lot.Octets);
    }

    [Fact]
    public async Task Une_constitution_simultanee_arrete_sans_rien_perdre()
    {
        var depot = new DepotFactice(Etat(), Lignes(3)) { Simultanee = true };
        var r = await new ConstitutionLotsSync(depot, new FormatV1()).ConstituerAsync();
        Assert.Equal(ConstitutionLotsSync.MotifSimultanee, r.Motif);
        Assert.Equal(3, depot.Attente.Count);
    }

    [Fact]
    public async Task Une_version_de_contrat_inconnue_est_refusee()
    {
        var depot = new DepotFactice(Etat() with { VersionContrat = "sync-v2" }, Lignes(1));
        var r = await new ConstitutionLotsSync(depot, new FormatV1()).ConstituerAsync();
        Assert.Contains("sync-v2", r.Motif);
        Assert.Empty(depot.Lots);
    }
}
