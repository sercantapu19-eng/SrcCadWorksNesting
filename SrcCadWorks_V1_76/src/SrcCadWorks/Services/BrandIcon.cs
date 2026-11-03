using System;
using System.Drawing;
using System.Windows.Forms;

namespace SrcCadWorks.Services
{
    internal static class BrandIcon
    {
        public static void Apply(Form form)
        {
            if (form == null) return;
            using (var stream = typeof(BrandIcon).Assembly.GetManifestResourceStream("SrcCadWorks.BrandIcon.ico"))
            {
                if (stream == null) return;
                using (var resourceIcon = new Icon(stream))
                {
                    var icon = (Icon)resourceIcon.Clone();
                    form.Icon = icon;
                    form.Disposed += (sender, args) => icon.Dispose();
                }
            }
        }
    }
}
