using System.Text.Json;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;

namespace Pharma.Application.Synchronisation;

// ---- Référentiels, lot 2 (docs/conception-synchronisation.md § 28) : organismes payeurs et centres payeurs envoyés par le central ----
// Organismes désignés par leur sigle, centres payeurs (CNAS, CASNOS) par leur code. Ajouts et corrections SEULEMENT : rien n'est supprimé
// ni désactivé, l'état actif d'une ligne existante n'est jamais changé. Écrits par les cas d'usage des écrans (Facturation,
// CentresPayeurs) : mêmes contrôles, même journal. Tout est contrôlé avant la première écriture.

/// <summary>Changements qu'un ordre « organismes » ferait dans l'officine.</summary>
public sealed record ChangementsOrganismes(IReadOnlyList<string> Creations, IReadOnlyList<string> Corrections, int CentresCrees, int CentresCorriges)
{
    public bool Vide => Creations.Count + Corrections.Count + CentresCrees + CentresCorriges == 0;

    public string Texte() => Vide ? "Rien ne change : organismes et centres déjà à jour."
        : string.Join(" ; ", new[]
        {
            Creations.Count > 0 ? $"organisme(s) ajouté(s) : {string.Join(", ", Creations)}" : null,
            Corrections.Count > 0 ? $"corrigé(s) : {string.Join(", ", Corrections)}" : null,
            CentresCrees > 0 ? $"{CentresCrees} centre(s) payeur(s) ajouté(s)" : null,
            CentresCorriges > 0 ? $"{CentresCorriges} centre(s) payeur(s) renommé(s)" : null
        }.Where(x => x is not null)) + ".";
}

public interface IOrganismesCentraux
{
    /// <summary>Changements prévus, sans rien écrire ; données invalides : OrdreInapplicableException.</summary>
    Task<ChangementsOrganismes> CalculerAsync(JsonElement donnees, CancellationToken ct = default);
    /// <summary>Application au nom du titulaire qui confirme.</summary>
    Task<ChangementsOrganismes> AppliquerAsync(JsonElement donnees, long titulaireId, CancellationToken ct = default);
}

