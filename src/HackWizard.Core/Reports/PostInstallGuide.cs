using System.Text;
using HackWizard.Core.Rules;
using static HackWizard.Core.Reports.Html;

namespace HackWizard.Core.Reports;

public static class PostInstallGuide
{
    public static string ToHtml(CompatibilityResult c, string macosName, string serial)
    {
        var b = new StringBuilder();
        b.Append("<ol>")
         .Append("<li>Boot macOS from the USB (OpenCore picker → your macOS disk) until the internal EFI is installed.</li>")
         .Append("<li>Open <code>Utilities/Copy-EFI-To-Internal.command</code> on the USB (right-click → Open). It mounts the internal disk's EFI partition, backs up anything there, copies <code>EFI</code> from the USB and asks whether to switch to the <b>release</b> config (no verbose text).</li>")
         .Append("<li>Reboot without the USB and confirm the OpenCore menu appears.</li>")
         .Append("<li>Map your USB ports with USBToolBox (<code>Utilities/USBToolBox</code>) and add the generated <code>UTBMap.kext</code> + <code>USBToolBox.kext</code>.</li>");
        foreach (var p in c.PostInstall) b.Append("<li>").Append(E(p)).Append("</li>");
        if (c.AudioLayoutCandidates.Count > 1)
            b.Append("<li>No sound? Try the other audio layouts for your codec by adding <code>alcid=N</code> to boot-args: ").Append(E(string.Join(", ", c.AudioLayoutCandidates))).Append(".</li>");
        b.Append("<li>Check that your serial <code>").Append(E(serial)).Append("</code> is <b>not valid</b> at <a href=\"https://checkcoverage.apple.com\">checkcoverage.apple.com</a>. If it is valid (belongs to a real Mac), generate a new one.</li>")
         .Append("<li>Keep the USB as a rescue drive.</li></ol>");
        var limits = c.Items.Where(i => i.Status is CompatStatus.Partial or CompatStatus.Unsupported && i.Device != "-").ToList();
        if (limits.Count > 0)
        {
            b.Append("<h2>Known limitations on this PC</h2><ul>");
            foreach (var i in limits) b.Append($"<li><b>{E(i.Component)}</b> ({E(i.Device)}): {E(i.Notes)}</li>");
            b.Append("</ul>");
        }
        return Page($"After installing {macosName}", b.ToString());
    }

    public const string CopyEfiScript = """
#!/bin/bash
# HackWizard: copy the OpenCore EFI from this USB to the internal disk's EFI System Partition.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
USB_ROOT="$(cd "$HERE/.." && pwd)"
echo "== HackWizard EFI installer =="
INTERNAL=$(diskutil list internal physical | awk '/^\/dev\/disk/{print $1; exit}')
if [ -z "$INTERNAL" ]; then echo "No internal disk found."; exit 1; fi
ESP=$(diskutil list "$INTERNAL" | awk '/EFI/{print $NF; exit}')
if [ -z "$ESP" ]; then echo "No EFI partition on $INTERNAL."; exit 1; fi
echo "Internal disk: $INTERNAL  EFI partition: $ESP"
read -r -p "Copy EFI to /dev/$ESP? Type YES to continue: " OK
[ "$OK" = "YES" ] || { echo "Cancelled."; exit 0; }
sudo diskutil mount "$ESP"
MP=$(diskutil info "$ESP" | awk -F': *' '/Mount Point/{print $2}')
if [ -d "$MP/EFI" ]; then
  BK="$HOME/Desktop/EFI-backup-$(date +%Y%m%d-%H%M%S)"
  echo "Backing up existing EFI to $BK"; cp -R "$MP/EFI" "$BK"; sudo rm -rf "$MP/EFI"
fi
sudo cp -R "$USB_ROOT/EFI" "$MP/EFI"
read -r -p "Switch to the release config (no verbose boot)? [y/N] " REL
if [ "$REL" = "y" ] || [ "$REL" = "Y" ]; then
  sudo cp "$MP/EFI/OC/config.plist" "$MP/EFI/OC/config-debug.plist"
  sudo cp "$MP/EFI/OC/config-release.plist" "$MP/EFI/OC/config.plist"
  echo "Release config active (debug copy kept as config-debug.plist)."
fi
sync; sudo diskutil unmount "$ESP" || true
echo "Done. Reboot without the USB to test."
""";
}
