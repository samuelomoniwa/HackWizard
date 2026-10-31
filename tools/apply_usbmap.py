#!/usr/bin/env python3
"""Adds the built-in USB port mapping step to an existing HackWizard checkout.
Run from the repository root:  python tools/apply_usbmap.py
Idempotent: running it twice is safe. Nothing is written unless every edit can be applied."""
import json, pathlib, sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
edits = {}   # path -> (text, crlf)

def load(path):
    if path not in edits:
        raw = (ROOT / path).read_bytes().decode("utf-8-sig")
        edits[path] = [raw.replace("\r\n", "\n"), "\r\n" in raw]
    return edits[path]

def patch(path, old, new, marker=None, optional=False):
    e = load(path)
    if (marker or new) in e[0]:
        print("  already done:", path); return
    if old not in e[0]:
        if optional:
            print("  skipped (optional, text not found):", path); return
        sys.exit(f"\nANCHOR NOT FOUND in {path}:\n---\n{old}\n---\nNothing was changed. Send this message to Copilot.")
    e[0] = e[0].replace(old, new, 1)
    print("  patched:", path)

APP = "src/HackWizard.App/"
for f in ["src/HackWizard.Core/Usb/UsbMap.cs", "src/HackWizard.Core/Usb/UsbScanner.cs",
          APP + "ViewModels/UsbMapViewModel.cs", "tests/HackWizard.Tests/UsbMapTests.cs"]:
    if not (ROOT / f).exists(): sys.exit(f"Missing {f}: extract the whole patch zip into the repository root first.")

# --- Step plumbing -------------------------------------------------------------------------------
patch(APP + "ViewModels/StepViewModel.cs",
      "    public virtual Task OnEnterAsync() => Task.CompletedTask;",
      "    public virtual Task OnEnterAsync() => Task.CompletedTask;\n    public virtual void OnLeave() { }")
patch(APP + "ViewModels/MainViewModel.cs",
      "            if (!SetProperty(ref _index, value)) return;",
      "            if (_index != value) Steps[_index].OnLeave();\n            if (!SetProperty(ref _index, value)) return;")
patch(APP + "ViewModels/MainViewModel.cs", "new AcpiViewModel(ctx),", "new AcpiViewModel(ctx), new UsbMapViewModel(ctx),",
      marker="new UsbMapViewModel(ctx)")
patch(APP + "Services/WizardContext.cs",
      "    public IReadOnlyList<GeneratedSsdt> Ssdts { get; set; } = Array.Empty<GeneratedSsdt>();",
      "    public IReadOnlyList<GeneratedSsdt> Ssdts { get; set; } = Array.Empty<GeneratedSsdt>();\n"
      "    /// <summary>USB map from step 5; null or empty selection = no map (UTBDefault.kext is used).</summary>\n"
      "    public HackWizard.Core.Usb.UsbMapState? UsbMap { get; set; }")

# --- Renumber later steps ------------------------------------------------------------------------
for f, old, new in [
    ("ViewModels/FinalViewModel.cs", '"9. Install macOS"', '"10. Install macOS"'),
    ("ViewModels/BiosViewModel.cs", '"8. Firmware (BIOS/UEFI) settings"', '"9. Firmware (BIOS/UEFI) settings"'),
    ("ViewModels/UsbViewModel.cs", '"7. Write the USB drive"', '"8. Write the USB drive"'),
    ("ViewModels/EfiViewModel.cs", '"6. Build the OpenCore EFI"', '"7. Build the OpenCore EFI"'),
    ("ViewModels/DownloadsViewModel.cs", '"5. Downloads"', '"6. Downloads"')]:
    patch(APP + f, old, new)
patch(APP + "ViewModels/FinalViewModel.cs", "apply the settings from step 8", "apply the settings from step 9", optional=True)

# --- UI template ---------------------------------------------------------------------------------
patch(APP + "Views/StepTemplates.xaml",
      'xmlns:conv="clr-namespace:HackWizard.App.Converters">',
      'xmlns:conv="clr-namespace:HackWizard.App.Converters"\n'
      '                    xmlns:usb="clr-namespace:HackWizard.Core.Usb;assembly=HackWizard.Core">',
      marker='xmlns:usb=')
