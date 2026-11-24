namespace Pharma.Application.SecondServeur;

/// <summary>
/// Réglages « SecondServeur:Bascule » (IOptions, bornés ; section absente = valeurs par défaut). Bornes et origine : contrat-api.md § 5.
/// <see cref="Borner"/> ramène chaque valeur dans sa borne (Q21 : 15 s à 5 min).
/// </summary>
public sealed class ReglagesBascule
{
    public const string Section = "SecondServeur:Bascule";
    private const int PlancherSecondes = 15, PlafondSecondes = 300;   // Q21 : 15 s à 5 min

    public int PertePosteSecondes { get; set; } = 30;
    public int InjoignableServeur1Secondes { get; set; } = 30;
    public int IsolementSecondes { get; set; } = 20;
    public int PromotionMinimumSecondes { get; set; } = 60;
    public int RetourVoieDirecteSecondes { get; set; } = 120;
    public int PosteActifMinutes { get; set; } = 3;
    public int PosteVuSecondes { get; set; } = 45;
    public int DemarrageVeilleSansDecisionSecondes { get; set; } = 120;
    public int SondeSecondes { get; set; } = 5;
    public int FenetreSignatureSecondes { get; set; } = 60;
    public bool PromotionAutomatique { get; set; } = true;

    /// <summary>Marge de sécurité (secondes) entre la fin de la règle de majorité du serveur 1 et la promotion possible du 2e serveur.</summary>
    public const int MargeSecondes = 5;

    /// <summary>
    /// Plus petite valeur sûre de <see cref="PromotionMinimumSecondes"/> : le serveur 1 doit s'être mis lui-même à l'écart (un poste n'est plus « vu » après
    /// PosteVuSecondes, puis IsolementSecondes de décompte, puis un passage de surveillance de SondeSecondes) AVANT que le 2e serveur puisse se promouvoir, plus une marge.
    /// Contrainte : PosteVuSecondes + IsolementSecondes + SondeSecondes + marge &lt; PromotionMinimumSecondes.
    /// </summary>
    public int PromotionMinimumSur => PosteVuSecondes + IsolementSecondes + SondeSecondes + MargeSecondes + 1;

    /// <summary>Vrai si <see cref="Borner"/> a dû relever PromotionMinimumSecondes (valeur demandée dans <see cref="PromotionMinimumDemandee"/>) : à journaliser.</summary>
    public bool PromotionMinimumRelevee { get; private set; }
    public int PromotionMinimumDemandee { get; private set; }

    /// <summary>Réglages effectifs qui gouvernent la règle de majorité, comparés entre les deux serveurs (GET /api/inter-serveurs/etat).</summary>
    public string Empreinte() =>
        $"vu={PosteVuSecondes};isolement={IsolementSecondes};sonde={SondeSecondes};promotionMin={PromotionMinimumSecondes};perte={PertePosteSecondes};injoignable={InjoignableServeur1Secondes};actif={PosteActifMinutes}";

    public ReglagesBascule Borner()
    {
        PertePosteSecondes = Math.Clamp(PertePosteSecondes, PlancherSecondes, PlafondSecondes);
        InjoignableServeur1Secondes = Math.Clamp(InjoignableServeur1Secondes, PlancherSecondes, PlafondSecondes);
        IsolementSecondes = Math.Clamp(IsolementSecondes, 10, 120);
        PromotionMinimumSecondes = Math.Clamp(PromotionMinimumSecondes, 60, 600);
        RetourVoieDirecteSecondes = Math.Clamp(RetourVoieDirecteSecondes, 30, 600);
        PosteActifMinutes = Math.Clamp(PosteActifMinutes, 1, 10);
        PosteVuSecondes = Math.Clamp(PosteVuSecondes, 30, 120);
        DemarrageVeilleSansDecisionSecondes = Math.Clamp(DemarrageVeilleSansDecisionSecondes, 60, 600);
        SondeSecondes = Math.Clamp(SondeSecondes, 2, 30);
        FenetreSignatureSecondes = Math.Clamp(FenetreSignatureSecondes, 30, 300);
        // Contrainte de sûreté : le serveur 1 s'est écarté avant toute promotion possible ; sinon la valeur est relevée au minimum sûr (et journalisée par l'appelant)
        if (PromotionMinimumSecondes < PromotionMinimumSur)
        {
            if (!PromotionMinimumRelevee) PromotionMinimumDemandee = PromotionMinimumSecondes;
            PromotionMinimumRelevee = true;
            PromotionMinimumSecondes = PromotionMinimumSur;
        }
        return this;
    }
}
