using System.Drawing.Drawing2D;

namespace Nexus.Desktop;

internal enum WorkspaceIcon { Library, Install, Hats, Formatter }

internal static class WorkspaceArt
{
    public static Color Tint(WorkspaceIcon icon) => icon switch {
        WorkspaceIcon.Library => Color.FromArgb(114,199,255),
        WorkspaceIcon.Install => Color.FromArgb(139,162,255),
        WorkspaceIcon.Hats => Color.FromArgb(185,151,255),
        _ => Color.FromArgb(134,213,227)
    };

    public static GraphicsPath Rounded(RectangleF box, float radius) {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(box.Width, box.Height));
        path.AddArc(box.Left, box.Top, d, d, 180, 90);
        path.AddArc(box.Right-d, box.Top, d, d, 270, 90);
        path.AddArc(box.Right-d, box.Bottom-d, d, d, 0, 90);
        path.AddArc(box.Left, box.Bottom-d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // Each pictogram has a 24-unit optical grid. Scaling here keeps the same stroke
    // weight in a navigation tab, empty view, and at higher monitor DPI.
    public static void Draw(Graphics graphics, RectangleF box, WorkspaceIcon icon, Color color) {
        var state = graphics.Save();
        try {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TranslateTransform(box.X,box.Y);
            graphics.ScaleTransform(box.Width/24f,box.Height/24f);
            using var pen = new Pen(color,1.65f){StartCap=LineCap.Round, EndCap=LineCap.Round, LineJoin=LineJoin.Round};
            using var solid = new SolidBrush(color);
            switch(icon) {
                case WorkspaceIcon.Library:
                    using (var card=Rounded(new(3,4,12,16),2)) graphics.DrawPath(pen,card);
                    graphics.DrawLines(pen,[new PointF(18,6),new(20,6),new(20,19),new(18,19)]);
                    graphics.DrawLine(pen,6,8,12,8);
                    graphics.FillPolygon(solid,[new PointF(7,11),new(11.5f,13.5f),new(7,16)]);
                    break;
                case WorkspaceIcon.Install:
                    graphics.DrawLine(pen,12,3,12,14);
                    graphics.DrawLines(pen,[new PointF(7.5f,10),new(12,14.5f),new(16.5f,10)]);
                    graphics.DrawLines(pen,[new PointF(3,15),new(3,20),new(21,20),new(21,15)]);
                    break;
                case WorkspaceIcon.Hats:
                    graphics.DrawPolygon(pen,[new PointF(12,2.5f),new(22,8),new(12,13.5f),new(2,8)]);
                    graphics.DrawLines(pen,[new PointF(3,12.6f),new(12,17.5f),new(21,12.6f)]);
                    graphics.DrawLines(pen,[new PointF(3,16.8f),new(12,21.6f),new(21,16.8f)]);
                    break;
                case WorkspaceIcon.Formatter:
                    graphics.DrawLines(pen,[new PointF(8,9),new(8,3),new(16,3),new(16,9)]);
                    graphics.DrawLine(pen,11,5.5f,11,7);graphics.DrawLine(pen,13.5f,5.5f,13.5f,7);
                    using (var drive=Rounded(new(5,9,14,12),2.4f)) graphics.DrawPath(pen,drive);
                    graphics.DrawLine(pen,9,17,15,17);
                    break;
            }
        } finally { graphics.Restore(state); }
    }
}

internal sealed class WorkspaceTab : Button
{
    readonly WorkspaceIcon icon;
    bool selected, hovering;
    public WorkspaceTab(string name, WorkspaceIcon kind, Action activate) {
        icon=kind;Text=name;AccessibleName=name.Replace("&",""); Dock=DockStyle.Fill;
        FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;Margin=new Padding(2,2,2,3);
        Cursor=Cursors.Hand;DoubleBuffered=true;
        Click+=(_,_)=>activate();
    }
    public bool Selected {
        get=>selected;
        set{if(selected!=value){selected=value;AccessibleDescription=value?"Current tab":"Open tab";Invalidate();AccessibilityNotifyClients(AccessibleEvents.DescriptionChange,-1);}}
    }
    protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);hovering=true;Invalidate();}
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hovering=false;Invalidate();}
    protected override void OnPaint(PaintEventArgs e) {
        var g=e.Graphics;
        var scale=DeviceDpi/96f;
        bool high=!NexusTheme.Enabled;
        g.Clear(Parent?.BackColor??SystemColors.Control);
        if(Width<4||Height<4)return;
        g.SmoothingMode=SmoothingMode.AntiAlias;
        var box=new RectangleF(1,1,Width-3,Height-3);
        using var shape=WorkspaceArt.Rounded(box,9*scale);
        var ink=high?(selected?SystemColors.HighlightText:SystemColors.ControlText):NexusTheme.Text;
        var tint=high?ink:WorkspaceArt.Tint(icon);
        var left=high?(selected?SystemColors.Highlight:SystemColors.Control):
            selected?Color.FromArgb(41,69,109):hovering?NexusTheme.Header:NexusTheme.Surface;
        var right=high?left:selected?Color.FromArgb(51,42,89):left;
        using var fill=new LinearGradientBrush(box,left,right,0f);
        using var outline=new Pen(high?SystemColors.WindowText:selected?tint:NexusTheme.Edge,scale);
        g.FillPath(fill,shape);g.DrawPath(outline,shape);
        float iconSize=Height<42*scale?21*scale:25*scale;
        float pad=Math.Max(7*scale,Math.Min(18*scale,Width*.065f));
        WorkspaceArt.Draw(g,new(pad,(Height-iconSize)/2,iconSize,iconSize),icon,Enabled?tint:SystemColors.GrayText);
        int tx=(int)(pad+iconSize+8*scale);
        var textBox=new Rectangle(tx,0,Math.Max(1,Width-tx-(int)(5*scale)),Height);
        TextRenderer.DrawText(g,Text,Font,textBox,Enabled?ink:SystemColors.GrayText,
            TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|
            (ShowKeyboardCues?0:TextFormatFlags.HidePrefix));
        if(selected) {
            using var bar=new Pen(tint,2*scale);
            g.DrawLine(bar,tx,Height-4*scale,Math.Min(Width-12*scale,tx+26*scale),Height-4*scale);
        }
        if(Focused && ShowFocusCues)ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(ClientRectangle,-5,-5),ink,left);
    }
}

