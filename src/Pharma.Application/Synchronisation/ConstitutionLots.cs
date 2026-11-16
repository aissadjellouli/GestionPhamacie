namespace Pharma.Application.Synchronisation;

// ---- Synchronisation, lot S1 : constitution des lots montants, SANS envoi (docs/conception-synchronisation.md § 3) ----
// La file (sync_sortie, script 126) est alimentée par la base dans la transaction de chaque opération. Ce service lit les
// lignes pas encore rattachées à un lot, forme des lots bornés (nombre de lignes et octets), les fait mettre en forme et
// empreindre par le contrat (port IFormatLotSync, contrat sync-v1 de Pharma.Shared) et les enregistre : n° de lot = dernier + 1,
// attribué dans la transaction d'enregistrement, donc sans trou. L'envoi au serveur central est le lot S3.

/// <summary>État de la synchronisation (ligne unique sync_parametre) et taille de la file.</summary>
public sealed record EtatSync(bool Active, Guid? OfficineId, int Epoque, string VersionContrat, long DernierLot, long DernierAccuse,
    int LotMaxLignes, int LotMaxOctets, long LignesEnAttente);

/// <summary>Ligne de la file, données déjà minimisées par la base (patients et porteurs sous code).</summary>
/// <param name="Numero">N° de la ligne dans la file (sync_sortie.id).</param>
/// <param name="Ordre">Ligne d'instantané (lot S6) : repère du chargement ; null pour une modification (ordre = n° de ligne).</param>
public sealed record LigneSortieSync(long Numero, string Table, string IdLocal, string Operation, DateTime SurvenuLeUtc, string? DonneesJson, long? Ordre = null)
{
    /// <summary>N° transmis au serveur central (état retenu = plus grand) et clé d'ordre d'envoi, avec le n° de ligne.</summary>
    public long NumeroEnvoye => Ordre ?? Numero;
}

/// <summary>En-tête d'un lot à mettre en forme.</summary>
public sealed record EnTeteLotSync(string VersionContrat, Guid OfficineId, int Epoque, long NumeroLot);

/// <summary>Lot mis en forme : contenu sérialisé et empreinte SHA-256 (hexadécimal).</summary>
public sealed record LotFormule(byte[] Contenu, string Empreinte);

/// <summary>Lot à enregistrer : n° attendu, lignes rattachées, taille et empreinte.</summary>
public sealed record LotConstitue(long Numero, IReadOnlyList<long> Lignes, int Octets, string Empreinte);

public sealed record ResultatConstitution(int Lots, int Lignes, string? Motif)
{
    public static ResultatConstitution Arret(string motif) => new(0, 0, motif);
}

/// <summary>Accès à la file et aux lots (script 126).</summary>
public interface ISortieSyncDepot
{
    Task<EtatSync> EtatAsync(CancellationToken ct = default);
    /// <summary>Lignes pas encore rattachées à un lot, de l'époque courante, par (ordre, n°) croissants (lot S6).</summary>
    Task<IReadOnlyList<LigneSortieSync>> LignesEnAttenteAsync(int maximum, CancellationToken ct = default);
    /// <summary>Enregistre le lot (sync_enregistrer_lot) ; false si son n° n'est plus le suivant (constitution simultanée).</summary>
    Task<bool> EnregistrerLotAsync(LotConstitue lot, CancellationToken ct = default);
}

/// <summary>Mise en forme d'un lot selon le contrat (sync-v1) : sérialisation déterministe et empreinte.</summary>
public interface IFormatLotSync
{
    string VersionContrat { get; }
    int TailleOctets(LigneSortieSync ligne);
    LotFormule Formuler(EnTeteLotSync enTete, IReadOnlyList<LigneSortieSync> lignes);
}

public sealed class ConstitutionLotsSync(ISortieSyncDepot depot, IFormatLotSync format)
{
    public const string MotifDesactivee = "Synchronisation désactivée.";
    public const string MotifNonInscrite = "Officine non inscrite au serveur central.";
    public const string MotifSimultanee = "Constitution simultanée : reprise au prochain passage.";

    /// <summary>Forme au plus <paramref name="maxLots"/> lots avec les lignes en attente.</summary>
    public async Task<ResultatConstitution> ConstituerAsync(int maxLots = 10, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLots, 1);
        var etat = await depot.EtatAsync(ct);
        if (!etat.Active) return ResultatConstitution.Arret(MotifDesactivee);
        if (etat.OfficineId is not { } officine || officine == Guid.Empty) return ResultatConstitution.Arret(MotifNonInscrite);
        if (etat.VersionContrat != format.VersionContrat)
            return ResultatConstitution.Arret($"Version de contrat {etat.VersionContrat} non prise en charge (attendue : {format.VersionContrat}).");

        int lots = 0, lignes = 0;
        var numero = etat.DernierLot;
        while (lots < maxLots)
        {
            var attente = await depot.LignesEnAttenteAsync(etat.LotMaxLignes, ct);
            if (attente.Count == 0) break;
            var choisies = Borner(attente, etat.LotMaxLignes, etat.LotMaxOctets);
            numero++;
            var formule = format.Formuler(new EnTeteLotSync(etat.VersionContrat, officine, etat.Epoque, numero), choisies);
            var lot = new LotConstitue(numero, choisies.Select(l => l.Numero).ToList(), formule.Contenu.Length, formule.Empreinte);
            if (!await depot.EnregistrerLotAsync(lot, ct)) return new ResultatConstitution(lots, lignes, MotifSimultanee);
            lots++;
            lignes += choisies.Count;
        }
        return new ResultatConstitution(lots, lignes, null);
    }

    /// <summary>
    /// Plus long début de la file tenant dans les bornes ; au moins une ligne (une ligne seule plus grande que la borne
    /// d'octets forme un lot à elle seule, sinon la file serait bloquée).
    /// </summary>
    public List<LigneSortieSync> Borner(IReadOnlyList<LigneSortieSync> attente, int maxLignes, int maxOctets)
    {
        var choisies = new List<LigneSortieSync>();
        long octets = 0;
        foreach (var l in attente.OrderBy(l => l.NumeroEnvoye).ThenBy(l => l.Numero))
        {
            if (choisies.Count >= maxLignes) break;
            var taille = format.TailleOctets(l);
            if (choisies.Count > 0 && octets + taille > maxOctets) break;
            choisies.Add(l);
            octets += taille;
        }
        return choisies;
    }
}
