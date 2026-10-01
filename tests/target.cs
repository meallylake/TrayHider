using System;
using System.Drawing;
using System.Windows.Forms;

class Target
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        string title = args.Length > 0 ? args[0] : "TrayHiderTestTarget";
        Form f = new Form();
        f.Text = title;
        f.Width = 380;
        f.Height = 170;
        Label l = new Label();
        l.Text = "test window: " + title;
        l.Dock = DockStyle.Fill;
        f.Controls.Add(l);
        Application.Run(f);
    }
}
