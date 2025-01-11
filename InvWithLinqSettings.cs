using System.Collections.Generic;
using System.Text.Json.Serialization;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using System.Drawing;
using ImGuiNET;
using System.Diagnostics;
using System.IO;
using System.Numerics;

namespace InvWithLinq;

public class InvWithLinqSettings : ISettings
{
    private bool _reloadRequired = false;

    public InvWithLinqSettings()
    {
        RuleConfig = new RuleRenderer(this);
    }

    public ToggleNode RunOutsideTown { get; set; } = new ToggleNode(true);
    public ToggleNode EnableForStash { get; set; } = new ToggleNode(true);
    public ToggleNode Enable { get; set; } = new ToggleNode(false);
    public ColorNode DefaultFrameColor { get; set; } = new ColorNode(Color.Red);
    public RangeNode<int> FrameThickness { get; set; } = new RangeNode<int>(1, 1, 20);

    [JsonIgnore]
    public TextNode FilterTest { get; set; } = new TextNode();
    
    [JsonIgnore]
    public ButtonNode ReloadFilters { get; set; } = new ButtonNode();

    [JsonIgnore]
    public ButtonNode DumpItems { get; set; } = new ButtonNode();

    [Menu("Use a Custom \"\\config\\custom_folder\" Folder")]
    public TextNode CustomConfigDirectory { get; set; } = new TextNode();
    
    public List<InvRule> InvRules { get; set; } = new List<InvRule>();

    [JsonIgnore]
    public RuleRenderer RuleConfig { get; set; }

    [Submenu(RenderMethod = nameof(Render))]
    public class RuleRenderer
    {
        private readonly InvWithLinqSettings _parent;

        public RuleRenderer(InvWithLinqSettings parent)
        {
            _parent = parent;
        }

        public void Render(InvWithLinq plugin)
        {
            if (ImGui.Button("Open rule folder"))
            {
                var configDir = plugin.ConfigDirectory;
                var customConfigFileDirectory = !string.IsNullOrEmpty(_parent.CustomConfigDirectory)
                    ? Path.Combine(Path.GetDirectoryName(plugin.ConfigDirectory), _parent.CustomConfigDirectory)
                    : null;

                var directoryToOpen = Directory.Exists(customConfigFileDirectory)
                    ? customConfigFileDirectory
                    : configDir;

                Process.Start("explorer.exe", directoryToOpen);
            }

            ImGui.Separator();
            ImGui.BulletText("Select Rules To Load");
            ImGui.BulletText("Ordering rule sets so general items will match first rather than last will improve performance");

            var tempNpcInvRules = new List<InvRule>(_parent.InvRules); // Create a copy

            for (int i = 0; i < tempNpcInvRules.Count; i++)
            {
                if (ImGui.ArrowButton($"##upButton{i}", ImGuiDir.Up) && i > 0)
                {
                    (tempNpcInvRules[i - 1], tempNpcInvRules[i]) = (tempNpcInvRules[i], tempNpcInvRules[i - 1]);
                    _parent._reloadRequired = true;
                }

                ImGui.SameLine();
                ImGui.Text(" ");
                ImGui.SameLine();

                if (ImGui.ArrowButton($"##downButton{i}", ImGuiDir.Down) && i < tempNpcInvRules.Count - 1)
                {
                    (tempNpcInvRules[i + 1], tempNpcInvRules[i]) = (tempNpcInvRules[i], tempNpcInvRules[i + 1]);
                    _parent._reloadRequired = true;
                }

                ImGui.SameLine();
                ImGui.Text(" - ");
                ImGui.SameLine();

                var refToggle = tempNpcInvRules[i].Enabled;
                if (ImGui.Checkbox($"{tempNpcInvRules[i].Name}##checkbox{i}", ref refToggle))
                {
                    tempNpcInvRules[i].Enabled = refToggle;
                    plugin.ReloadRules();
                }

                ImGui.SameLine();
                var color = new Vector4(tempNpcInvRules[i].Color.Value.R / 255.0f, tempNpcInvRules[i].Color.Value.G / 255.0f, tempNpcInvRules[i].Color.Value.B / 255.0f, tempNpcInvRules[i].Color.Value.A / 255.0f);
                if (ImGui.ColorEdit4($"##colorPicker{i}", ref color))
                    tempNpcInvRules[i].Color.Value = Color.FromArgb((int)(color.W * 255), (int)(color.X * 255), (int)(color.Y * 255), (int)(color.Z * 255));
            }

            _parent.InvRules = tempNpcInvRules;

            if (_parent._reloadRequired)
            {
                plugin.ReloadRules();
                _parent._reloadRequired = false;
            }
        }
    }
}

public class InvRule
{
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public bool Enabled { get; set; } = false;
    public ColorNode Color { get; set; } = new ColorNode(System.Drawing.Color.Red);

    public InvRule(string name, string location, bool enabled)
    {
        Name = name;
        Location = location;
        Enabled = enabled;
    }
}