using Pharma.Application.Offre;

namespace Pharma.Application.Notifications;

/// <summary>
/// Lot O6 (offre de base et options) : alertes au titulaire selon les packs de l'offre. Alertes de sécurité (sauvegarde, synchronisation,
/// 2e serveur, réglages imposés) : toujours envoyées (décision du 2026-11-26). Alertes métier par SMS ou e-mail (écart de caisse, lot
/// bloqué, écarts d'inventaire, annulations) : pack E. Points du contrôle interne et ancien lot délaissé : pack A (enregistrés en silence
/// sans lui). Une alerte non envoyée pour cette raison n'est pas une erreur : rien n'est signalé.
/// </summary>
public sealed class AlertesTitulaireSelonOffre(IAlertesTitulaire alertes, IPacksOffre packs) : IAlertesTitulaire
{
    private async Task SiAsync(string pack, Func<Task> envoyer, CancellationToken ct)
    {
        if (await packs.InclusAsync(pack, ct)) await envoyer();
    }

    public Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default, bool releve = false) =>
        SiAsync(PacksOffre.Pilotage, () => alertes.EcartCaisseAsync(employe, poste, ecart, seuilCaisse, ct, releve), ct);

    public Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default) =>
        SiAsync(PacksOffre.Pilotage, () => alertes.LotBloqueAsync(produit, numeroLot, boitesEnStock, boitesVendues, ct), ct);

    public Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default) =>
        SiAsync(PacksOffre.Pilotage, () => alertes.EcartsInventaireAsync(rayon, lignes, boites, valeur, ct), ct);

    public Task AnnulationsAsync(string poste, int annulations, int seuil, CancellationToken ct = default) =>
        SiAsync(PacksOffre.Pilotage, () => alertes.AnnulationsAsync(poste, annulations, seuil, ct), ct);

    public Task LotDelaisseAsync(string message, bool urgent, CancellationToken ct = default) =>
        SiAsync(PacksOffre.ControleFraude, () => alertes.LotDelaisseAsync(message, urgent, ct), ct);

    public Task SurveillanceAsync(string message, bool urgent, CancellationToken ct = default) =>
        SiAsync(PacksOffre.ControleFraude, () => alertes.SurveillanceAsync(message, urgent, ct), ct);

    // Sécurité : toujours
    public Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default) => alertes.SauvegardeEnEchecAsync(disqueAbsent, detail, ct);
    public Task SansSauvegardeDisqueAsync(int jours, string detail, CancellationToken ct = default) => alertes.SansSauvegardeDisqueAsync(jours, detail, ct);
    public Task SynchronisationAsync(string objet, string detail, string sms, CancellationToken ct = default) => alertes.SynchronisationAsync(objet, detail, sms, ct);
    public Task ReglagesImposesAsync(IReadOnlyList<Reglages.ReglageImpose> tables, CancellationToken ct = default) => alertes.ReglagesImposesAsync(tables, ct);
    public Task SecondServeurAsync(string objet, string detail, string sms, CancellationToken ct = default) => alertes.SecondServeurAsync(objet, detail, sms, ct);
}