public sealed class OrganismesCentraux(Factures.IFactures factures, Factures.Facturation facturation, Chifa.ICentresPayeurs centres,
    Chifa.CentresPayeurs gestionCentres) : IOrganismesCentraux
{
    private sealed record CentreRecu(string Code, string Nom);
    private sealed record OrganismeRecu(string Code, string Nom, string? Adresse, string? Nif, string? Telephone, string? Email, int? Delai,
        IReadOnlyList<CentreRecu> Centres);
    private sealed record Plan(List<(Factures.OrganismeFiche Fiche, bool Change)> Organismes,
        List<(string Organisme, long? Id, string Code, string Nom, bool Actif)> Centres, ChangementsOrganismes Changements);

    public async Task<ChangementsOrganismes> CalculerAsync(JsonElement donnees, CancellationToken ct = default) =>
        (await PlanifierAsync(Lire(donnees), ct)).Changements;

    public async Task<ChangementsOrganismes> AppliquerAsync(JsonElement donnees, long titulaireId, CancellationToken ct = default)
    {
        var plan = await PlanifierAsync(Lire(donnees), ct);
        var ids = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fiche, change) in plan.Organismes)
        {
            ids[fiche.Code] = change ? await facturation.EnregistrerOrganismeAsync(fiche, titulaireId, ct) : fiche.Id!.Value;
        }
        foreach (var c in plan.Centres)
            await gestionCentres.EnregistrerAsync(c.Id, ids[c.Organisme], c.Code, c.Nom, c.Actif, titulaireId, ct);
        return plan.Changements;
    }

    private static List<OrganismeRecu> Lire(JsonElement d)
    {
        try
        {
            static string? T(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return d.GetProperty("organismes").EnumerateArray().Select(e => new OrganismeRecu(
                T(e, "code") ?? "", T(e, "nom") ?? "", T(e, "adresse"), T(e, "nif"), T(e, "telephone"), T(e, "email"),
                e.TryGetProperty("delai_paiement_jours", out var dl) && dl.ValueKind == JsonValueKind.Number ? dl.GetInt32() : null,
                e.TryGetProperty("centres", out var cs) && cs.ValueKind == JsonValueKind.Array
                    ? cs.EnumerateArray().Select(c => new CentreRecu(T(c, "code") ?? "", T(c, "nom") ?? "")).ToList() : [])).ToList();
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new OrdreInapplicableException("Liste d’organismes mal formée.");
        }
    }

    private async Task<Plan> PlanifierAsync(List<OrganismeRecu> recus, CancellationToken ct)
    {
        if (recus.Count == 0) throw new OrdreInapplicableException("Aucun organisme dans l’ordre.");
        var existants = await factures.OrganismesAsync(ct);
        var organismes = new List<(Factures.OrganismeFiche, bool)>();
        var lignesCentres = new List<(string, long?, string, string, bool)>();
        var (creations, corrections) = (new List<string>(), new List<string>());
        var (centresCrees, centresCorriges) = (0, 0);
        try
        {
            foreach (var r in recus)
            {
                // Mêmes contrôles que l'écran (Facturation.EnregistrerOrganismeAsync), faits ici avant toute écriture
                var code = r.Code.Trim().ToUpperInvariant();
                if (code.Length == 0 || string.IsNullOrWhiteSpace(r.Nom)) throw new RegleMetierException("Sigle et nom de l’organisme obligatoires.");
                if (r.Delai is { } dl) Pharma.Domain.Factures.RegleFacture.VerifierDelaiPaiement(dl);
                var tel = Contacts.RegleContact.Telephone(r.Telephone);
                var mail = Contacts.RegleContact.Email(r.Email);
                var ex = existants.FirstOrDefault(o => string.Equals(o.Code.Trim(), code, StringComparison.OrdinalIgnoreCase));
                Factures.OrganismeFiche fiche;
                if (ex is null)
                {
                    fiche = new Factures.OrganismeFiche(null, code, r.Nom.Trim(), r.Adresse?.Trim(), r.Nif?.Trim(), true, tel, mail,
                        r.Delai ?? Pharma.Domain.Factures.RegleFacture.DelaiEcheanceJours);
                    creations.Add(code);
                    organismes.Add((fiche, true));
                }
                else
                {
                    // Une valeur absente de l'ordre garde la valeur de l'officine ; l'état actif n'est jamais changé
                    fiche = ex with
                    {
                        Nom = r.Nom.Trim(), Adresse = r.Adresse?.Trim() ?? ex.Adresse, Nif = r.Nif?.Trim() ?? ex.Nif,
                        Telephone = tel ?? ex.Telephone, Email = mail ?? ex.Email, DelaiPaiementJours = r.Delai ?? ex.DelaiPaiementJours
                    };
                    var change = fiche != ex;
                    if (change) corrections.Add(code);
                    organismes.Add((fiche, change));
                }
                if (r.Centres.Count == 0) continue;
                if (!Caisse.OrganismeVoletChifa.Accepte(code)) throw new RegleMetierException("Un centre payeur appartient à la CNAS ou à la CASNOS.");
                var actuels = ex?.Id is { } oid ? await TousLesCentresAsync(oid, ct) : [];
                foreach (var c in r.Centres)
                {
                    var (cc, cn) = Pharma.Domain.Ventes.RegleCentrePayeur.Verifier(c.Code, c.Nom);
                    var actuel = actuels.FirstOrDefault(a => string.Equals(a.Code.Trim(), cc, StringComparison.OrdinalIgnoreCase));
                    if (actuel is null) { centresCrees++; lignesCentres.Add((code, null, cc, cn, true)); }
                    else if (actuel.Nom != cn) { centresCorriges++; lignesCentres.Add((code, actuel.Id, actuel.Code, cn, actuel.Actif)); }
                }
            }
        }
        catch (RegleMetierException e)
        {
            throw new OrdreInapplicableException($"Organismes refusés, rien n’a été changé : {e.Message}");
        }
        return new Plan(organismes, lignesCentres, new ChangementsOrganismes(creations, corrections, centresCrees, centresCorriges));
    }

    private async Task<List<Chifa.CentrePayeur>> TousLesCentresAsync(long organismeId, CancellationToken ct)
    {
        var tous = new List<Chifa.CentrePayeur>();
        for (var page = 1; ; page++)
        {
            var p = await centres.ListerAsync(organismeId, null, null, new DemandePage(page, DemandePage.TailleMax), ct);
            tous.AddRange(p.Lignes);
            if (p.Lignes.Count < DemandePage.TailleMax || tous.Count >= p.Total) return tous;
        }
    }
}