patch(APP + "Views/StepTemplates.xaml",
      '    <DataTemplate DataType="{x:Type vm:DownloadsViewModel}">',
      '''    <DataTemplate DataType="{x:Type vm:UsbMapViewModel}">
        <StackPanel>
            <Border Style="{StaticResource Card}"><TextBlock Text="{Binding Instructions}"/></Border>
            <StackPanel Orientation="Horizontal" Margin="0,0,0,10">
                <Button Content="{Binding DiscoverButtonText}" Command="{Binding ToggleDiscoveryCommand}" Style="{StaticResource PrimaryButton}"/>
                <Button Content="_Auto-select used ports" Command="{Binding AutoSelectCommand}"/>
                <Button Content="Select _none (skip mapping)" Command="{Binding ClearCommand}"/>
                <Button Content="_Forget and rescan" Command="{Binding ResetCommand}"/>
            </StackPanel>
            <TextBlock Text="{Binding Summary}" FontWeight="SemiBold" Margin="0,0,0,8"/>
            <ItemsControl ItemsSource="{Binding Controllers}" Focusable="False">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Border Style="{StaticResource Card}">
                            <StackPanel>
                                <TextBlock Text="{Binding Header}" FontWeight="Bold"/>
                                <TextBlock Text="{Binding CountText}" Foreground="{DynamicResource Brush.Muted}" Margin="0,2,0,6"/>
                                <DataGrid ItemsSource="{Binding Ports}" IsReadOnly="False" CanUserAddRows="False" CanUserDeleteRows="False" MaxHeight="460">
                                    <DataGrid.Columns>
                                        <DataGridTemplateColumn Header="Use">
                                            <DataGridTemplateColumn.CellTemplate>
                                                <DataTemplate><CheckBox IsChecked="{Binding Selected, UpdateSourceTrigger=PropertyChanged}" HorizontalAlignment="Center"/></DataTemplate>
                                            </DataGridTemplateColumn.CellTemplate>
                                        </DataGridTemplateColumn>
                                        <DataGridTextColumn Header="Port" Binding="{Binding Name}" IsReadOnly="True"/>
                                        <DataGridTextColumn Header="#" Binding="{Binding Number}" IsReadOnly="True"/>
                                        <DataGridTextColumn Header="Type" Binding="{Binding ProtocolText}" IsReadOnly="True"/>
                                        <DataGridTemplateColumn Header="Connector">
                                            <DataGridTemplateColumn.CellTemplate>
                                                <DataTemplate>
                                                    <ComboBox MinWidth="200" ItemsSource="{x:Static usb:UsbConnectors.All}" SelectedValuePath="Value" DisplayMemberPath="Label"
                                                              SelectedValue="{Binding Connector, UpdateSourceTrigger=PropertyChanged}"/>
                                                </DataTemplate>
                                            </DataGridTemplateColumn.CellTemplate>
                                        </DataGridTemplateColumn>
                                        <DataGridTextColumn Header="Pair" Binding="{Binding CompanionText}" IsReadOnly="True"/>
                                        <DataGridTextColumn Header="Now" Binding="{Binding LiveText}" IsReadOnly="True"/>
                                        <DataGridTextColumn Header="Last device seen" Binding="{Binding LastDevice}" IsReadOnly="True" Width="*"/>
                                    </DataGrid.Columns>
                                </DataGrid>
                            </StackPanel>
                        </Border>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </StackPanel>
    </DataTemplate>

    <DataTemplate DataType="{x:Type vm:DownloadsViewModel}">''',
      marker='{x:Type vm:UsbMapViewModel}')

# --- EFI pipeline: USBToolBox.kext always; UTBMap.kext from the map, else UTBDefault.kext ---------
EP = APP + "Services/EfiPipeline.cs"
patch(EP, "        var kexts = ResolveKexts(kb, c, macos);",
      "        var kexts = ResolveKexts(kb, c, macos);\n"
      "        var utbSrc = kb.Source(\"usbtoolbox-kext\");\n"
      "        foreach (var n in new[] { \"USBToolBox.kext\", \"UTBDefault.kext\" })\n"
      "            if (kexts.All(k => k.BundleName != n)) kexts.Add(new KextRequest(n, utbSrc));",
      marker='kb.Source("usbtoolbox-kext")')
patch(EP, "        foreach (var (req, art) in d.Kexts)\n        {",
      "        foreach (var (req, art) in d.Kexts)\n        {\n"
      "            if (req.BundleName == \"UTBDefault.kext\" && ctx.UsbMap is { SelectedCount: > 0 }) continue;",
      marker='req.BundleName == "UTBDefault.kext"')
patch(EP, "        var ordered = KextOrderer.Order(kextInfos);",
      "        if (ctx.UsbMap is { SelectedCount: > 0 } usbMap)\n"
      "        {\n"
      "            if (usbMap.OverLimit) throw new InvalidOperationException(\"USB map has more than 15 ports on a controller. Fix it in step 5.\");\n"
      "            var utbMap = Path.Combine(oc, \"Kexts\", \"UTBMap.kext\");\n"
      "            HackWizard.Core.Usb.UtbMapWriter.Write(usbMap, utbMap);\n"
      "            kextInfos.AddRange(KextInfo.ReadWithPlugins(utbMap, \"UTBMap.kext\"));\n"
      "            notes.Add($\"USB map: UTBMap.kext with {usbMap.SelectedCount} ports on {usbMap.Mapped.Count()} controller(s).\");\n"
      "        }\n"
      "        else notes.Add(\"No USB map: UTBDefault.kext enables all ports (up to 15 per controller). Map them later.\");\n"
      "        var ordered = KextOrderer.Order(kextInfos);",
      marker="UtbMapWriter.Write")

# --- Knowledge base: the USBToolBox zip now also provides kexts ----------------------------------
sp = ROOT / "Rules/kb/sources.json"
src = json.loads(sp.read_bytes().decode("utf-8-sig"))
entry = next((s for s in src["sources"] if s.get("id") == "usbtoolbox-kext"), None)
if entry is None: sys.exit("usbtoolbox-kext not found in Rules/kb/sources.json")
new_provides = ["USBToolBox.kext", "UTBDefault.kext"]
kb_changed = entry.get("kind") != "kext" or entry.get("provides") != new_provides
entry["kind"] = "kext"; entry["provides"] = new_provides

# --- Write everything ----------------------------------------------------------------------------
for path, (text, crlf) in edits.items():
    (ROOT / path).write_text(text.replace("\n", "\r\n") if crlf else text, encoding="utf-8", newline="")
if kb_changed:
    sp.write_text(json.dumps(src, indent=2) + "\n", encoding="utf-8", newline="\n")
    print("  patched: Rules/kb/sources.json")
print("ALL DONE")
