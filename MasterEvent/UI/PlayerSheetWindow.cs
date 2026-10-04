using System.Linq;
using Dalamud.Interface.Textures.TextureWraps;
using System.Threading.Tasks;
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using MasterEvent.Localization;
using MasterEvent.Models;
using MasterEvent.Services;
using MasterEvent.UI.Components;

namespace MasterEvent.UI;

public sealed class PlayerSheetWindow : MasterEventWindowBase
{
    private readonly SessionManager session;
    private readonly UmbraProfileIpc umbraProfiles;
    private readonly UmbraPortraitCache portraits;
    private readonly string playerHash;
    private readonly uint objectId;

    public PlayerSheetWindow(SessionManager session, UmbraProfileIpc umbraProfiles,
        UmbraPortraitCache portraits, string playerHash, string playerName, uint objectId)
        : base($"{playerName}###MasterEventSheet_{playerHash}", ImGuiWindowFlags.NoCollapse)
    {
        this.session = session;
        this.umbraProfiles = umbraProfiles;
        this.portraits = portraits;
        this.playerHash = playerHash;
        this.objectId = objectId;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 240),
            MaximumSize = new Vector2(700, 1200),
        };
    }

    protected override void DrawContents()
    {
        var player = session.PartyMembers.FirstOrDefault(p => p.Hash == playerHash);
        if (player == null)
        {
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Sheet.PlayerGone"));
            return;
        }

        LayoutControls.DrawTabHeader(FontAwesomeIcon.Scroll, player.Name,
            player.IsGm ? Loc.Get("Group.Gm") : player.GroupLabel);

        DrawUmbraIdentity();

        LayoutControls.BeginCard(Loc.Get("Npc.Vitality"), FontAwesomeIcon.Heart);

        HpBar.Draw(player.Hp, Attitude.Friendly, LayoutControls.CardContentWidth,
            session.HpMode, player.HpMax, shield: session.ShowShield ? player.Shield : 0);

        if (session.ShowMpBar)
            HpBar.DrawMpBar(player.Mp, LayoutControls.CardContentWidth, session.MpMode, player.MpMax);

        if (player.Counters != null)
        {
            foreach (var counter in player.Counters)
                CounterBar.Draw(counter, LayoutControls.CardContentWidth);
        }

        if (player.TempModifier != 0)
        {
            var sign = player.TempModifier > 0 ? "+" : string.Empty;
            var color = player.TempModifier > 0 ? MasterEventTheme.SuccessColor : MasterEventTheme.DangerColor;
            ImGui.TextColored(MasterEventTheme.TextSecondary, Loc.Get("Marker.TempMod"));
            ImGui.SameLine();
            ImGui.TextColored(color, $"{sign}{player.TempModifier}");
            if (player.TempModTurns > 0)
            {
                ImGui.SameLine(0, 2f * ImGuiHelpers.GlobalScale);
                ImGui.TextColored(MasterEventTheme.TextSecondary, $"({player.TempModTurns}t)");
            }
        }

        LayoutControls.EndCard();

        LayoutControls.BeginCard(Loc.Get("Models.Stats"), FontAwesomeIcon.ChartBar);

        if (player.Stats is not { Count: > 0 })
        {
            ImGui.TextColored(MasterEventTheme.TextDim, Loc.Get("Sheet.NoStats"));
        }
        else
        {
            foreach (var stat in player.Stats)
            {
                ImGui.TextUnformatted(stat.Name);
                ImGui.SameLine();
                var value = stat.Modifier >= 0 ? $"+{stat.Modifier}" : stat.Modifier.ToString();
                var width = LayoutControls.CardContentWidth - ImGui.CalcTextSize(value).X;
                if (width > ImGui.GetCursorPosX()) ImGui.SameLine(width);
                ImGui.TextColored(MasterEventTheme.TextStrong, value);
            }
        }

        LayoutControls.EndCard();
    }

    private void DrawUmbraIdentity()
    {
        var profile = umbraProfiles.GetProfile(objectId);
        if (profile == null) return;

        LayoutControls.BeginCard(Loc.Get("Sheet.Identity"), FontAwesomeIcon.IdCard);

        DrawPortrait();

        var fullName = string.Join(' ', new[] { profile.FirstName, profile.LastName }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        if (fullName.Length > 0)
            ImGui.TextColored(MasterEventTheme.TextStrong, fullName);

        if (!string.IsNullOrWhiteSpace(profile.Title))
            ImGui.TextColored(MasterEventTheme.TextSecondary, profile.Title);

        if (profile.IsNsfw)
            ImGui.TextColored(MasterEventTheme.WarningColor, Loc.Get("Sheet.Nsfw"));

        ImGuiHelpers.ScaledDummy(2f);

        DrawField(Loc.Get("Sheet.Race"), profile.Race);
        DrawField(Loc.Get("Sheet.Ethnicity"), profile.Ethnicity);
        DrawField(Loc.Get("Sheet.Age"), profile.Age);
        DrawField(Loc.Get("Sheet.Height"), profile.Height);
        DrawField(Loc.Get("Sheet.Build"), profile.Build);
        DrawField(Loc.Get("Sheet.Residence"), profile.Residence);
        DrawField(Loc.Get("Sheet.Occupation"), profile.Occupation);
        DrawField(Loc.Get("Sheet.Affiliation"), profile.Affiliation);
        DrawField(Loc.Get("Sheet.Alignment"), profile.Alignment);

        foreach (var field in profile.CustomFields ?? [])
            DrawField(field.Label, field.Value);

        if (!string.IsNullOrWhiteSpace(profile.AdditionalInfo))
        {
            ImGuiHelpers.ScaledDummy(2f);
            ImGui.PushStyleColor(ImGuiCol.Text, MasterEventTheme.TextSecondary);
            ImGui.TextWrapped(profile.AdditionalInfo);
            ImGui.PopStyleColor();
        }

        LayoutControls.EndCard();
    }

    private void DrawPortrait()
    {
        var texture = portraits.Get(objectId);
        if (texture == null) return;

        const float maxHeight = 160f;
        var scale = Math.Min(1f, maxHeight * ImGuiHelpers.GlobalScale / texture.Height);
        ImGui.Image(texture.Handle, new Vector2(texture.Width * scale, texture.Height * scale));
        ImGuiHelpers.ScaledDummy(2f);
    }

    private static void DrawField(string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        ImGui.TextColored(MasterEventTheme.TextDim, label);
        ImGui.SameLine();
        ImGui.TextUnformatted(value);
    }

}
