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
        return this;
    }
}
