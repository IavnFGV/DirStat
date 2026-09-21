using System.Drawing;

namespace DiskSpaceMonitor;

internal sealed class StatusForm : Form
{
    private readonly DataGridView windows = Grid();
    private readonly DataGridView linux = Grid();
    private readonly DataGridView directories = Grid();
    private readonly Label wslStatus = new() { Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label details = new() { Dock = DockStyle.Top, Height = 48, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label vhdxDetails = new() { Dock = DockStyle.Top, Height = 26, TextAlign = ContentAlignment.MiddleLeft };

    public StatusForm()
    {
        Text = "Disk Space Monitor — состояние";
        Width = 1100; Height = 720; MinimumSize = new Size(750, 500);
        StartPosition = FormStartPosition.CenterScreen;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var winTab = new TabPage("Windows"); winTab.Controls.Add(windows);
        var wslTab = new TabPage("WSL");
        var panel = new Panel { Dock = DockStyle.Fill };
        var upper = new Panel { Dock = DockStyle.Top, Height = 260 };
        upper.Controls.Add(linux); upper.Controls.Add(vhdxDetails); upper.Controls.Add(details); upper.Controls.Add(wslStatus);
        var title = new Label { Dock = DockStyle.Top, Height = 28, Text = "Размеры каталогов (последний ручной анализ)", TextAlign = ContentAlignment.MiddleLeft };
        panel.Controls.Add(directories); panel.Controls.Add(title); panel.Controls.Add(upper);
        wslTab.Controls.Add(panel);
        tabs.TabPages.Add(winTab); tabs.TabPages.Add(wslTab);
        Controls.Add(tabs);
        windows.Columns.AddRange(Col("Диск"), Col("Всего GiB"), Col("Занято GiB"), Col("Свободно GiB"), Col("% занято"), Col("Δ GiB"), Col("Рост GiB/мин"), Col("ETA"), Col("Состояние"));
        linux.Columns.AddRange(Col("ФС"), Col("Всего GiB"), Col("Занято GiB"), Col("Доступно GiB"), Col("% занято"), Col("Рост GiB/мин"), Col("ETA"), Col("Состояние"));
        directories.Columns.AddRange(Col("Каталог"), Col("Размер GiB"), Col("Ошибка"));
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };
    private static DataGridViewTextBoxColumn Col(string name) => new() { HeaderText = name, SortMode = DataGridViewColumnSortMode.NotSortable };
    private static string Eta(TimeSpan? value) => value is null ? "—" : value.Value.TotalDays >= 1 ? $"{value.Value.TotalDays:F1} д" : $"{value.Value.TotalHours:F1} ч";

    private static void Tint(DataGridViewRow row, DangerLevel level)
    {
        row.DefaultCellStyle.BackColor = level switch { DangerLevel.Emergency => Color.LightCoral, DangerLevel.Critical => Color.MistyRose, DangerLevel.Warning => Color.LightYellow, _ => Color.White };
    }

    public void UpdateData(IEnumerable<DiskReading> drives, WslSnapshot? wsl, string status, MonitorConfig config, Trend trend, double? vhdxGiB = null)
    {
        windows.Rows.Clear();
        foreach (var disk in drives)
        {
            var row = windows.Rows[windows.Rows.Add(disk.Name, disk.TotalGiB.ToString("F1"), disk.UsedGiB.ToString("F1"), disk.FreeGiB.ToString("F1"), disk.UsedPercent.ToString("F1"), disk.Trend.DeltaGiB.ToString("+0.00;-0.00;0.00"), disk.Trend.GrowthGiBPerMinute.ToString("F2"), Eta(disk.Trend.Eta), disk.Error ?? disk.Level.ToString())];
            Tint(row, disk.Level);
        }
        wslStatus.Text = "WSL: " + status;
        vhdxDetails.Text = vhdxGiB is null ? "Файл ext4.vhdx: не найден" : $"Файл ext4.vhdx на Windows: {vhdxGiB:F1} GiB. Освобождённые в Linux блоки могут пока оставаться в этом файле.";
        linux.Rows.Clear(); directories.Rows.Clear();
        if (wsl is null) { details.Text = "Метрики отсутствуют"; return; }
        var level = Metrics.Level(wsl.AvailableGiB, config);
        var rowWsl = linux.Rows[linux.Rows.Add("/", wsl.TotalGiB.ToString("F1"), wsl.UsedGiB.ToString("F1"), wsl.AvailableGiB.ToString("F1"), wsl.UsedPercent.ToString("F1"), trend.GrowthGiBPerMinute.ToString("F2"), Eta(trend.Eta), level.ToString())];
        Tint(rowWsl, level);
        details.Text = $"Хост: {wsl.Hostname}  •  Время: {wsl.Timestamp.LocalDateTime:G}  •  Свободно всего: {wsl.FreeGiB:F1} GiB  •  Резерв: {wsl.ReservedGiB:F1} GiB\n" +
            (wsl.TopWriter is null ? "Активный процесс записи: нет данных" : $"Максимальная запись: PID {wsl.TopWriter.Pid}, {wsl.TopWriter.Command}, {wsl.TopWriter.WriteBytesPerSecond / 1048576:F2} MiB/с");
        foreach (var item in wsl.Directories) directories.Rows.Add(item.Path, item.SizeGiB.ToString("F2"), item.Error ?? "");
    }
}
