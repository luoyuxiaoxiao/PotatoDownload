using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase.Controls
{
    public sealed partial class UserControl1 : UserControl
    {
        private PluginData _data;
        
        public UserControl1(PluginData data)
        {
            XamlResourceLocatorFactory.PluginControlInit(ref _contentLoaded, this);
            _data = data;
        }
    }
}
