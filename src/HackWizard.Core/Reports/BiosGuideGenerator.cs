using System.Text;
using System.Text.RegularExpressions;
using HackWizard.Core.Models;
using HackWizard.Core.Rules;
using static HackWizard.Core.Reports.Html;

namespace HackWizard.Core.Reports;

public sealed class BiosGuide
{
    public string Title { get; init; } = "";
    public string SetupKey { get; init; } = "";
    public string BootMenuKey { get; init; } = "";
    public string SetupNotes { get; init; } = "";
    public bool IsModelSpecific { get; init; }
    public KbModelGuide? Model { get; init; }
    public List<KbBiosSetting> Settings { get; init; } = new();
}

/// <summary>Builds the BIOS guide: a model profile if one exists (e.g. ThinkPad T495, any ThinkPad), otherwise vendor keys +
/// generic settings. Every setting can carry a "when" condition (CPU vendor, laptop/desktop, capabilities).</summary>
public static class BiosGuideGenerator
{
    public static BiosGuide Build(KnowledgeBase kb, HardwareReport hw, CompatibilityResult? compat = null)
    {
        var s = hw.System;
        var ctx = new EvaluationContext();
        var caps = compat ?? new CompatibilityResult();
        bool Applies(KbBiosSetting x) => x.When is null || (RulesEngine.MatchSystem(x.When, hw, ctx, includeAcpi: true) && RulesEngine.MatchCaps(x.When, caps));

        var model = kb.Models.Models.FirstOrDefault(m =>
            Regex.IsMatch(s.Manufacturer, m.ManufacturerRegex, RegexOptions.IgnoreCase) &&
            (m.MachineTypes.Contains(s.MachineType, StringComparer.OrdinalIgnoreCase) ||
             (m.FamilyRegex != null && Regex.IsMatch($"{s.Version} {s.Family} {s.Model}", m.FamilyRegex, RegexOptions.IgnoreCase))));
        if (model != null)
        {
            var settings = model.Settings.Where(Applies).ToList();
            if (model.IncludeGeneric)
                settings.AddRange(kb.Models.GenericSettings.Where(Applies).Where(g => settings.All(x => x.Id != g.Id)));
            MoveSaveLast(settings);
            return new BiosGuide
            {
                Title = $"Firmware setup for {model.DisplayName} — {s.Version} {s.Model} (BIOS {s.BiosVersion}, {s.BiosDate})".Replace("  ", " "),
                SetupKey = model.SetupKey, BootMenuKey = model.BootMenuKey, SetupNotes = model.SetupNotes,
                IsModelSpecific = model.MachineTypes.Count > 0, Model = model, Settings = settings
            };
        }

        var vendorKey = kb.Models.VendorDefaults.Keys.Where(k => k != "DEFAULT").FirstOrDefault(k => s.Manufacturer.StartsWith(k, StringComparison.OrdinalIgnoreCase)) ?? "DEFAULT";
        var v = kb.Models.VendorDefaults.TryGetValue(vendorKey, out var vk) ? vk : new KbVendorKeys();
        var generic = kb.Models.GenericSettings.Where(Applies).ToList();
        MoveSaveLast(generic);
        return new BiosGuide
        {
            Title = $"Firmware setup for {s.Manufacturer} {s.Version} {s.Model}".Trim(),
            SetupKey = v.SetupKey, BootMenuKey = v.BootMenuKey, SetupNotes = v.Notes,
            IsModelSpecific = false, Settings = generic
        };
    }

    private static void MoveSaveLast(List<KbBiosSetting> l)
    {
        var save = l.FirstOrDefault(x => x.Id == "save");
        if (save != null) { l.Remove(save); l.Add(save); }
    }

    public static string ToHtml(BiosGuide g)
    {
        var b = new StringBuilder();
        if (!g.IsModelSpecific) b.Append("<div class=\"box warn\">Menu names differ between manufacturers and BIOS versions. The names below are the usual ones — look for the closest match. Use the search function (F3 / Ctrl+F) if your BIOS has one.</div>");
        b.Append("<div class=\"box\"><b>Enter setup:</b> power on and tap <kbd>").Append(E(g.SetupKey)).Append("</kbd>. <b>Boot menu:</b> <kbd>")
         .Append(E(g.BootMenuKey)).Append("</kbd>.<br>").Append(E(g.SetupNotes))
         .Append("<br>Tip: HackWizard's final screen has a <i>Restart into BIOS</i> button (<code>shutdown /r /fw</code>) that works on any UEFI PC.</div>");
        b.Append("<div class=\"box warn\">If a <b>supervisor/administrator password</b> is set you need it to change these settings.</div>");
        var n = 1;
        foreach (var s in g.Settings)
        {
            b.Append($"<h3>{n++}. {E(s.Title)} {(s.Required ? "(required)" : "(recommended)")}</h3>")
             .Append($"<p><b>Where:</b> {E(s.MenuPath)}<br><b>Set to:</b> <code>{E(s.Value)}</code><br><b>Why:</b> {E(s.Why)}</p>")
             .Append($"<div class=\"shot\">[Screenshot placeholder: {E(s.MenuPath)}]</div>");
            if (!string.IsNullOrEmpty(s.IfMissing)) b.Append($"<p><i>What if I don't see this option?</i> {E(s.IfMissing)}</p>");
        }
        return Page(g.Title, b.ToString());
    }
}