internal sealed class WorkspaceHeader : Panel
{
    readonly Image logo;
    readonly Label title;
    public WorkspaceHeader(MenuStrip menu) {
        Dock=DockStyle.Fill;Margin=Padding.Empty;DoubleBuffered=true;
        var a=typeof(WorkspaceHeader).Assembly;
        using var stream=a.GetManifestResourceStream(a.GetManifestResourceNames().Single(n=>n.EndsWith("nexus.ico")))!;
        using var mark=new Icon(stream,new Size(64,64));logo=mark.ToBitmap();
        title=new Label{Text="Nexus Manager",AutoSize=true,BackColor=Color.Transparent,
            Font=new Font("Segoe UI",15,FontStyle.Bold),ForeColor=NexusTheme.Enabled?NexusTheme.Text:SystemColors.WindowText};
        Controls.Add(title);
        menu.Dock=DockStyle.None;menu.Anchor=AnchorStyles.Right|AnchorStyles.Top;menu.AutoSize=true;
        menu.LayoutStyle=ToolStripLayoutStyle.HorizontalStackWithOverflow;
        Controls.Add(menu);
        if(NexusTheme.Enabled) {BackColor=NexusTheme.Background;menu.BackColor=NexusTheme.Background;}
    }
    protected override void OnLayout(LayoutEventArgs e) {
        base.OnLayout(e);
        if(title!=null)title.Location=new Point(LogicalToDeviceUnits(53),(Height-title.Height)/2);
        foreach(var m in Controls.OfType<MenuStrip>()) {
            m.Location=new Point(Width-m.Width-LogicalToDeviceUnits(8),(Height-m.Height)/2);
        }
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        var g=e.Graphics;
        if(NexusTheme.Enabled && Width>LogicalToDeviceUnits(500)){
            var state=g.Save();
            g.SmoothingMode=SmoothingMode.AntiAlias;
            g.SetClip(new Rectangle(Width/2,0,Width/2,Height));
            for(int band=0;band<4;band++){
                float bend=Width*.64f+band*LogicalToDeviceUnits(22);
                float rise=LogicalToDeviceUnits(22);
                using var pen=new Pen(Color.FromArgb(64-band*10,NexusTheme.Accent),Math.Max(1,DeviceDpi/96f));
                g.DrawLines(pen,[new PointF(bend-2*rise,Height+2),new(bend,Height*.44f),new(bend+rise*.9f,Height*.44f),new(bend+rise*2.8f,-2)]);
            }
            g.Restore(state);
        }
        int side=Math.Max(1,Math.Min(Height-4,LogicalToDeviceUnits(35)));
        int x=LogicalToDeviceUnits(9);
        var dest=new Rectangle(x,(Height-side)/2,side,side);
        g.InterpolationMode=InterpolationMode.HighQualityBicubic;
        g.DrawImage(logo,dest);
    }
    protected override void Dispose(bool disposing){if(disposing)logo.Dispose();base.Dispose(disposing);}
}

