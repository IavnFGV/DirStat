using System.Drawing.Drawing2D;

namespace DiskSpaceMonitor;

/// <summary>
/// Squarified treemap layout (Bruls, Huizing, van Wijk, 2000): rows of rectangles
/// are grown while that keeps their aspect ratios closer to square.
/// </summary>
public static class TreemapLayout
{
    /// <summary>Returns one rectangle per size, in input order; areas are proportional to sizes.</summary>
    public static RectangleF[] Squarify(IReadOnlyList<double> sizes, RectangleF bounds)
    {
        var result = new RectangleF[sizes.Count];
        var order = Enumerable.Range(0, sizes.Count).Where(i => sizes[i] > 0).OrderByDescending(i => sizes[i]).ToArray();
        var total = order.Sum(i => sizes[i]);
        if (order.Length == 0 || total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;
        var scale = bounds.Width * (double)bounds.Height / total;
        double x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height;
        var start = 0;
        while (start < order.Length)
        {
            var side = Math.Min(w, h);
            if (side <= 0) break;
            var end = start;
            double rowSum = 0, best = double.PositiveInfinity;
            var largest = sizes[order[start]] * scale;
            while (end < order.Length)
            {
                var area = sizes[order[end]] * scale;
                var sum = rowSum + area;
                // Sizes are descending: the row's extremes are its first and newest items.
                var worst = Math.Max(side * side * largest / (sum * sum), sum * sum / (side * side * area));
                if (end > start && worst > best) break;
                best = worst; rowSum = sum; end++;
            }
            if (w >= h)
            {
                var strip = end == order.Length ? w : rowSum / h;
                var cursor = y;
                for (var i = start; i < end; i++)
                {
                    var height = i == end - 1 ? y + h - cursor : sizes[order[i]] * scale / strip;
                    result[order[i]] = new RectangleF((float)x, (float)cursor, (float)strip, (float)height);
                    cursor += height;
                }
                x += strip; w -= strip;
            }
            else
            {
                var strip = end == order.Length ? h : rowSum / w;
                var cursor = x;
                for (var i = start; i < end; i++)
                {
                    var width = i == end - 1 ? x + w - cursor : sizes[order[i]] * scale / strip;
                    result[order[i]] = new RectangleF((float)cursor, (float)y, (float)width, (float)strip);
                    cursor += width;
                }
                y += strip; h -= strip;
            }
            start = end;
        }
        return result;
    }
}

/// <summary>
/// Draws a DiskTreeNode as a treemap. The picture is rendered once into a bitmap
/// per root/size change; hover and selection are drawn on top of it.
/// </summary>
internal sealed class TreemapControl : Control
{
    private sealed class Item
    {
        public Item(RectangleF rect, DiskTreeNode node) { Rect = rect; Node = node; }
        public RectangleF Rect { get; }
        public DiskTreeNode Node { get; }
        public List<Item>? Children { get; set; }
    }

    private const float MinSplitSide = 4;
    private DiskTreeNode? root;
    private DiskTreeNode? hover;
    private DiskTreeNode? selected;
    private Bitmap? cache;
    private Item? layout;

    public TreemapControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(40, 40, 40);
    }

    public Func<DiskTreeNode, Color> ColorFor { get; set; } = _ => Color.SteelBlue;
    public string PlaceholderText { get; set; } = "";
    public event Action<DiskTreeNode?>? HoverChanged;
    public event Action<DiskTreeNode>? NodeClicked;
    public event Action<DiskTreeNode>? NodeDoubleClicked;
    public event Action? UpRequested;

    public DiskTreeNode? Root
    {
        get => root;
        set { root = value; hover = null; InvalidateLayout(); }
    }

    public DiskTreeNode? Selected
    {
        get => selected;
        set { if (!ReferenceEquals(selected, value)) { selected = value; Invalidate(); } }
    }

