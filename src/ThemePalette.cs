using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Biggofiles
{
    internal sealed class ThemePalette
    {
        // Shared mutable brushes update templates, drive cards and open dialogs
        // together without rebuilding the result list or touching scan data.
        private readonly Dictionary<string, SolidColorBrush> brushes = new Dictionary<string, SolidColorBrush>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PaletteColor> colors = new Dictionary<string, PaletteColor>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> light = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal bool IsLight { get; private set; }
        internal ThemePalette(ResourceDictionary resources)
        {
            Add(resources, "#11161C", "#F6F7F4");
            Add(resources, "#151B22", "#EDF0EA");
            Add(resources, "#151C23 #192129 #1A2129 #202A34", "#FFFFFF");
            Add(resources, "#1A2329", "#F0F5E8");
            Add(resources, "#1B232C #1B242C #222C36", "#FAFBF8");
            Add(resources, "#202C35", "#F0F3EB");
            Add(resources, "#172017 #22301C", "#172017");
            Add(resources, "#232C35", "#E8ECE4");
            Add(resources, "#253221 #263326 #263424 #2A382B #34432B #344334", "#E3EDD8");
            Add(resources, "#26313A", "#F0F2ED");
            Add(resources, "#29323C #2A333B #2A333C #2A333E #2B343F #2C3640 #2D3741 #303B43 #303C47 #34404A #3C4955", "#D8DFD2");
            Add(resources, "#304035 #344039", "#D6E1CB");
            Add(resources, "#302721", "#FFF2EC");
            Add(resources, "#3B4855", "#B5C0AD");
            Add(resources, "#4A3830", "#E6CFC0");
            Add(resources, "#55773A", "#BCD79C");
            Add(resources, "#597065 #788F7B #81908A #849185 #8F9F9A", "#606F61");
            Add(resources, "#597343 #738E4D", "#759454");
            Add(resources, "#778997 #7B8D9C #7C8E9D #7E909F #7F909F #7F919F #81919D #889AA9 #899AA7 #8C9AA9 #8D9DAA #93A1AF #95A2AF #98A6B3", "#607060");
            Add(resources, "#829378 #91A278 #92A784 #9AAB97 #A2B590", "#546D3B");
            Add(resources, "#9EC870 #ADCD82", "#6D943F");
            Add(resources, "#BBDA91 #CCE996 #D5F58A", "#42651F");
            Add(resources, "#A7B5C1 #A9BAC6", "#52604D");
            Add(resources, "#CCD5DE #CDD7E1 #D6E4CC #DBE3E9 #DDE5ED #ECF1F5 #EDF2F4 #EEF2F4 #F3F5F1", "#253023");
            Add(resources, "#E5B49F #E9AB89", "#91421F");
            Add(resources, "#BDA0F6", "#7850AE");
            Add(resources, "#F4B97B", "#935B1C");
            Add(resources, "#E493AE", "#A2496E");
            Add(resources, "#D5DC88", "#68721E");
            Add(resources, "#89BFE4", "#336F94");
            Add(resources, "#79C6B8", "#237969");
            resources["AccentFill"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D5F58A"));
        }
        private void Add(ResourceDictionary resources, string darkColors, string lightColor)
        {
            foreach (string dark in darkColors.Split(' '))
            {
                var color = new PaletteColor { Value = (Color)ColorConverter.ConvertFromString(dark) };
                var brush = new SolidColorBrush();
                // A binding keeps WPF's template/style optimization from freezing
                // the brush, so the same resource updates every current consumer.
                BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty, new Binding("Value") { Source = color });
                colors.Add(dark, color); brushes.Add(dark, brush); light.Add(dark, lightColor); resources["Theme" + dark.Substring(1)] = brush;
            }
        }
        internal Brush Get(string color) { return brushes[color]; }
        internal void SetLight(bool value)
        {
            IsLight = value;
            foreach (var pair in colors) pair.Value.Change((Color)ColorConverter.ConvertFromString(value ? light[pair.Key] : pair.Key));
        }
        private sealed class PaletteColor : INotifyPropertyChanged
        {
            public Color Value { get; set; }
            public event PropertyChangedEventHandler PropertyChanged;
            internal void Change(Color value) { Value = value; var changed = PropertyChanged; if (changed != null) changed(this, new PropertyChangedEventArgs("Value")); }
        }
    }
}
