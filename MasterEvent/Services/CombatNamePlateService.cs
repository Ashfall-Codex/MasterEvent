using System;
using System.Collections.Generic;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Plugin.Services;

namespace MasterEvent.Services;

public sealed class CombatNamePlateService : IDisposable
{
    private readonly Configuration configuration;
    private readonly SessionManager session;
    private readonly INamePlateGui namePlateGui;

    private bool hiding;

    public CombatNamePlateService(Configuration configuration, SessionManager session, INamePlateGui namePlateGui)
    {
        this.configuration = configuration;
        this.session = session;
        this.namePlateGui = namePlateGui;
        namePlateGui.OnDataUpdate += OnDataUpdate;
    }

    private bool ShouldHide()
        => configuration.HideNameplatesInCombat && session.CurrentTurnState is { IsActive: true };
    
    public void Tick()
    {
        var shouldHide = ShouldHide();
        if (shouldHide == hiding) return;
        hiding = shouldHide;
        namePlateGui.RequestRedraw();
    }

    private void OnDataUpdate(INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        if (!ShouldHide()) return;
        // VisibilityFlags = 0 → nameplate masqué. Hors masquage, on ne touche à
        // rien : la visibilité par défaut du jeu s'applique.
        foreach (var handler in handlers)
            handler.VisibilityFlags = 0;
    }

    public void Dispose()
    {
        namePlateGui.OnDataUpdate -= OnDataUpdate;
        if (hiding)
        {
            hiding = false;
            namePlateGui.RequestRedraw();
        }
    }
}