    public void InvalidateLayout()
    {
        cache?.Dispose(); cache = null; layout = null;
        Invalidate();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); InvalidateLayout(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) cache?.Dispose();
        base.Dispose(disposing);
    }

    private void Build()
    {
        if (root is null || ClientSize.Width < 2 || ClientSize.Height < 2) return;
        cache = new Bitmap(ClientSize.Width, ClientSize.Height);
        using var g = Graphics.FromImage(cache);
        g.Clear(BackColor);
        using var border = new Pen(Color.FromArgb(30, 30, 30), 2);
        layout = Lay(g, border, root, new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), 0);
    }

    private Item Lay(Graphics g, Pen border, DiskTreeNode node, RectangleF rect, int depth)
    {
        var item = new Item(rect, node);
        if (node.Children.Count == 0 || rect.Width < MinSplitSide || rect.Height < MinSplitSide)
        {
            DrawLeaf(g, rect, node);
            return item;
        }
        var rects = TreemapLayout.Squarify(node.Children.Select(c => (double)c.Size).ToArray(), rect);
        item.Children = new List<Item>(node.Children.Count);
        for (var i = 0; i < rects.Length; i++)
        {
            if (rects[i].Width * rects[i].Height < 1) continue; // invisible at this size
            item.Children.Add(Lay(g, border, node.Children[i], rects[i], depth + 1));
        }
        // Outline the immediate children of the shown root so the top-level split reads clearly.
        if (depth == 1)
        {
            g.DrawRectangle(border, rect.X, rect.Y, rect.Width, rect.Height);
            DrawLabel(g, rect, node);
        }
        return item;
    }

    private void DrawLabel(Graphics g, RectangleF rect, DiskTreeNode node)
    {
        if (rect.Width < 70 || rect.Height < 22) return;
        var text = $"{node.DisplayName}  {Units.Bytes(node.Size)}";
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var size = g.MeasureString(text, Font);
        var box = new RectangleF(rect.X + 3, rect.Y + 3, Math.Min(size.Width + 6, rect.Width - 6), size.Height + 2);
        using var background = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        g.FillRectangle(background, box);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(text, Font, Brushes.White, new RectangleF(box.X + 3, box.Y + 1, box.Width - 4, box.Height), format);
    }

    private void DrawLeaf(Graphics g, RectangleF rect, DiskTreeNode node)
    {
        var color = ColorFor(node.IsDirectory ? node.DominantLeaf() : node);
        if (rect.Width >= 3 && rect.Height >= 3)
        {
            // Light top-left to dark bottom-right: a cheap "cushion" that separates neighbours.
            using var brush = new LinearGradientBrush(rect, Shade(color, 1.35), Shade(color, 0.6), 45f);
            g.FillRectangle(brush, rect);
            using var edge = new Pen(Color.FromArgb(90, 0, 0, 0));
            g.DrawRectangle(edge, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        }
        else
        {
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, rect);
        }
    }

    private static Color Shade(Color color, double factor) => Color.FromArgb(
        Math.Clamp((int)(color.R * factor), 0, 255), Math.Clamp((int)(color.G * factor), 0, 255), Math.Clamp((int)(color.B * factor), 0, 255));

    protected override void OnPaint(PaintEventArgs e)
    {
        if (cache is null) Build();
        if (cache is null)
        {
            e.Graphics.Clear(BackColor);
            TextRenderer.DrawText(e.Graphics, PlaceholderText, Font, ClientRectangle, Color.Gainsboro,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        e.Graphics.DrawImageUnscaled(cache, 0, 0);
        if (selected is not null && Find(selected) is { } chosen)
        {
            using var outer = new Pen(Color.Black, 4);
            using var inner = new Pen(Color.Yellow, 2);
            var r = Rectangle.Round(chosen.Rect);
            e.Graphics.DrawRectangle(outer, r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
            e.Graphics.DrawRectangle(inner, r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
        }
        if (hover is not null && Find(hover) is { } hovered)
        {
            using var pen = new Pen(Color.White, 2);
            var r = Rectangle.Round(hovered.Rect);
            e.Graphics.DrawRectangle(pen, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
        }
    }

    /// <summary>The drawn item for a node, or its nearest drawn ancestor if it is too small to see.</summary>
    private Item? Find(DiskTreeNode node)
    {
        if (layout is null) return null;
        var chain = node.PathFromRoot().ToList();
        var start = chain.FindIndex(n => ReferenceEquals(n, layout.Node));
        if (start < 0) return null;
        var item = layout;
        for (var i = start + 1; i < chain.Count && item.Children is not null; i++)
        {
            var next = item.Children.FirstOrDefault(c => ReferenceEquals(c.Node, chain[i]));
            if (next is null) break;
            item = next;
        }
        return item;
    }

    private DiskTreeNode? Hit(Point point)
    {
        if (layout is null || !layout.Rect.Contains(point)) return null;
        var item = layout;
        while (item.Children is not null)
        {
            var next = item.Children.FirstOrDefault(c => c.Rect.Contains(point));
            if (next is null) break;
            item = next;
        }
        return item.Node;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var node = Hit(e.Location);
        if (ReferenceEquals(node, hover)) return;
        hover = node;
        Invalidate();
        HoverChanged?.Invoke(node);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hover is null) return;
        hover = null;
        Invalidate();
        HoverChanged?.Invoke(null);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Right) { UpRequested?.Invoke(); return; }
        if (e.Button == MouseButtons.Left && Hit(e.Location) is { } node) NodeClicked?.Invoke(node);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && Hit(e.Location) is { } node) NodeDoubleClicked?.Invoke(node);
    }
}