internal sealed class WorkspaceEmpty : Control
{
    readonly WorkspaceIcon icon;
    string caption="",hint="";
    public WorkspaceEmpty(WorkspaceIcon icon) {
        this.icon=icon;Dock=DockStyle.Fill;DoubleBuffered=true;AccessibleRole=AccessibleRole.StaticText;
        BackColor=NexusTheme.Enabled?NexusTheme.Surface:SystemColors.Window;
    }
    public void SetMessage(string headline,string detail) {
        if(caption==headline && hint==detail)return;
        caption=headline;hint=detail;AccessibleName=headline;AccessibleDescription=detail;Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        if(Width<4||Height<4)return;
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
        float s=DeviceDpi/96f;
        bool compact=Height<125*s;
        float badge=compact?0:Math.Min(118*s,Height*.37f);
        var cx=Width*.5f;
        var top=Math.Max(0,(Height-(badge+59*s))/2);
        if(badge>0){
            if(NexusTheme.Enabled)DrawArtwork(g, cx, top, badge, icon);
            else WorkspaceArt.Draw(g,new(cx-badge/2,top,badge,badge),icon,SystemColors.WindowText);
        }
        int textY=(int)(top+badge+(compact?0:9*s));
        var flags=TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix;
        using var head=new Font(Font.FontFamily,12,FontStyle.Bold);
        TextRenderer.DrawText(g,caption,head,new Rectangle(8,textY,Width-16,(int)(26*s)),NexusTheme.Enabled?NexusTheme.Text:SystemColors.WindowText,flags);
        TextRenderer.DrawText(g,hint,Font,new Rectangle(8,textY+(int)(26*s),Width-16,(int)(26*s)),NexusTheme.Enabled?NexusTheme.Secondary:SystemColors.WindowText,flags);
    }

    static void DrawArtwork(Graphics g,float cx,float top,float height,WorkspaceIcon kind){
        var canvas=g.Save();
        try{
            g.TranslateTransform(cx,top);g.ScaleTransform(height/118f,height/118f);
            using var ring=new Pen(Color.FromArgb(56,NexusTheme.Accent),.8f);
            g.DrawEllipse(ring,-94,77,188,29);g.DrawEllipse(ring,-106,70,212,43);
            using var shadow=new SolidBrush(Color.FromArgb(65,5,9,22));
            g.FillEllipse(shadow,-59,79,122,21);
            if(kind==WorkspaceIcon.Library){
                // Fan three cartridges out from the same contact edge.
                for(int layer=0;layer<3;layer++){
                    var frame=g.Save();
                    g.TranslateTransform(-14+layer*15,50-layer);
                    g.RotateTransform(-23+layer*13);
                    using var shell=new GraphicsPath();
                    shell.AddPolygon([new PointF(-27,-34),new(18,-34),new(29,-23),new(29,34),new(-27,34)]);
                    var light=layer==2?Color.FromArgb(95,103,194):Color.FromArgb(56+layer*9,81+layer*10,126+layer*21);
                    using var plastic=new LinearGradientBrush(new Rectangle(-28,-35,58,70),light,Color.FromArgb(24,32,65),60f);
                    using var rim=new Pen(layer==2?NexusTheme.Violet:NexusTheme.Cyan,1.25f);
                    g.FillPath(plastic,shell);g.DrawPath(rim,shell);
                    if(layer==2){
                        using var plate=WorkspaceArt.Rounded(new(-21,-23,44,41),4);
                        using var face=new LinearGradientBrush(new Rectangle(-22,-24,46,43),Color.FromArgb(45,66,128),Color.FromArgb(59,41,112),35f);
                        g.FillPath(face,plate);
                        WorkspaceArt.Draw(g,new(-11,-16,27,27),kind,Color.FromArgb(203,222,255));
                        using var contact=new Pen(Color.FromArgb(194,177,236),2);
                        for(int i=0;i<7;i++)g.DrawLine(contact,-15+i*5,24,-15+i*5,29);
                    }
                    g.Restore(frame);
                }
            }else{
                PointF[] upper=[new(0,26),new(45,46),new(0,67),new(-45,46)];
                PointF[] left=[new(-45,46),new(0,67),new(0,94),new(-45,73)];
                PointF[] right=[new(0,67),new(45,46),new(45,73),new(0,94)];
                using var lid=new LinearGradientBrush(new Rectangle(-45,25,90,43),Color.FromArgb(76,122,194),Color.FromArgb(101,86,171),0f);
                using var side=new SolidBrush(Color.FromArgb(42,65,109));using var front=new SolidBrush(Color.FromArgb(63,52,115));
                using var edge=new Pen(Color.FromArgb(155,166,234),1.3f);
                g.FillPolygon(side,left);g.FillPolygon(front,right);g.FillPolygon(lid,upper);
                g.DrawPolygon(edge,upper);g.DrawPolygon(edge,left);g.DrawPolygon(edge,right);
                using var arrow=new Pen(NexusTheme.Cyan,3.5f){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round};
                g.DrawLine(arrow,0,7,0,40);g.DrawLines(arrow,[new PointF(-9,31),new(0,40),new(9,31)]);
            }
        }finally{g.Restore(canvas);}
    }
}

