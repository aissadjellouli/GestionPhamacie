using System.Windows;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.Web.WebView2.Core;

namespace Pharma.Desktop;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>
    /// La caméra sert à photographier l'ordonnance en caisse : seule cette permission est accordée,
    /// et seulement pour la page interne du poste.
    /// </summary>
    private void SurWebViewInitialisee(object? sender, BlazorWebViewInitializedEventArgs e) =>
        e.WebView.CoreWebView2.PermissionRequested += (_, demande) =>
        {
            if (demande.PermissionKind == CoreWebView2PermissionKind.Camera
                && Uri.TryCreate(demande.Uri, UriKind.Absolute, out var origine) && origine.Host == "0.0.0.1")
                demande.State = CoreWebView2PermissionState.Allow;
        };
}
