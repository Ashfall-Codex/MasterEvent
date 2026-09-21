using System;
using System.Numerics;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.UI;

namespace MasterEvent.Services;

public sealed class PartyContextMenu : IDisposable
{
    private readonly IContextMenu contextMenu;
    private readonly SessionManager session;
    private readonly UmbraProfileIpc umbraProfiles;
    private readonly Action<PlayerData, uint> onShowSheet;
    private readonly Action<PlayerData> onRequestRoll;

    public PartyContextMenu(IContextMenu contextMenu, SessionManager session,
        UmbraProfileIpc umbraProfiles, Action<PlayerData, uint> onShowSheet, Action<PlayerData> onRequestRoll)
    {
        this.contextMenu = contextMenu;
        this.session = session;
        this.umbraProfiles = umbraProfiles;
        this.onShowSheet = onShowSheet;
        this.onRequestRoll = onRequestRoll;

        this.contextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose() => contextMenu.OnMenuOpened -= OnMenuOpened;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetDefault target) return;
        if (ResolveMember(target) is not { } member) return;

        var objectId = (uint)target.TargetObjectId;
        var hasUmbraProfile = umbraProfiles.GetProfile(objectId) != null;

        args.AddMenuItem(new MenuItem
        {
            PrefixChar = 'M',
            PrefixColor = AccentColorKey(),
            UseDefaultPrefix = false,
            Name = Loc.Get(hasUmbraProfile ? "ContextMenu.ShowSheetSynced" : "ContextMenu.ShowSheet"),
            OnClicked = _ => onShowSheet(member, objectId),
        });
        if (!session.CanEdit || !session.IsConnected) return;

        args.AddMenuItem(new MenuItem
        {
            PrefixChar = 'M',
            PrefixColor = AccentColorKey(),
            UseDefaultPrefix = false,
            Name = Loc.Get("ContextMenu.RequestRoll"),
            OnClicked = _ => onRequestRoll(member),
        });
    }

    private static ushort? cachedAccentColor;

    private const ushort FallbackAccentColor = 17;

    private static ushort AccentColorKey()
        => cachedAccentColor ??= ClosestUiColor(MasterEventTheme.AccentColor, FallbackAccentColor);

    private static ushort ClosestUiColor(Vector4 wanted, ushort fallback)
    {
        var best = fallback;
        var bestDistance = float.MaxValue;

        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.UIColor>();
            foreach (var row in sheet)
            {
                var packed = row.Dark;
                var r = ((packed >> 24) & 0xFF) / 255f;
                var g = ((packed >> 16) & 0xFF) / 255f;
                var b = ((packed >> 8) & 0xFF) / 255f;

                if (r + g + b < 0.9f) continue;

                var distance = (r - wanted.X) * (r - wanted.X)
                             + (g - wanted.Y) * (g - wanted.Y)
                             + (b - wanted.Z) * (b - wanted.Z);
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = (ushort)row.RowId;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[MasterEvent] Couleur de menu introuvable : {ex.Message}");
        }

        return best;
    }

    private PlayerData? ResolveMember(MenuTargetDefault target)
    {
        if (target.TargetContentId != 0)
        {
            var hash = Plugin.GeneratePlayerHash(target.TargetContentId);
            var byHash = session.PartyMembers.Find(p => p.Hash == hash);
            if (byHash != null) return Eligible(byHash);
        }

        if (string.IsNullOrEmpty(target.TargetName)) return null;

        var byName = session.PartyMembers.Find(
            p => string.Equals(p.Name, target.TargetName, StringComparison.Ordinal));
        return byName == null ? null : Eligible(byName);
    }

    private PlayerData? Eligible(PlayerData member)
        => member.Hash == session.LocalPlayerHash ? null : member;
}
