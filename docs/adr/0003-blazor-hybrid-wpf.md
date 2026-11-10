# ADR 0003 – Postes en Blazor Hybrid dans WPF

**Statut** : accepté

**Contexte** : besoin d’un accès direct au matériel (imprimantes, tiroir, lecteurs), d’une interface moderne trilingue et d’une porte ouverte vers le web et le mobile.

**Décision** : interface écrite en composants Blazor, affichée dans une fenêtre WPF via `BlazorWebView` (WebView2).

**Conséquences** : composants réutilisables plus tard dans un portail ou une application ; postes Windows uniquement pour l’instant ; WebView2 requis sur chaque poste.
