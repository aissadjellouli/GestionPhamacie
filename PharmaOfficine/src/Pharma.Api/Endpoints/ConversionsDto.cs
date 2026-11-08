using Pharma.Application.Alertes;
using Pharma.Application.Catalogue;
using Pharma.Application.Securite;
using Pharma.Application.Stock;
using Pharma.Application.Tracabilite;
using Pharma.Domain.Commun;
using Pharma.Domain.Stock;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Modèles de l'Application vers le contrat HTTP partagé avec les postes (Pharma.Shared).</summary>
internal static class ConversionsDto
{
    public static ConnexionReponse VersDto(this Session s) => new(s.Jeton, s.NomComplet, s.ExpireLe);

    public static ProduitDto VersDto(this ProduitResume p) =>
        new(p.Id, p.NomCommercial, p.Dci, p.Dosage, p.Forme, p.PrixPublic, p.Stock, p.Classe.Code());

    public static LotEcarteDto VersDto(this LotEcarte l) =>
        new(l.NumeroLot, l.DatePeremption, l.Raison == RaisonNonVendable.Bloque ? "bloque" : "perime", l.MotifBlocage);

    public static LotFefoDto VersDto(this LotAServir l, IEnumerable<LotEcarte> ecartes) =>
        new(l.LotId, l.NumeroLot, l.DatePeremption, l.Quantite, l.Fournisseur, ecartes.Select(VersDto).ToList());

    public static ProduitAlternatifDto VersDto(this ProduitAlternatif a) =>
        new(a.ProduitId, a.NomCommercial, a.PrixPublic, a.Stock, a.Exact, a.Dosage, a.Forme);

    public static AlerteDto VersDto(this Alerte a) =>
        new(a.Id, a.Type, a.Niveau, a.ProduitId, a.LotId, a.Message, a.ValeurDzd, a.Statut, a.MiseAJourLe,
            a.Produit, a.NumeroLot, a.Emplacement);

    public static EtatStockDto VersDto(this EtatStock e) =>
        new(new IndicateursStockDto(e.Indicateurs.ReferencesEnStock, e.Indicateurs.ValeurAchat, e.Indicateurs.LotsDateAVerifier),
            e.Lignes.Select(l => new LigneStockDto(
                l.ProduitId, l.NomCommercial, l.Dci, l.CodeBarre, l.Classe.Code(), l.Stock, l.CouvertureJours,
                l.PremierePeremption, l.NombreLots, l.Grossistes, l.NumerosLots, l.Statut, l.PeremptionProche,
                l.EmplacementId, l.Emplacement, l.ReserveId, l.Reserve)).ToList());

    public static EtatEtiquetageDto VersDto(this EtatEtiquetage e) =>
        new(e.BoitesIdentifiees, e.BoitesEnStock, e.Pourcentage, e.ModeStrict);

    public static RappelLotDto VersDto(this ResultatRappel r) =>
        new(r.Produit, r.NumeroLot, r.LotsBloques, r.BoitesEnStock, r.BoitesVendues, r.PatientsAContacter, r.VentesAnonymes,
            r.Ventes.Select(v => new VenteARappelerDto(v.Patient, v.Telephone, v.DateVente, v.Ticket, v.Boites, v.Anonyme)).ToList(), r.Emplacements);

    public static ParcoursBoiteDto VersDto(this ParcoursBoite p) =>
        new(p.BoiteId, p.Identifiant, p.Produit, p.NumeroLot, p.DatePeremption,
            p.Fournisseur, p.NumeroBl, p.DateReception, p.Statut, p.LotBloque,
            p.Ticket, p.DateVente, p.NumFactureChifa, p.Patient,
            p.Etapes.Select(e => new EtapeParcoursDto(e.Type, e.Date, e.Commentaire)).ToList(), p.ProduitId);
}
