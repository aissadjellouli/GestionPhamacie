using System.Security.Cryptography;
using System.Text;
using Pharma.Application.Administration;
using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Fiscal;
using Pharma.Application.Integrite;
using Pharma.Application.Notifications;
using Pharma.Domain.Caisse;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

/// <summary>Journal fiscal des ventes (lot 3) : clôture scellée, continuité, export d'audit, période.</summary>
public class JournalFiscalTests
{
    private static readonly DateTime Maintenant = new(2026, 11, 16, 18, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Cloture_de_caisse_scelle_le_journal_imprime_le_sceau_et_archive_le_rapport()
    {
        var (integrite, fiscal, pdf, archives, cloture) = Monter();
        var resultat = await cloture.ExecuterAsync(7, 3);

        var sceau = Assert.Single(integrite.Sceaux);
        Assert.Equal(JournalFiscal.Journal, sceau.Journal);
        Assert.Equal(MotifSceau.ClotureJournaliere, sceau.Motif);
        Assert.Equal(42, sceau.DernierRang);
        Assert.Equal(3, sceau.AuteurId);
        // Le sceau est imprimé sur le rapport de clôture archivé, et la clôture fiscale relie session et sceau
        Assert.Contains(Convert.ToHexStringLower(sceau.Sceau), pdf.SceauImprime);
        Assert.Single(archives.Fichiers);
        var enregistree = Assert.Single(fiscal.Clotures);
        Assert.Equal((JournalFiscal.TypeJournaliere, 7L, sceau.Id), (enregistree.Type, enregistree.SessionCaisseId!.Value, enregistree.SceauId));
        Assert.NotNull(enregistree.EmpreinteArchive);
        Assert.NotNull(resultat);

        // Une session déjà scellée ne l'est pas deux fois
        await cloture.ExecuterAsync(7, 3);
        Assert.Single(integrite.Sceaux);
    }

    [Fact]
    public async Task Archivage_impossible_n_empeche_pas_le_sceau()
    {
        var (integrite, fiscal, _, archives, cloture) = Monter();
        archives.Echec = true;
        await cloture.ExecuterAsync(7, 3);
        Assert.Single(integrite.Sceaux);
        Assert.Null(Assert.Single(fiscal.Clotures).FichierArchive);
    }

    [Fact]
    public async Task Continuite_rompue_enregistree_et_signalee_au_titulaire()
    {
        var fiscal = new FiscalFactice { Anomalies = [new AnomalieNumerotation("numero_manquant", "T-000124", null)] };
        var integrite = new IntegriteFactice();
        var alertes = new AlertesFactices();
        var anomalies = await new ControlerNumerotation(fiscal, integrite, alertes).ExecuterAsync(null);

        Assert.Single(anomalies);
        var v = Assert.Single(integrite.Verifications);
        Assert.Equal((JournalFiscal.Journal, JournalFiscal.CleNumerotation, VerifierIntegrite.Rupture), (v.Journal, v.CleChaine, v.Statut));
        Assert.Contains("T-000124", v.Motif);
        Assert.True(Assert.Single(alertes.Messages).Urgent);
    }

    [Fact]
    public async Task Continuite_intacte_enregistree_sans_alerte()
    {
        var integrite = new IntegriteFactice();
        var alertes = new AlertesFactices();
        await new ControlerNumerotation(new FiscalFactice(), integrite, alertes).ExecuterAsync(5);
        Assert.Equal(VerifierIntegrite.Ok, Assert.Single(integrite.Verifications).Statut);
        Assert.Empty(alertes.Messages);
    }

    [Fact]
    public void Export_csv_contient_lignes_tva_et_sceaux_et_neutralise_les_formules()
    {
        var p = new ParametresFiscaux(6, "csv-v1", ";", "Africa/Algiers");
        var lignes = new[]
        {
            new LigneJournalFiscal(2, 2, "vente", "T-000001", Maintenant, "CAISSE-1", "Ali", 119, 100, 19, 119, 0, "especes:119.00", 0, "=1+1", "ab12"),
            new LigneJournalFiscal(3, 3, "annulation", "T-000001", Maintenant, "CAISSE-1", "Ali", -119, -100, -19, -119, 0, "especes:-119.00", 0, "erreur; client", "cd34")
        };
        var tva = new[] { new LigneRecapTva(0, 0, 0, 0, 2), new LigneRecapTva(19, 0, 0, 0, 2) };
        var sceaux = new[] { new ClotureFiscale(1, "journaliere", 7, new(2026, 11, 16), new(2026, 11, 16), 9, Maintenant, 3, "HMAC-SHA256", "abcd", [0xAB, 0xCD], 2, 0, 0, 0, null) };
        var texte = Encoding.UTF8.GetString(ExportFiscalCsv.Construire(p, new(2026, 11, 1), new(2026, 11, 30), lignes, tva, sceaux, "hex")).TrimStart('﻿');

        Assert.StartsWith("#FORMAT;csv-v1;periode;2026-11-01;2026-11-30", texte);
        Assert.Contains("2;2;vente;T-000001;2026-11-16T18:30:00Z;CAISSE-1;Ali;119.00;100.00;19.00", texte);
        Assert.Contains("'=1+1", texte);
        Assert.Contains("\"erreur; client\"", texte);
        Assert.Contains("0.00;exonere", texte);
        Assert.Contains("19.00;taxe", texte);
        Assert.Contains(";abcd;abcd", texte);
    }

    [Fact]
    public void Periode_inversee_ou_trop_longue_refusee_et_mois_courant_par_defaut()
    {
        var aujourdhui = new DateOnly(2026, 11, 16);
        Assert.Equal((new DateOnly(2026, 11, 1), aujourdhui), PeriodeFiscale.Lire(null, null, aujourdhui));
        Assert.Throws<RegleMetierException>(() => PeriodeFiscale.Lire(aujourdhui, aujourdhui.AddDays(-1), aujourdhui));
        Assert.Throws<RegleMetierException>(() => PeriodeFiscale.Lire(aujourdhui.AddYears(-2), aujourdhui, aujourdhui));
    }

    [Fact]
    public void Texte_du_sceau_suit_le_format_d_export()
    {
        var c = new ClotureFiscale(1, "mensuelle", null, new(2026, 10, 1), new(2026, 10, 31), 12, Maintenant, 99, "HMAC-SHA256", "0123", [0xFF, 0x00], 0, 0, 0, 0, null);
        Assert.Contains("n° 12", TexteSceau.Pour(c, "hex"));
        Assert.EndsWith("ff00", TexteSceau.Pour(c, "hex"));
        Assert.EndsWith("/wA=", TexteSceau.Pour(c, "base64"));
        Assert.Contains("ligne 99", TexteSceau.Pour(c, "hex"));
    }

    // ---- Montage et doublures ----

    private static (IntegriteFactice, FiscalFactice, PdfFactice, ArchivesFactices, ScellerClotureCaisse) Monter()
    {
        var integrite = new IntegriteFactice();
        var fiscal = new FiscalFactice();
        var pdf = new PdfFactice();
        var archives = new ArchivesFactices();
        var horloge = new Horloge();
        var cloture = new ScellerClotureCaisse(new ScellerJournal(integrite, new Scelleur(), horloge), fiscal, new SessionsFactices(), pdf, archives,
            integrite, new PharmacieFactice(), horloge);
        return (integrite, fiscal, pdf, archives, cloture);
    }

    private sealed class Horloge : IHorloge
    {
        public DateTime MaintenantUtc => Maintenant;
        public DateOnly Aujourdhui => DateOnly.FromDateTime(Maintenant);
    }

    private sealed class Scelleur : IScelleurIntegrite
    {
        private static readonly byte[] Cle = Encoding.ASCII.GetBytes("cle-de-test-de-32-octets-exactem");
        public string Algorithme => "HMAC-SHA256";
        public string EmpreinteCle => Convert.ToHexStringLower(SHA256.HashData(Cle))[..16];
        public byte[] Sceller(byte[] message) => HMACSHA256.HashData(Cle, message);
        public bool Verifier(byte[] message, byte[] sceau) => CryptographicOperations.FixedTimeEquals(Sceller(message), sceau);
    }

    private sealed class IntegriteFactice : IIntegriteDepot
    {
        public List<SceauEnregistre> Sceaux { get; } = [];
        public List<NouvelleVerification> Verifications { get; } = [];
        public Task<IReadOnlyList<JournalChaine>> JournauxAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<JournalChaine>>([new JournalChaine(JournalFiscal.Journal, "Journal des ventes", "sha256", Maintenant)]);
        public Task<IReadOnlyList<ResultatChaine>> VerifierChaineAsync(string journal, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ResultatChaine>>([]);
        public Task<MaillonChaine> DernierMaillonAsync(string journal, string cleChaine, CancellationToken ct = default) =>
            Task.FromResult(new MaillonChaine(42, 420, SHA256.HashData([42])));
        public Task<byte[]?> HashAuRangAsync(string journal, string cleChaine, long rang, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<long> EnregistrerSceauAsync(NouveauSceau s, CancellationToken ct = default)
        {
            Sceaux.Add(new SceauEnregistre(Sceaux.Count + 1, s.Journal, s.CleChaine, s.Motif, s.PeriodeDu, s.PeriodeAu, s.ScelleLeUtc, s.DernierRang,
                s.DernierId, s.DernierHash, s.Algorithme, s.EmpreinteCle, s.Sceau, s.AuteurId));
            return Task.FromResult((long)Sceaux.Count);
        }
        public Task<IReadOnlyList<SceauEnregistre>> SceauxAsync(string journal, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SceauEnregistre>>(Sceaux);
        public Task EnregistrerVerificationAsync(NouvelleVerification v, CancellationToken ct = default) { Verifications.Add(v); return Task.CompletedTask; }
        public Task<IReadOnlyList<DerniereVerification>> DernieresVerificationsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DerniereVerification>>([]);
        public Task<ParametresIntegrite> ParametresAsync(CancellationToken ct = default) => Task.FromResult(new ParametresIntegrite("sha256", 3, "hex"));
        public Task JournaliserRuptureSystemeAsync(string journal, IReadOnlyDictionary<string, object?> details, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FiscalFactice : IJournalFiscalDepot
    {
        public List<NouvelleClotureFiscale> Clotures { get; } = [];
        public IReadOnlyList<AnomalieNumerotation> Anomalies { get; set; } = [];
        private static readonly TotauxFiscaux Totaux = new(2, 119, 100, 19);
        public Task<Page<LigneJournalFiscal>> RechercherAsync(DateOnly du, DateOnly au, string? ticket, DemandePage page, CancellationToken ct = default) =>
            Task.FromResult(new Page<LigneJournalFiscal>([], 0, 1, page.Taille));
        public Task<IReadOnlyList<LigneJournalFiscal>> LignesAsync(DateOnly du, DateOnly au, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LigneJournalFiscal>>([]);
        public Task<IReadOnlyList<LigneRecapTva>> RecapTvaAsync(DateOnly du, DateOnly au, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LigneRecapTva>>([]);
        public Task<TotauxFiscaux> TotauxAsync(DateOnly du, DateOnly au, CancellationToken ct = default) => Task.FromResult(Totaux);
        public Task<IReadOnlyList<AnomalieNumerotation>> ContinuiteAsync(CancellationToken ct = default) => Task.FromResult(Anomalies);
        public Task<IReadOnlyList<TrouHistorique>> TrousHistoriqueAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TrouHistorique>>([]);
        public Task<IReadOnlyList<ClotureFiscale>> CloturesAsync(DateOnly du, DateOnly au, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ClotureFiscale>>([]);
        public Task<ClotureFiscale?> ClotureSessionAsync(long sessionCaisseId, CancellationToken ct = default) =>
            Task.FromResult(Clotures.FirstOrDefault(c => c.SessionCaisseId == sessionCaisseId) is { } c
                ? new ClotureFiscale(1, c.Type, c.SessionCaisseId, c.Du, c.Au, c.SceauId, Maintenant, 42, "HMAC-SHA256", "cle", [1], 2, 119, 100, 19, c.FichierArchive)
                : null);
        public Task<bool> MoisClotureAsync(DateOnly premierJour, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<long>> SessionsNonScelleesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<long>>([]);
        public Task<long> EnregistrerClotureAsync(NouvelleClotureFiscale c, CancellationToken ct = default) { Clotures.Add(c); return Task.FromResult((long)Clotures.Count); }
        public Task<ParametresFiscaux> ParametresAsync(CancellationToken ct = default) => Task.FromResult(new ParametresFiscaux(6, "csv-v1", ";", "Africa/Algiers"));
        public Task<CompteurTickets> CompteurAsync(CancellationToken ct = default) => Task.FromResult(new CompteurTickets("T-", 10, 1, Maintenant));
    }

    private sealed class PdfFactice : IDocumentsPdf
    {
        public string? SceauImprime { get; private set; }
        public byte[] RapportCloture(DetailSession session, bool avecEcart, decimal seuilEcart, string? pharmacie)
        {
            SceauImprime = session.SceauFiscal;
            return Encoding.UTF8.GetBytes("pdf " + session.SceauFiscal);
        }
    }

    private sealed class ArchivesFactices : IArchivesFiscales
    {
        public bool Echec { get; set; }
        public List<string> Fichiers { get; } = [];
        public Task<string> RangerAsync(string sousDossier, string nomFichier, byte[] contenu, CancellationToken ct = default)
        {
            if (Echec) throw new IOException("disque plein");
            Fichiers.Add(sousDossier + "/" + nomFichier);
            return Task.FromResult(Fichiers[^1]);
        }
    }

    private sealed class PharmacieFactice : IPharmacie
    {
        public Task<IdentitePharmacie> LireAsync(CancellationToken ct = default) => Task.FromResult(new IdentitePharmacie("Pharmacie", null, null, null, null, null, "fr"));
        public Task EnregistrerAsync(IdentitePharmacie identite, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class SessionsFactices : ISessionsCaisse
    {
        public Task<SessionCaisse?> SessionDuPosteAsync(string poste, CancellationToken ct = default) => Task.FromResult<SessionCaisse?>(null);
        public Task<long> OuvrirAsync(long utilisateurId, string poste, decimal fondCaisse, long? releveDe = null, CancellationToken ct = default) => Task.FromResult(1L);
        public Task<IReadOnlyList<Vendeur>> VendeursAsync(long sessionId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Vendeur>>([new Vendeur(3, "Ali")]);
        public Task<decimal> VentesEspecesAsync(long sessionId, CancellationToken ct = default) => Task.FromResult(0m);
        public Task CloturerAsync(long sessionId, Comptage comptage, ResultatCloture resultat, CancellationToken ct = default) => Task.CompletedTask;
        public Task<DetailSession?> DetailAsync(long sessionId, CancellationToken ct = default) =>
            Task.FromResult<DetailSession?>(new DetailSession(sessionId, 3, "Ali", "CAISSE-1", Maintenant.AddHours(-8), 1000, Maintenant, 500, 1500, 1500, 0, 2, 119, []));
        public Task<string?> NomEmployeAsync(long utilisateurId, CancellationToken ct = default) => Task.FromResult<string?>("Ali");
        public Task<ParametresCaisse> ParametresAsync(CancellationToken ct = default) => Task.FromResult(new ParametresCaisse(false, 1000, 100, 0));
        public Task ModifierParametresAsync(ParametresCaisse parametres, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class AlertesFactices : IAlertesTitulaire
    {
        public List<(string Message, bool Urgent)> Messages { get; } = [];
        public Task SurveillanceAsync(string message, bool urgent, CancellationToken ct = default) { Messages.Add((message, urgent)); return Task.CompletedTask; }
        public Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default, bool releve = false) => Task.CompletedTask;
        public Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default) => Task.CompletedTask;
        public Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default) => Task.CompletedTask;
        public Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default) => Task.CompletedTask;
        public Task AnnulationsAsync(string poste, int annulations, int seuil, CancellationToken ct = default) => Task.CompletedTask;
        public Task LotDelaisseAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
    }
}
