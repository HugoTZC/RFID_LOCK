internal sealed class IdleNotice : Form
{
    private readonly Label message = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        AutoEllipsis = true,
        Padding = new Padding(8)
    };

    public IdleNotice()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(300, 68);
        BackColor = Color.FromArgb(28, 31, 38);
        ForeColor = Color.White;
        Controls.Add(message);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int NoActivate = 0x08000000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= NoActivate;
            return parameters;
        }
    }

    public void UpdateCountdown(string user, uint remainingMilliseconds)
    {
        uint seconds = (remainingMilliseconds + 999) / 1000;
        message.Text = $"{user}\nBloqueo en {seconds / 60}:{seconds % 60:00}";
        var area = (Screen.PrimaryScreen ?? Screen.FromPoint(Point.Empty)).WorkingArea;
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        if (!Visible)
            Show();
    }
}
