namespace Pharma.Shared;

// ---- Journal fiscal des ventes (lot 3) : écran Administration › Contrôle interne › Journal fiscal (titulaire) ----

public sealed record LigneJournalFiscalDto(long Rang, string Evenement, string? NumeroTicket, DateTime SurvenuLe, string? Poste, string? Vendeur,
    decimal Ttc, decimal Ht, decimal Tva, decimal PartPatient, decimal PartOrganisme, string Paiements, decimal MontantMouvement, string? Motif,
    string Empreinte);

public sealed record LigneRecapTvaDto(decimal Taux, bool Exoneree, decimal Ttc, decimal Ht, decimal Tva, long Evenements);

public sealed record ClotureFiscaleDto(long SceauId, string Type, long? SessionCaisseId, DateOnly Du, DateOnly Au, DateTime ScelleLe,
    long DernierRang, string Sceau, string EmpreinteCle, decimal Ttc, string? FichierArchive);

public sealed record JournalFiscalDto(PageDto<LigneJournalFiscalDto> Lignes, IReadOnlyList<LigneRecapTvaDto> Tva, long Evenements, decimal Ttc,
    decimal Ht, decimal MontantTva, IReadOnlyList<ClotureFiscaleDto> Clotures, IReadOnlyList<string> Anomalies, IReadOnlyList<string> TrousHistorique,
    string DernierTicket, string PremierTicketContinu, DateTime MisEnServiceLe, int ConservationAnnees, string FormatExport,
    string? EtatChaine, DateTime? VerifieeLe, string? MotifChaine);
