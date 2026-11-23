namespace Pharma.Shared;

// ---- 2e serveur (2026-11-16, script 122) ----

public sealed record ServeurOfficineDto(
    string Nom, string Role, string? DossierChifa, string? DossierMdn,
    bool ChifaInstalle, bool LecteurToken, bool MdnInstalle, bool DouchetteTestee,
    string? VersionApi, bool? ClePresente, string? EmpreinteCle, string? Imprimantes,
    bool? DossierChifaJoignable, bool? DossierMdnJoignable, DateTime? ReleveLe);

public sealed record SecondServeurDto(
    string CeServeur, string? VersionApi, string? EmpreinteCle,
    bool EnRecovery, bool Connecte, string? Etat, string? Client, long? RetardOctets, double? RetardSecondes,
    bool SlotExiste, bool SlotActif, bool DetailsVisibles,
    int SeuilSecondes, int SeuilMo, DateTime? DerniereAlerte, string? Probleme, int GedCopieMinutes,
    IReadOnlyList<ServeurOfficineDto> Serveurs);

public sealed record ModifierServeurOfficineDto(string Nom, string? DossierChifa, string? DossierMdn,
    bool ChifaInstalle, bool LecteurToken, bool MdnInstalle, bool DouchetteTestee);

public sealed record SeuilsReplicationDto(int SeuilSecondes, int SeuilMo);

/// <summary>Copie de la GED vers le 2e serveur : toutes les N minutes (1 à 60, 5 par défaut ; décision du titulaire du 2026-11-17).</summary>
public sealed record CopieGedDto(int Minutes);

/// <summary>MotDePasse : chiffre le fichier exporté ; MotDePasseTitulaire : mot de passe de connexion du titulaire, ressaisi.</summary>
public sealed record ExportCleDto(string MotDePasse, string Confirmation, string? MotDePasseTitulaire = null);

/// <summary>Fichier d'export de la clé de scellement (chiffré par le mot de passe du titulaire), en base 64.</summary>
public sealed record FichierCleDto(string NomFichier, string ContenuBase64, string Empreinte);
