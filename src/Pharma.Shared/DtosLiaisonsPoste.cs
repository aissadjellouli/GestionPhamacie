namespace Pharma.Shared;

/// <summary>Anomalie d'une liaison pour la barre d'état : libellé court, niveau (« attention » ou « anormal »), détail (infobulle).</summary>
public sealed record AnomalieLiaisonDto(string Libelle, string Niveau, string Detail);

/// <summary>
/// GET /api/synchronisation/etat-poste (tout employé connecté) : liaison avec le serveur central et avec le 2e serveur. Central et Second
/// sont null quand rien n'est à signaler (liaison absente ou à jour). Aucune donnée personnelle ni réglage technique (ni adresse, ni code
/// d'officine, ni empreinte, ni message d'erreur).
/// </summary>
public sealed record EtatLiaisonsPosteDto(bool CentralActive, AnomalieLiaisonDto? Central, long LotsEnAttente, DateTime? DernierEnvoiLe,
    bool SecondConfigure, AnomalieLiaisonDto? Second);
